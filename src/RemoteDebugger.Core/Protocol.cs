using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDebugger.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, Options);
    public static string Text(object? value) => JsonSerializer.Serialize(value, Options);
    public static string Str(this JsonElement e, string key, string fallback = "") => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;
    public static int Int(this JsonElement e, string key, int fallback = 0) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.GetInt32() : fallback;
    public static long Long(this JsonElement e, string key, long fallback = 0) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.GetInt64() : fallback;
    public static string[] Strings(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
}
public sealed record Request(string Id, string Token, string Operation, JsonElement Args, int TimeoutSeconds = 60);
public sealed record Reply(string Id, bool Ok, JsonElement Data, string? Error = null, string? Message = null)
{
    public static Reply Success(string id, object? data) => new(id, true, Json.Element(data));
    public static Reply Failure(string id, string error, string message) => new(id, false, Json.Element(null), error, message);
}
public static class Wire
{
    public const int MaxFrame = 4 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
        if (body.Length > MaxFrame) throw new InvalidDataException("Frame exceeds 4 MiB.");
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        await stream.WriteAsync(length, ct); await stream.WriteAsync(body, ct); await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        byte[] length = new byte[4]; await stream.ReadExactlyAsync(length, ct);
        int size = BinaryPrimitives.ReadInt32BigEndian(length);
        if (size is < 1 or > MaxFrame) throw new InvalidDataException("Invalid frame length.");
        byte[] body = new byte[size]; await stream.ReadExactlyAsync(body, ct);
        return JsonSerializer.Deserialize<T>(body, Json.Options) ?? throw new InvalidDataException("Empty message.");
    }
}
public static class Safety
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public static string UnderRoot(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new ArgumentException("A relative path is required.");
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path escapes the workspace.");
        for (string? cursor = path; cursor != null && cursor.Length >= prefix.TrimEnd(Path.DirectorySeparatorChar).Length; cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("Reparse points are not permitted.");
        return path;
    }
}
public sealed class PairingGate(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object sync = new();
    private string? code;
    private DateTimeOffset expires;
    private int failures;
    public string Open()
    {
        lock (sync) { code = RandomNumberGenerator.GetInt32(0, 100000000).ToString("D8"); expires = time.GetUtcNow().AddMinutes(3); failures = 0; return code; }
    }
    public void Close() { lock (sync) code = null; }
    public bool TryConsume(string candidate)
    {
        lock (sync)
        {
            if (code == null || time.GetUtcNow() >= expires || failures >= 5) return false;
            if (!Safety.Equal(code, candidate)) { failures++; return false; }
            code = null; return true;
        }
    }
}
