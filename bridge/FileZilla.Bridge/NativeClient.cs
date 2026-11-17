using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FileZilla.Bridge;

internal sealed class NativeClient
{
    private readonly Func<IEnumerable<int>> candidates;
    private readonly Func<int, string> pipeName;
    private readonly Action<NamedPipeClientStream, int> verify;

    internal NativeClient(Func<IEnumerable<int>>? candidates = null, Func<int, string>? pipeName = null,
        Action<NamedPipeClientStream, int>? verify = null)
    {
        this.candidates = candidates ?? FindProcesses;
        this.pipeName = pipeName ?? (pid => $"filezilla-api-{pid}");
        this.verify = verify ?? VerifyServer;
    }

    private static IEnumerable<int> FindProcesses()
    {
        foreach (var process in Process.GetProcessesByName("filezilla"))
        {
            using (process) yield return process.Id;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    internal static void VerifyPid(NamedPipeClientStream pipe, int pid)
    {
        if (!OperatingSystem.IsWindows()) throw new BridgeException("This bridge requires Windows named pipes.");
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actual))
            throw new BridgeException($"Cannot verify pipe server PID (Win32 {Marshal.GetLastWin32Error()}).");
        if (actual != pid) throw new BridgeException("Pipe server PID does not match the selected process.");
    }

    private static void VerifyServer(NamedPipeClientStream pipe, int pid)
    {
        VerifyPid(pipe, pid);
        try
        {
            using var process = Process.GetProcessById(pid);
            string? executable = process.MainModule?.FileName;
            if (!string.Equals(Path.GetFileName(executable), "filezilla.exe", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("Pipe server executable is not filezilla.exe.");
        }
        catch (BridgeException) { throw; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            throw new BridgeException($"Cannot verify pipe server executable: {ex.Message}");
        }
    }

    private async Task<NamedPipeClientStream> ConnectAsync(int pid, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName(pid), PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(300, ct);
            verify(pipe, pid);
            return pipe;
        }
        catch { await pipe.DisposeAsync(); throw; }
    }

    private async Task<NamedPipeClientStream> DiscoverAsync(int? pid, CancellationToken ct)
    {
        if (pid.HasValue) return await ConnectAsync(pid.Value, ct);
        NamedPipeClientStream? selected = null;
        try
        {
            foreach (int candidate in candidates().Distinct())
            {
                NamedPipeClientStream pipe;
                try { pipe = await ConnectAsync(candidate, ct); }
                catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or BridgeException)
                {
                    continue;
                }
                if (selected is not null)
                {
                    await pipe.DisposeAsync();
                    throw new BridgeException("Multiple live FileZilla API pipes found; select one with --pid.");
                }
                selected = pipe;
            }
            if (selected is null) throw new BridgeException("No verified FileZilla API pipe found.");
            return selected;
        }
        catch
        {
            if (selected is not null) await selected.DisposeAsync();
            throw;
        }
    }

    internal async Task<JsonElement> CallAsync(int? pid, string method, JsonElement parameters)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var pipe = await DiscoverAsync(pid, timeout.Token);
            await Json.WriteAsync(pipe, new { jsonrpc = "2.0", id = 1, method, @params = parameters }, Json.MaxRequest, timeout.Token);
            string? frame = await new FrameReader(pipe, Json.MaxResponse).ReadAsync(timeout.Token);
            if (frame is null) throw new BridgeException("Native server closed without a response.");
            using var document = JsonDocument.Parse(frame);
            var root = document.RootElement;
            if (!Json.HasOnly(root, "jsonrpc", "id", "result", "error") ||
                !root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                !root.TryGetProperty("id", out var id) || !id.TryGetInt32Safe(out int number) || number != 1 ||
                root.TryGetProperty("result", out _) == root.TryGetProperty("error", out _))
                throw new BridgeException("Invalid native JSON-RPC response envelope.");
            if (root.TryGetProperty("error", out var error))
            {
                if (!Json.HasOnly(error, "code", "message", "data") ||
                    !error.TryGetProperty("code", out var code) || !code.TryGetInt32Safe(out int errorCode) ||
                    !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
                    throw new BridgeException("Invalid native JSON-RPC error.");
                throw new BridgeException($"Native error {errorCode}: {message.GetString()}");
            }
            return root.GetProperty("result").Clone();
        }
        catch (BridgeException) { throw; }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or FrameException or UnauthorizedAccessException)
        {
            throw new BridgeException($"Native API request failed: {ex.Message}");
        }
    }
}

internal static class JsonExtensions
{
    internal static bool TryGetInt32Safe(this JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }
}
