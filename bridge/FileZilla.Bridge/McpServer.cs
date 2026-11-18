using System.Text.Json;

namespace FileZilla.Bridge;

internal static class McpServer
{
    private static object Error(object? id, int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };
    private static object Result(object? id, object result) => new { jsonrpc = "2.0", id, result };

    private static bool HasParams(JsonElement parameters, params string[] names) =>
        Json.HasOnly(parameters, [.. names, "_meta"]) &&
        (!parameters.TryGetProperty("_meta", out var metadata) || metadata.ValueKind == JsonValueKind.Object);

    internal static async Task<int> RunAsync(Stream input, Stream output, TextWriter diagnostics, NativeClient native, int? pid)
    {
        var reader = new FrameReader(input, Json.MaxRequest);
        bool initialized = false;
        bool ready = false;
        while (true)
        {
            object? id = null;
            object? response;
            try
            {
                string? frame = await reader.ReadAsync();
                if (frame is null) return 0;
                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;
                if (!Json.HasOnly(root, "jsonrpc", "id", "method", "params") ||
                    !root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                    !root.TryGetProperty("method", out var methodValue) || methodValue.ValueKind != JsonValueKind.String ||
                    (root.TryGetProperty("id", out var requestId) && !Json.IsId(requestId)))
                {
                    response = Error(null, -32600, "Invalid JSON-RPC request.");
                }
                else
                {
                    bool notification = !root.TryGetProperty("id", out requestId);
                    if (!notification) id = requestId.Clone();
                    string method = methodValue.GetString()!;
                    JsonElement parameters = root.TryGetProperty("params", out var supplied) ? supplied : Json.Element(new { });
                    if (notification)
                    {
                        if (method == "notifications/initialized" && initialized && HasParams(parameters)) ready = true;
                        continue;
                    }
                    if (method == "ping")
                        response = HasParams(parameters) ? Result(id, new { }) : Error(id, -32602, "Invalid ping parameters.");
                    else if (method == "initialize")
                    {
                        if (initialized) response = Error(id, -32600, "Already initialized.");
                        else if (!HasParams(parameters, "protocolVersion", "capabilities", "clientInfo") ||
                            !parameters.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.String ||
                            !parameters.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object ||
                            !parameters.TryGetProperty("clientInfo", out var client) || client.ValueKind != JsonValueKind.Object ||
                            !client.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                            !client.TryGetProperty("version", out var clientVersion) || clientVersion.ValueKind != JsonValueKind.String)
                            response = Error(id, -32602, "Invalid initialize parameters.");
                        else
                        {
                            string requested = protocol.GetString()!;
                            string negotiated = requested is "2024-11-05" or "2025-03-26" ? requested : "2025-03-26";
                            initialized = true;
                            response = Result(id, new { protocolVersion = negotiated, capabilities = new { tools = new { listChanged = false } },
                                serverInfo = new { name = "filezilla-bridge", version = "0.1.0" } });
                        }
                    }
                    else if (!ready) response = Error(id, -32002, "Complete initialize and notifications/initialized first.");
                    else if (method == "tools/list")
                        response = HasParams(parameters) ? Result(id, new { tools = Tools.List() }) : Error(id, -32602, "Invalid tools/list parameters.");
                    else if (method == "tools/call")
                    {
                        if (!HasParams(parameters, "name", "arguments") || !parameters.TryGetProperty("name", out var toolName) || toolName.ValueKind != JsonValueKind.String)
                            response = Error(id, -32602, "Expected tool name and arguments.");
                        else
                        {
                            var tool = Tools.Definitions.FirstOrDefault(t => t.Name == toolName.GetString());
                            if (tool.Name is null) response = Error(id, -32602, "Unknown tool.");
                            else
                            {
                                JsonElement toolArgs = parameters.TryGetProperty("arguments", out var a) ? a : Json.Element(new { });
                                JsonElement nativeArgs = toolArgs;
                                int waitSeconds = 0;
                                try
                                {
                                    if (tool.Method == "sessions.refresh")
                                    {
                                        waitSeconds = Tools.RefreshWaitSeconds(toolArgs);
                                        nativeArgs = Json.Element(new { session = toolArgs.GetProperty("session").GetString() });
                                    }
                                    else Tools.Validate(tool.Method, toolArgs);
                                }
                                catch (BridgeException ex)
                                {
                                    response = Error(id, -32602, ex.Message);
                                    await Json.WriteAsync(output, response, Json.MaxResponse);
                                    continue;
                                }
                                try
                                {
                                    var result = waitSeconds > 0
                                        ? await RefreshFlow.RunAsync(native, pid, nativeArgs, waitSeconds)
                                        : await native.CallAsync(pid, tool.Method, nativeArgs);
                                    response = Result(id, new { content = new[] { new { type = "text", text = result.GetRawText() } }, isError = false });
                                }
                                catch (RefreshTimeoutException ex)
                                {
                                    response = Result(id, new { content = new[] { new { type = "text",
                                        text = $"{ex.Message} Last status: {ex.Status.GetRawText()}" } }, isError = true });
                                }
                                catch (BridgeException ex)
                                {
                                    response = Result(id, new { content = new[] { new { type = "text", text = ex.Message } }, isError = true });
                                }
                            }
                        }
                    }
                    else response = Error(id, -32601, "Method not found.");
                }
            }
            catch (Exception ex) when (ex is JsonException or FrameException)
            {
                response = Error(null, -32700, ex is FrameException ? ex.Message : "Invalid JSON.");
            }
            try { await Json.WriteAsync(output, response, Json.MaxResponse); }
            catch (BridgeException ex)
            {
                await diagnostics.WriteLineAsync(ex.Message);
                await Json.WriteAsync(output, Error(id, -32603, "Response exceeds maximum frame size."), Json.MaxResponse);
            }
        }
    }
}
