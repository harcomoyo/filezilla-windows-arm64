using System.Diagnostics;
using System.Text.Json;

namespace FileZilla.Bridge;

internal sealed class RefreshTimeoutException(JsonElement status, int seconds)
    : BridgeException($"Refresh did not complete within {seconds} seconds.")
{
    internal JsonElement Status { get; } = status;
}

internal static class RefreshFlow
{
    internal const int MaxWaitSeconds = 30;
    internal const int DefaultWaitSeconds = 10;
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    internal static async Task<JsonElement> RunAsync(NativeClient native, int? pid, JsonElement parameters, int waitSeconds)
    {
        JsonElement accepted = await native.CallAsync(pid, "sessions.refresh", parameters);
        if (waitSeconds <= 0) return accepted;
        if (accepted.ValueKind != JsonValueKind.Object ||
            !accepted.TryGetProperty("token", out var tokenValue) ||
            tokenValue.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(tokenValue.GetString()))
            throw new BridgeException("Native refresh response did not include a token.");
        if (accepted.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True) return accepted;
        if (!parameters.TryGetProperty("session", out var sessionValue) || sessionValue.ValueKind != JsonValueKind.String)
            throw new BridgeException("refresh requires a session.");
        string token = tokenValue.GetString()!;
        JsonElement statusParameters = Json.Element(new { session = sessionValue.GetString(), token });
        var clock = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(waitSeconds);
        while (true)
        {
            JsonElement status = await native.CallAsync(pid, "sessions.refreshStatus", statusParameters);
            if (status.ValueKind != JsonValueKind.Object || !status.TryGetProperty("completed", out var done) ||
                done.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new BridgeException("Native refreshStatus response is missing a boolean completed.");
            if (done.ValueKind == JsonValueKind.True) return status;
            TimeSpan remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new RefreshTimeoutException(status, waitSeconds);
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval);
        }
    }
}
