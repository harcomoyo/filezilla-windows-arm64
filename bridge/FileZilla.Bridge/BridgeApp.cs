using System.Globalization;
using System.Text.Json;

namespace FileZilla.Bridge;

internal static class BridgeApp
{
    internal static async Task<int> RunAsync(string[] arguments, Stream input, Stream output, TextWriter diagnostics, NativeClient native)
    {
        try
        {
            var args = arguments.ToList();
            int? pid = null;
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] != "--pid") continue;
                if (pid.HasValue || i + 1 == args.Count || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
                    throw new BridgeException("--pid requires one positive 32-bit process ID.");
                pid = parsed;
                args.RemoveRange(i, 2);
                i--;
            }
            if (args.Count == 0 || args.SequenceEqual(new[] { "mcp" }))
                return await McpServer.RunAsync(input, output, diagnostics, native, pid);
            if (args.SequenceEqual(new[] { "--help" }))
            {
                await diagnostics.WriteLineAsync("filezilla-bridge [--pid PID] mcp | sessions list | files list --session ID [--offset N] [--limit N] | refresh --session ID | queue summary | info | cwd --session S --path P | download --session S --path REL [--local-path REL] | upload --session S --path REL | jobs status [--job J] | fs prepare --session S --op mkdir|rename|delete [--path REL | --from REL --to REL] | fs commit --plan ID\nREL is a relative POSIX path under the site's root. cwd also accepts an absolute POSIX path under the site root. Native validates root containment.");
                return 0;
            }
            string method;
            int skip;
            if (args.Count >= 2 && args[0] == "sessions" && args[1] == "list") { method = "sessions.list"; skip = 2; }
            else if (args.Count >= 2 && args[0] == "files" && args[1] == "list") { method = "sessions.files"; skip = 2; }
            else if (args.Count >= 2 && args[0] == "queue" && args[1] == "summary") { method = "queue.summary"; skip = 2; }
            else if (args[0] == "refresh") { method = "sessions.refresh"; skip = 1; }
            else if (args[0] == "info") { method = "api.info"; skip = 1; }
            else if (args[0] == "cwd") { method = "sessions.cwd"; skip = 1; }
            else if (args[0] == "download") { method = "transfers.download"; skip = 1; }
            else if (args[0] == "upload") { method = "transfers.upload"; skip = 1; }
            else if (args.Count >= 2 && args[0] == "jobs" && args[1] == "status") { method = "jobs.status"; skip = 2; }
            else if (args.Count >= 2 && args[0] == "fs" && args[1] == "prepare") { method = "fs.prepare"; skip = 2; }
            else if (args.Count >= 2 && args[0] == "fs" && args[1] == "commit") { method = "fs.commit"; skip = 2; }
            else throw new BridgeException("Unknown command. Use --help for usage.");
            var values = new Dictionary<string, object>();
            for (int i = skip; i < args.Count; i += 2)
            {
                string option = args[i];
                if (i + 1 >= args.Count || option is not ("--session" or "--offset" or "--limit" or "--path" or "--local-path" or
                    "--job" or "--op" or "--from" or "--to" or "--plan"))
                    throw new BridgeException("Unknown, repeated, or incomplete option.");
                string key = option == "--plan" ? "plan_id" : option[2..].Replace('-', '_');
                if (values.ContainsKey(key)) throw new BridgeException("Unknown, repeated, or incomplete option.");
                if (option is not ("--offset" or "--limit")) values[key] = args[i + 1];
                else
                {
                    if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                        throw new BridgeException($"{option} requires a non-negative 32-bit integer.");
                    values[option[2..]] = number;
                }
            }
            JsonElement parameters = Json.Element(values);
            Tools.Validate(method, parameters);
            JsonElement result = await native.CallAsync(pid, method, parameters);
            await Json.WriteAsync(output, result, Json.MaxResponse);
            return 0;
        }
        catch (Exception ex) when (ex is BridgeException or IOException)
        {
            await diagnostics.WriteLineAsync(ex.Message);
            return 1;
        }
    }
}
