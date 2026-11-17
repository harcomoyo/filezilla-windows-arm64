using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FileZilla.Bridge;

return await Tests.RunAsync();

internal static class Tests
{
    private static int passed;
    private static int failed;
    private static readonly int Pid = Environment.ProcessId;
    private static readonly JsonElement Empty = Json.Element(new { });

    internal static async Task<int> RunAsync()
    {
        await Test("CLI commands use exact native methods and arguments", async () =>
        {
            foreach (var (command, method) in new[]
            {
                (new[] { "sessions", "list" }, "sessions.list"),
                (new[] { "files", "list", "--session", "opaque:not-a-pointer", "--offset", "3", "--limit", "7" }, "sessions.files"),
                (new[] { "refresh", "--session", "opaque:not-a-pointer" }, "sessions.refresh"),
                (new[] { "queue", "summary" }, "queue.summary"),
                (new[] { "info" }, "api.info")
            })
            {
                await using var mock = new MockPipe(request =>
                {
                    Equal(method, request.GetProperty("method").GetString());
                    Equal("2.0", request.GetProperty("jsonrpc").GetString());
                    Equal(1, request.GetProperty("id").GetInt32());
                    if (method is "sessions.files" or "sessions.refresh")
                        Equal("opaque:not-a-pointer", request.GetProperty("params").GetProperty("session").GetString());
                    if (method == "sessions.files")
                    {
                        Equal(3, request.GetProperty("params").GetProperty("offset").GetInt32());
                        Equal(7, request.GetProperty("params").GetProperty("limit").GetInt32());
                    }
                    return Success(new { ok = true });
                });
                var result = await Run(command.Concat(new[] { "--pid", Pid.ToString() }).ToArray(), mock.Client());
                Equal(0, result.Code);
                Equal("", result.Error);
                using var json = JsonDocument.Parse(result.Output);
                Check(json.RootElement.GetProperty("ok").GetBoolean(), "CLI result");
                await mock.Completion;
            }
        });
        await Test("CLI rejects unsafe, repeated, missing and invalid arguments", async () =>
        {
            foreach (string[] args in new[]
            {
                new[] { "files", "list" }, new[] { "refresh", "--session", "" },
                new[] { "sessions", "list", "--session", "x" },
                new[] { "files", "list", "--session", "x", "--limit", "0" },
                new[] { "files", "list", "--session", "x", "--offset", "-1" },
                new[] { "files", "list", "--session", "x", "--offset", "2147483648" },
                new[] { "refresh", "--session", "x", "--session", "y" },
                new[] { "upload" }, new[] { "info", "--password", "secret" },
                new[] { "--pid", "0", "info" }, new[] { "--pid", "x", "info" },
                new[] { "--pid", "1", "info", "--pid", "2" }, new[] { "info", "--pid" }
            })
            {
                var result = await Run(args, NoServer());
                Equal(1, result.Code);
                Equal("", result.Output);
                Check(result.Error.Length > 0, "diagnostic expected");
            }
        });
        await Test("MCP negotiates both supported versions and safe fallback", async () =>
        {
            foreach (string version in new[] { "2024-11-05", "2025-03-26", "2099-01-01" })
            {
                var result = await Run(["mcp"], NoServer(), Initialize(version) + Request(2, "ping") + Ready + Request(3, "tools/list"));
                Equal(0, result.Code);
                Equal("", result.Error);
                var frames = ParseFrames(result.Output);
                Equal(3, frames.Length);
                Equal(version == "2099-01-01" ? "2025-03-26" : version, frames[0].GetProperty("result").GetProperty("protocolVersion").GetString());
                Equal(0, frames[1].GetProperty("result").EnumerateObject().Count());
                var tools = frames[2].GetProperty("result").GetProperty("tools");
                Equal(4, tools.GetArrayLength());
                foreach (var tool in tools.EnumerateArray())
                {
                    Check(!tool.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean(), "strict tool schema");
                    Equal(tool.GetProperty("name").GetString() != "sessions_refresh", tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
                    Check(!tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean(), "non-destructive tools");
                }
            }
        });
        await Test("MCP accepts params metadata without forwarding it to native", async () =>
        {
            foreach (var tool in Tools.Definitions)
            {
                await using var mock = new MockPipe(request =>
                {
                    Equal(tool.Method, request.GetProperty("method").GetString());
                    var args = request.GetProperty("params");
                    Check(!args.TryGetProperty("_meta", out _), "MCP metadata must not reach native params");
                    Check(Json.HasOnly(args, tool.Method is "sessions.files" or "sessions.refresh" ? ["session"] : []), "native arguments unchanged");
                    return Success(new { });
                });
                object metadata = new { progressToken = "progress-1", custom = new { value = 42 } };
                object arguments = tool.Method is "sessions.files" or "sessions.refresh" ? new { session = "session-1" } : new { };
                string input = Request(1, "initialize", new { protocolVersion = "2025-03-26", capabilities = new { },
                    clientInfo = new { name = "test", version = "1" }, _meta = metadata }) +
                    "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{\"_meta\":{}}}\n" +
                    Request(2, "ping", new { _meta = metadata }) + Request(3, "tools/list", new { _meta = new { } }) +
                    Request(4, "tools/call", new { name = tool.Name, arguments, _meta = metadata });
                var result = await Run(["mcp", "--pid", Pid.ToString()], mock.Client(), input);
                Equal(0, result.Code);
                Equal("", result.Error);
                var frames = ParseFrames(result.Output);
                Equal(4, frames.Length);
                Equal("2025-03-26", frames[0].GetProperty("result").GetProperty("protocolVersion").GetString());
                Equal(0, frames[1].GetProperty("result").EnumerateObject().Count());
                Equal(4, frames[2].GetProperty("result").GetProperty("tools").GetArrayLength());
                Check(!frames[3].GetProperty("result").GetProperty("isError").GetBoolean(), "tool success with metadata");
                await mock.Completion;
            }
        });
        await Test("MCP rejects invalid metadata and duplicate params without relaxing tool arguments", async () =>
        {
            foreach (var (method, properties) in new[]
            {
                ("initialize", "\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"},"),
                ("ping", ""), ("tools/list", ""), ("tools/call", "\"name\":\"sessions_list\",\"arguments\":{},")
            })
            {
                foreach (string metadata in new[] { "null", "[]", "\"text\"", "1", "true", "{},\"_meta\":{}" })
                {
                    using var args = JsonDocument.Parse("{" + properties + "\"_meta\":" + metadata + "}");
                    string input = (method == "initialize" ? "" : Initialize() + Ready) + Request(9, method, args.RootElement);
                    var result = await Run(["mcp"], NoServer(), input);
                    Equal(-32602, Code(ParseFrames(result.Output)[^1]));
                }
            }
            string[] invalidCalls =
            [
                Request(1, "tools/call", new { name = "sessions_list", arguments = new { _meta = new { } }, _meta = new { } }),
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"sessions_list\",\"name\":\"sessions_list\",\"_meta\":{}}}\n",
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"sessions_refresh\",\"arguments\":{\"session\":\"x\",\"session\":\"y\"},\"_meta\":{}}}\n"
            ];
            var frames = ParseFrames((await Run(["mcp"], NoServer(), Initialize() + Ready + string.Concat(invalidCalls))).Output);
            Equal(4, frames.Length);
            foreach (var frame in frames.Skip(1)) Equal(-32602, Code(frame));
        });
        await Test("sessions.files schema CLI and MCP enforce native limit cap", async () =>
        {
            var tools = Json.Element(Tools.List());
            var schema = tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sessions_files").GetProperty("inputSchema");
            Equal(1000, schema.GetProperty("properties").GetProperty("limit").GetProperty("maximum").GetInt32());
            Equal(int.MaxValue, schema.GetProperty("properties").GetProperty("offset").GetProperty("maximum").GetInt32());
            foreach (bool mcp in new[] { false, true })
            {
                await using var mock = new MockPipe(request =>
                {
                    Equal(1000, request.GetProperty("params").GetProperty("limit").GetInt32());
                    return Success(new { });
                });
                string input = Initialize() + Ready + Request(2, "tools/call", new { name = "sessions_files", arguments = new { session = "x", limit = 1000 } });
                var accepted = await Run(mcp ? ["mcp", "--pid", Pid.ToString()] :
                    ["files", "list", "--session", "x", "--limit", "1000", "--pid", Pid.ToString()], mock.Client(), mcp ? input : "");
                Equal(0, accepted.Code);
                if (mcp) Check(!ParseFrames(accepted.Output)[1].GetProperty("result").GetProperty("isError").GetBoolean(), "MCP accepts limit 1000");
                await mock.Completion;
                foreach (int limit in new[] { 1001, int.MaxValue })
                {
                    var rejected = await Run(mcp ? ["mcp"] : ["files", "list", "--session", "x", "--limit", limit.ToString()], NoServer(),
                        mcp ? Initialize() + Ready + Request(3, "tools/call", new { name = "sessions_files", arguments = new { session = "x", limit } }) : "");
                    if (mcp) Equal(-32602, Code(ParseFrames(rejected.Output)[1]));
                    else
                    {
                        Equal(1, rejected.Code);
                        Equal("", rejected.Output);
                        Check(rejected.Error.Contains("1000"), "CLI reports limit cap");
                    }
                }
            }
        });
        await Test("MCP all four tools forward native results", async () =>
        {
            foreach (var tool in Tools.Definitions)
            {
                await using var mock = new MockPipe(request =>
                {
                    Equal(tool.Method, request.GetProperty("method").GetString());
                    return Success(new { marker = "native-result" });
                });
                object args = tool.Method is "sessions.files" or "sessions.refresh" ? new { session = "session-1" } : new { };
                string input = Initialize() + Ready + Request("tool-id", "tools/call", new { name = tool.Name, arguments = args });
                var result = await Run(["--pid", Pid.ToString(), "mcp"], mock.Client(), input);
                var response = ParseFrames(result.Output)[1];
                Equal("tool-id", response.GetProperty("id").GetString());
                Check(!response.GetProperty("result").GetProperty("isError").GetBoolean(), "tool success");
                using var text = JsonDocument.Parse(response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
                Equal("native-result", text.RootElement.GetProperty("marker").GetString());
                await mock.Completion;
            }
        });
        await Test("MCP lifecycle, notifications, IDs and method errors", async () =>
        {
            string input = Request(1, "tools/list") + Request(2, "ping") + Initialize() +
                Request(3, "tools/list") + Ready + Request(4, "not/a/method") + Initialize() +
                "{\"jsonrpc\":\"2.0\",\"method\":\"unknown-notification\"}\n" +
                "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"ping\"}\n";
            var frames = ParseFrames((await Run(["mcp"], NoServer(), input)).Output);
            Equal(7, frames.Length);
            Equal(-32002, Code(frames[0]));
            Equal(-32002, Code(frames[3]));
            Equal(-32601, Code(frames[4]));
            Equal(-32600, Code(frames[5]));
            Equal(JsonValueKind.Null, frames[6].GetProperty("id").ValueKind);
        });
        await Test("MCP strict tool validation rejects dangerous and malformed arguments", async () =>
        {
            string[] calls =
            [
                Request(1, "tools/call", new { name = "upload", arguments = new { } }),
                Request(2, "tools/call", new { name = "sessions_refresh", arguments = new { session = 1 } }),
                Request(3, "tools/call", new { name = "sessions_files", arguments = new { session = "x", limit = 0 } }),
                Request(4, "tools/call", new { name = "sessions_files", arguments = new { session = "x", offset = 0.5 } }),
                Request(5, "tools/call", new { name = "sessions_list", arguments = new { credentials = "forbidden" } }),
                Request(6, "tools/call", new { name = "sessions_list", arguments = new object[] { } }),
                Request(7, "tools/call", new { name = "sessions_refresh" }),
                Request(8, "tools/call", new { name = "sessions_refresh", arguments = new { session = "x" }, shell = "forbidden" })
            ];
            var frames = ParseFrames((await Run(["mcp"], NoServer(), Initialize() + Ready + string.Concat(calls))).Output);
            Equal(calls.Length + 1, frames.Length);
            foreach (var frame in frames.Skip(1)) Equal(-32602, Code(frame));
        });
        await Test("MCP native failures are tool errors, not transport errors", async () =>
        {
            await using var mock = new MockPipe(_ => "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32001,\"message\":\"Busy\"}}\n");
            string input = Initialize() + Ready + Request(2, "tools/call", new { name = "queue_summary" });
            var response = ParseFrames((await Run(["mcp", "--pid", Pid.ToString()], mock.Client(), input)).Output)[1];
            Check(response.GetProperty("result").GetProperty("isError").GetBoolean(), "isError expected");
            Check(response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!.Contains("-32001: Busy"), "native error preserved");
            await mock.Completion;
        });
        await Test("MCP recovers after malformed JSON, envelope, UTF-8 and oversized frame", async () =>
        {
            string input = "{bad\n[]\n{\"jsonrpc\":\"2.0\",\"id\":[],\"method\":\"ping\"}\n" +
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"method\":\"ping\"}\n" +
                new string('x', Json.MaxRequest + 1) + "\n" + Request(9, "ping");
            var frames = ParseFrames((await Run(["mcp"], NoServer(), input)).Output);
            Equal(6, frames.Length);
            Equal(-32700, Code(frames[0]));
            Equal(-32600, Code(frames[1]));
            Equal(-32600, Code(frames[2]));
            Equal(-32600, Code(frames[3]));
            Equal(-32700, Code(frames[4]));
            Equal(9, frames[5].GetProperty("id").GetInt32());
            byte[] invalid = [0xff, 10, .. Encoding.UTF8.GetBytes(Request(10, "ping"))];
            using var source = new MemoryStream(invalid);
            using var output = new MemoryStream();
            Equal(0, await BridgeApp.RunAsync(["mcp"], source, output, new StringWriter(), NoServer()));
            frames = ParseFrames(Encoding.UTF8.GetString(output.ToArray()));
            Equal(-32700, Code(frames[0]));
            Equal(10, frames[1].GetProperty("id").GetInt32());
        });
        await Test("Frame reader exact bounds, multibyte, CRLF and partial EOF", async () =>
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes("1234\n12345\nx\r\n"));
            var reader = new FrameReader(input, 4);
            Equal("1234", await reader.ReadAsync());
            await Throws<FrameException>(() => reader.ReadAsync());
            Equal("x\r", await reader.ReadAsync());
            Equal<string?>(null, await reader.ReadAsync());
            using var utf8 = new MemoryStream(Encoding.UTF8.GetBytes("\u00e9\u00e9\n"));
            Equal("\u00e9\u00e9", await new FrameReader(utf8, 4).ReadAsync());
            using var partial = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
            await Throws<FrameException>(() => new FrameReader(partial, 4).ReadAsync());
            using var bytes = new MemoryStream();
            await Throws<BridgeException>(() => Json.WriteAsync(bytes, new string('x', Json.MaxRequest), Json.MaxRequest));
            Equal(0L, bytes.Length);
        });
        await Test("Native malformed and oversized responses fail closed", async () =>
        {
            foreach (string response in new[]
            {
                "{bad\n", "{}\n", "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{}}\n",
                "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{}}\n",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{},\"error\":{}}\n",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":\"bad\",\"message\":3}}\n",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"id\":1,\"result\":{}}\n",
                new string('x', Json.MaxResponse + 1) + "\n", "{}", ""
            })
            {
                await using var mock = new MockPipe(_ => response);
                var result = await Run(["info", "--pid", Pid.ToString()], mock.Client());
                Equal(1, result.Code);
                Equal("", result.Output);
                await mock.Completion;
            }
        });
        await Test("Native response exact 1 MiB accepted", async () =>
        {
            string response = Success("");
            response = Success(new string('x', Json.MaxResponse - (Encoding.UTF8.GetByteCount(response) - 1)));
            Equal(Json.MaxResponse + 1, Encoding.UTF8.GetByteCount(response));
            await using var mock = new MockPipe(_ => response);
            var value = await mock.Client().CallAsync(Pid, "api.info", Empty);
            Check(value.GetString()!.Length > 1_000_000, "large valid result");
            await mock.Completion;
        });
        await Test("Native requests enforce exact 64 KiB bound before writing", async () =>
        {
            int overhead = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = 1, method = "sessions.files", @params = new { session = "" } }).Length;
            await using (var mock = new MockPipe(request =>
            {
                Equal(Json.MaxRequest, Encoding.UTF8.GetByteCount(request.GetRawText()));
                return Success(new { });
            }))
            {
                await mock.Client().CallAsync(Pid, "sessions.files", Json.Element(new { session = new string('x', Json.MaxRequest - overhead) }));
                await mock.Completion;
            }
            await using (var mock = new MockPipe(_ => throw new Exception("Oversized request must not reach server")))
            {
                await Throws<BridgeException>(() => mock.Client().CallAsync(Pid, "sessions.files",
                    Json.Element(new { session = new string('x', Json.MaxRequest - overhead + 1) })), "exceeds");
                await mock.Completion;
            }
        });
        await Test("MCP bounds wrapped response and remains usable", async () =>
        {
            await using var mock = new MockPipe(_ => Success(new string('x', Json.MaxResponse - 60)));
            var result = await Run(["mcp", "--pid", Pid.ToString()], mock.Client(),
                Initialize() + Ready + Request(2, "tools/call", new { name = "queue_summary" }) + Request(3, "ping"));
            var frames = ParseFrames(result.Output);
            Equal(3, frames.Length);
            Equal(-32603, Code(frames[1]));
            Equal(3, frames[2].GetProperty("id").GetInt32());
            Check(result.Error.Contains("exceeds"), "oversized response diagnostic");
            await mock.Completion;
        });
        await Test("Discovery no server, single server, duplicate candidate and ambiguity", async () =>
        {
            await Throws<BridgeException>(() => NoServer().CallAsync(null, "api.info", Empty), "No verified");
            await using (var mock = new MockPipe(_ => Success(new { discovered = true })))
            {
                var value = await mock.Client(() => [Pid, Pid]).CallAsync(null, "api.info", Empty);
                Check(value.GetProperty("discovered").GetBoolean(), "single discovered");
                await mock.Completion;
            }
            await using var first = new MockPipe(_ => Success(new { }));
            await using var second = new MockPipe(_ => Success(new { }));
            // Logical discovery candidates map only to isolated mock names; actual server PID is still checked.
            var client = new NativeClient(() => [101, 102], id => id == 101 ? first.Name : second.Name,
                (pipe, _) => NativeClient.VerifyPid(pipe, Pid));
            await Throws<BridgeException>(() => client.CallAsync(null, "api.info", Empty), "Multiple live");
            await first.Completion;
            await second.Completion;
        });
        await Test("Explicit PID bypasses discovery and nonexistent pipe times out", async () =>
        {
            await using var mock = new MockPipe(_ => Success(new { }));
            var client = mock.Client(() => throw new Exception("Discovery must not run"));
            await client.CallAsync(Pid, "api.info", Empty);
            await mock.Completion;
            var absent = new NativeClient(() => [], _ => "filezilla-bridge-absent-" + Guid.NewGuid().ToString("N"));
            await Throws<BridgeException>(() => absent.CallAsync(Pid, "api.info", Empty));
        });
        await Test("Windows pipe PID mismatch and non-filezilla executable rejected", async () =>
        {
            if (!OperatingSystem.IsWindows()) throw new Exception("Windows verification test requires Windows");
            await using (var mock = new MockPipe(_ => Success(new { })))
            {
                var client = new NativeClient(() => [], _ => mock.Name, NativeClient.VerifyPid);
                await Throws<BridgeException>(() => client.CallAsync(Pid == 1 ? 2 : 1, "api.info", Empty), "PID does not match");
                await mock.Completion;
            }
            await using (var mock = new MockPipe(_ => Success(new { })))
            {
                var client = new NativeClient(() => [], _ => mock.Name);
                await Throws<BridgeException>(() => client.CallAsync(Pid, "api.info", Empty), "not filezilla.exe");
                await mock.Completion;
            }
        });
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static NativeClient NoServer() => new(() => []);
    private static string Success(object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result }) + "\n";
    private static string Request(object id, string method, object? parameters = null) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } }) + "\n";
    private static string Initialize(string version = "2025-03-26") => Request("init", "initialize", new { protocolVersion = version, capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
    private const string Ready = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n";
    private static int Code(JsonElement frame) => frame.GetProperty("error").GetProperty("code").GetInt32();
    private static JsonElement[] ParseFrames(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }).ToArray();

    private static async Task<(int Code, string Output, string Error)> Run(string[] args, NativeClient client, string input = "")
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter();
        int code = await BridgeApp.RunAsync(args, source, output, diagnostics, client);
        return (code, Encoding.UTF8.GetString(output.ToArray()), diagnostics.ToString());
    }

    private static async Task Test(string name, Func<Task> action)
    {
        try { await action(); passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
    }

    private static async Task Throws<T>(Func<Task> action, string? contains = null) where T : Exception
    {
        try { await action(); }
        catch (T ex)
        {
            if (contains is not null) Check(ex.Message.Contains(contains, StringComparison.Ordinal), ex.Message);
            return;
        }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    private sealed class MockPipe : IAsyncDisposable
    {
        internal string Name { get; } = "filezilla-bridge-test-" + Guid.NewGuid().ToString("N");
        internal Task Completion { get; }
        private readonly NamedPipeServerStream server;
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        internal MockPipe(Func<JsonElement, string> respond)
        {
            server = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Completion = Serve(respond);
        }

        private async Task Serve(Func<JsonElement, string> respond)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            string? frame = await new FrameReader(server, Json.MaxRequest).ReadAsync(timeout.Token);
            if (frame is null) return;
            using var request = JsonDocument.Parse(frame);
            byte[] response = Encoding.UTF8.GetBytes(respond(request.RootElement));
            await server.WriteAsync(response, timeout.Token);
            await server.FlushAsync(timeout.Token);
            // Closing signals EOF for intentionally truncated/empty response tests.
            await server.DisposeAsync();
        }

        internal NativeClient Client(Func<IEnumerable<int>>? candidates = null) => new(candidates ?? (() => [Pid]), _ => Name, NativeClient.VerifyPid);

        public async ValueTask DisposeAsync()
        {
            await timeout.CancelAsync();
            await server.DisposeAsync();
            try { await Completion; }
            catch (OperationCanceledException) { }
            timeout.Dispose();
        }
    }
}
