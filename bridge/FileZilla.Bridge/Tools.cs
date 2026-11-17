using System.Text.Json;

namespace FileZilla.Bridge;

internal static class Tools
{
    internal static readonly (string Name, string Method, string Description, bool ReadOnly, bool Destructive)[] Definitions =
    [
        ("sessions_list", "sessions.list", "List FileZilla sessions with opaque session IDs.", true, false),
        ("sessions_files", "sessions.files", "Read a session's cached remote directory listing, with optional pagination.", true, false),
        ("sessions_refresh", "sessions.refresh", "Request a non-destructive refresh of a session's remote listing.", false, false),
        ("queue_summary", "queue.summary", "Read the transfer queue summary.", true, false),
        ("sessions_cwd", "sessions.cwd", "Change the remote directory using a site-relative POSIX path or an absolute POSIX path under the site root. site must match the session's current site.", false, false),
        ("transfers_download", "transfers.download", "Download a site-relative POSIX path into the site's local mirror root, overwriting an existing local file. site must match the session's current site; optional local_path is relative to that local root and defaults to path.", false, false),
        ("transfers_upload", "transfers.upload", "Upload a relative POSIX path from the site's local mirror root to the matching path under its remote root. site must match the session's current site. May overwrite remote data.", false, true),
        ("jobs_status", "jobs.status", "Read API job metadata and the transfer queue summary, optionally for one job.", true, false),
        ("fs_prepare", "fs.prepare", "Prepare an expiring mkdir, rename, or delete plan using relative POSIX paths under the site's remote root, without executing it. site must match the session's current site.", false, false),
        ("fs_commit", "fs.commit", "Execute a previously prepared remote filesystem plan. May rename or delete remote data.", false, true)
    ];

    internal static object[] List() => Definitions.Select(tool => (object)new
    {
        name = tool.Name,
        description = tool.Description,
        inputSchema = Schema(tool.Method),
        annotations = new { readOnlyHint = tool.ReadOnly, destructiveHint = tool.Destructive, openWorldHint = true }
    }).ToArray();

    private static object Schema(string method)
    {
        var properties = new Dictionary<string, object>();
        foreach (string field in Fields(method))
            properties[field] = new { type = "string", minLength = 1 };
        if (method == "sessions.files")
        {
            properties["offset"] = new { type = "integer", minimum = 0, maximum = int.MaxValue };
            properties["limit"] = new { type = "integer", minimum = 1, maximum = 1000 };
        }
        foreach (string field in new[] { "path", "local_path", "from", "to" })
            if (properties.ContainsKey(field)) properties[field] = new
            {
                type = "string", minLength = 1,
                pattern = (method == "sessions.cwd" ? "^" : "^(?!/)") + @"(?!.*(?:^|/)\.{1,2}(?:/|$))[^\\:\x00-\x1F\x7F-\x9F]+(?![\s\S])"
            };
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object", ["properties"] = properties, ["required"] = Required(method), ["additionalProperties"] = false
        };
        if (method == "fs.prepare")
        {
            properties["op"] = new { type = "string", @enum = new[] { "mkdir", "rename", "delete" } };
            schema["oneOf"] = new object[]
            {
                new { properties = new { op = new { @enum = new[] { "mkdir", "delete" } } }, required = new[] { "path" },
                    not = new { anyOf = new[] { new { required = new[] { "from" } }, new { required = new[] { "to" } } } } },
                new { properties = new { op = new { @enum = new[] { "rename" } } }, required = new[] { "from", "to" },
                    not = new { required = new[] { "path" } } }
            };
        }
        return schema;
    }

    private static string[] Fields(string method) => method switch
    {
        "sessions.files" => ["session", "offset", "limit"],
        "sessions.refresh" => ["session"],
        "sessions.cwd" => ["session", "site", "path"],
        "transfers.download" => ["session", "site", "path", "local_path"],
        "transfers.upload" => ["session", "site", "path"],
        "jobs.status" => ["job"],
        "fs.prepare" => ["session", "site", "op", "path", "from", "to"],
        "fs.commit" => ["plan_id"],
        "sessions.list" or "queue.summary" or "api.info" => [],
        _ => throw new BridgeException("Unsupported native method.")
    };

    private static string[] Required(string method) => method switch
    {
        "sessions.files" or "sessions.refresh" => ["session"],
        "sessions.cwd" => ["session", "site", "path"],
        "transfers.download" or "transfers.upload" => ["session", "site", "path"],
        "fs.prepare" => ["session", "site", "op"],
        "fs.commit" => ["plan_id"],
        _ => []
    };

    internal static void Validate(string method, JsonElement args)
    {
        string[] allowed = Fields(method);
        if (!Json.HasOnly(args, allowed)) throw new BridgeException("Arguments must be an object with only the documented properties.");
        foreach (string field in Required(method))
            if (!args.TryGetProperty(field, out _)) throw new BridgeException($"{field} is required.");
        foreach (var property in args.EnumerateObject())
            if (property.Name is not ("offset" or "limit") && (property.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(property.Value.GetString()) || property.Value.GetString()!.Any(char.IsControl)))
                throw new BridgeException($"{property.Name} must be a non-empty string without control characters.");
        foreach (string name in new[] { "offset", "limit" })
        {
            int minimum = name == "offset" ? 0 : 1;
            int maximum = name == "offset" ? int.MaxValue : 1000;
            if (args.TryGetProperty(name, out var value) && (!value.TryGetInt32Safe(out int number) || number < minimum || number > maximum))
                throw new BridgeException($"{name} must be a 32-bit integer between {minimum} and {maximum}.");
        }
        if (method == "fs.prepare")
        {
            string op = args.GetProperty("op").GetString()!;
            if (op is not ("mkdir" or "rename" or "delete")) throw new BridgeException("op must be mkdir, rename, or delete.");
            string[] fields = op == "rename" ? ["from", "to"] : ["path"];
            if (fields.Any(field => !args.TryGetProperty(field, out _)) ||
                (op == "rename" ? args.TryGetProperty("path", out _) : args.TryGetProperty("from", out _) || args.TryGetProperty("to", out _)))
                throw new BridgeException("rename requires only from and to; mkdir/delete require only path.");
        }
        foreach (string field in new[] { "path", "local_path", "from", "to" })
        {
            if (!args.TryGetProperty(field, out var value)) continue;
            string path = value.GetString()!;
            if ((method != "sessions.cwd" && path.StartsWith('/')) || path.IndexOfAny(['\\', ':']) >= 0 ||
                path.Split('/').Any(segment => segment is "." or ".."))
                throw new BridgeException($"{field} must be a POSIX path without host syntax or dot segments; only cwd accepts an absolute path.");
        }
    }
}
