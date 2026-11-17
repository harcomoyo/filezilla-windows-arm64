using System.Text.Json;

namespace FileZilla.Bridge;

internal static class Tools
{
    internal static readonly (string Name, string Method, string Description, bool ReadOnly)[] Definitions =
    [
        ("sessions_list", "sessions.list", "List FileZilla sessions with opaque session IDs.", true),
        ("sessions_files", "sessions.files", "Read a session's cached remote directory listing, with optional pagination.", true),
        ("sessions_refresh", "sessions.refresh", "Request a non-destructive refresh of a session's remote listing.", false),
        ("queue_summary", "queue.summary", "Read the transfer queue summary.", true)
    ];

    internal static object[] List() => Definitions.Select(tool => (object)new
    {
        name = tool.Name,
        description = tool.Description,
        inputSchema = Schema(tool.Method),
        annotations = new { readOnlyHint = tool.ReadOnly, destructiveHint = false, openWorldHint = true }
    }).ToArray();

    private static object Schema(string method)
    {
        var properties = new Dictionary<string, object>();
        bool session = method is "sessions.files" or "sessions.refresh";
        if (session) properties["session"] = new { type = "string", minLength = 1 };
        if (method == "sessions.files")
        {
            properties["offset"] = new { type = "integer", minimum = 0, maximum = int.MaxValue };
            properties["limit"] = new { type = "integer", minimum = 1, maximum = 1000 };
        }
        return new { type = "object", properties, required = session ? new[] { "session" } : [], additionalProperties = false };
    }

    internal static void Validate(string method, JsonElement args)
    {
        string[] allowed = method switch
        {
            "sessions.files" => ["session", "offset", "limit"],
            "sessions.refresh" => ["session"],
            "sessions.list" or "queue.summary" or "api.info" => [],
            _ => throw new BridgeException("Unsupported native method.")
        };
        if (!Json.HasOnly(args, allowed)) throw new BridgeException("Arguments must be an object with only the documented properties.");
        if (allowed.Contains("session") && (!args.TryGetProperty("session", out var session) ||
            session.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(session.GetString())))
            throw new BridgeException("session must be a non-empty opaque string.");
        foreach (string name in new[] { "offset", "limit" })
        {
            int minimum = name == "offset" ? 0 : 1;
            int maximum = name == "offset" ? int.MaxValue : 1000;
            if (args.TryGetProperty(name, out var value) && (!value.TryGetInt32Safe(out int number) || number < minimum || number > maximum))
                throw new BridgeException($"{name} must be a 32-bit integer between {minimum} and {maximum}.");
        }
    }
}
