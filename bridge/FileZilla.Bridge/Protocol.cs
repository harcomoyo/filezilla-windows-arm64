using System.Text;
using System.Text.Json;

namespace FileZilla.Bridge;

internal class BridgeException(string message) : Exception(message);
internal sealed class FrameException(string message) : Exception(message);

internal static class Json
{
    internal const int MaxRequest = 64 * 1024;
    internal const int MaxResponse = 1024 * 1024;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value);
    internal static bool HasOnly(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return obj.EnumerateObject().All(p => names.Contains(p.Name, StringComparer.Ordinal) && seen.Add(p.Name));
    }

    internal static bool IsId(JsonElement id) => id.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null;

    internal static async Task WriteAsync(Stream stream, object value, int limit, CancellationToken ct = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > limit) throw new BridgeException($"JSON frame exceeds {limit} bytes.");
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync(new byte[] { 10 }, ct);
        await stream.FlushAsync(ct);
    }
}

// Retain bytes read past a newline, and drain oversized frames without growing memory.
internal sealed class FrameReader(Stream stream, int limit)
{
    private readonly byte[] buffer = new byte[4096];
    private int position;
    private int available;

    internal async Task<string?> ReadAsync(CancellationToken ct = default)
    {
        using var frame = new MemoryStream();
        bool oversized = false;
        while (true)
        {
            if (position == available)
            {
                available = await stream.ReadAsync(buffer, ct);
                position = 0;
                if (available == 0)
                {
                    if (frame.Length == 0 && !oversized) return null;
                    throw new FrameException("Unterminated JSON frame.");
                }
            }
            byte next = buffer[position++];
            if (next == 10)
            {
                if (oversized) throw new FrameException($"JSON frame exceeds {limit} bytes.");
                try { return Json.Utf8.GetString(frame.GetBuffer(), 0, (int)frame.Length); }
                catch (DecoderFallbackException) { throw new FrameException("Frame is not valid UTF-8."); }
            }
            if (frame.Length < limit) frame.WriteByte(next);
            else oversized = true;
        }
    }
}
