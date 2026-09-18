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
public sealed record Request(string Id, string Token, string Operation, JsonElement Args, int TimeoutSeconds = 60, string? BinarySha256 = null);
public sealed record Reply(string Id, bool Ok, JsonElement Data, string? Error = null, string? Message = null)
{
    public static Reply Success(string id, object? data) => new(id, true, Json.Element(data));
    public static Reply Failure(string id, string error, string message) => new(id, false, Json.Element(null), error, message);
}
public enum StreamPacketKind : byte
{
    Jpeg = 1,
    H264 = 2,
    Control = 3
}
public sealed record StreamPacket(StreamPacketKind Kind, long Sequence, DateTimeOffset CapturedUtc, double CaptureEncodeMs, byte[] Payload);
public static class Wire
{
    public const int MaxFrame = 4 * 1024 * 1024;
    private const int StreamPacketHeaderLength = 25;
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

    public static async Task WriteStreamPacketAsync(Stream stream, StreamPacket packet, CancellationToken ct)
    {
        if (packet.Payload.Length > MaxFrame - StreamPacketHeaderLength) throw new InvalidDataException("Stream packet exceeds 4 MiB.");
        byte[] body = new byte[StreamPacketHeaderLength + packet.Payload.Length];
        body[0] = (byte)packet.Kind;
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(1, 8), packet.Sequence);
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(9, 8), packet.CapturedUtc.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(17, 8), BitConverter.DoubleToInt64Bits(packet.CaptureEncodeMs));
        packet.Payload.CopyTo(body, StreamPacketHeaderLength);
        await WriteBodyAsync(stream, body, ct);
    }

    public static async Task<StreamPacket> ReadStreamPacketAsync(Stream stream, CancellationToken ct)
    {
        byte[] length = new byte[4]; await stream.ReadExactlyAsync(length, ct);
        int size = BinaryPrimitives.ReadInt32BigEndian(length);
        if (size is < StreamPacketHeaderLength or > MaxFrame) throw new InvalidDataException("Invalid stream packet length.");
        byte[] body = new byte[size]; await stream.ReadExactlyAsync(body, ct);
        if (!Enum.IsDefined((StreamPacketKind)body[0])) throw new InvalidDataException("Unknown stream packet kind.");
        long sequence = BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(1, 8));
        long capturedMs = BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(9, 8));
        double captureEncodeMs = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(17, 8)));
        return new StreamPacket((StreamPacketKind)body[0], sequence, DateTimeOffset.FromUnixTimeMilliseconds(capturedMs), captureEncodeMs, body[StreamPacketHeaderLength..]);
    }

    private static async Task WriteBodyAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        await stream.WriteAsync(length, ct); await stream.WriteAsync(body, ct); await stream.FlushAsync(ct);
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
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object sync = new();
    private string? code;
    private string? privateSecret;
    private DateTimeOffset expires;
    private int failures;
    private bool open;
    private readonly Queue<DateTimeOffset> attempts = new();
    public string? CurrentCode { get { lock (sync) { RotateIfExpired(); return privateSecret == null ? code : null; } } }
    public DateTimeOffset ExpiresUtc { get { lock (sync) { RotateIfExpired(); return expires; } } }
    public int AttemptsRemaining { get { lock (sync) { RotateIfExpired(); return Math.Max(0, 5 - failures); } } }
    public string Open()
    {
        lock (sync) { privateSecret = null; open = true; Rotate(); return code!; }
    }
    public void OpenPrivate(string secret)
    {
        if (!PairingExchange.ValidHash(secret)) throw new ArgumentException("Invalid private authentication secret.");
        lock (sync) { privateSecret = secret; open = true; Rotate(); }
    }
    private void Rotate()
    {
        string? previous = code;
        if (privateSecret != null) code = privateSecret;
        else do { code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6"); } while (code == previous);
        expires = time.GetUtcNow() + Lifetime; failures = 0;
    }
    private void RotateIfExpired() { if (open && time.GetUtcNow() >= expires) Rotate(); }
    public void Close() { lock (sync) { open = false; code = privateSecret = null; } }

    // Reserve a bounded attempt before expensive PAKE work. The rolling allowance
    // survives code rotation/reopening so rotating the UI cannot defeat throttling.
    public string? BeginAttempt()
    {
        lock (sync)
        {
            RotateIfExpired(); var now = time.GetUtcNow();
            while (attempts.TryPeek(out var at) && now - at >= TimeSpan.FromMinutes(15)) attempts.Dequeue();
            if (!open || code == null || failures >= 5 || attempts.Count >= 15) return null;
            attempts.Enqueue(now); failures++; return code;
        }
    }
    public bool CompleteAttempt(string reservedCode)
    {
        lock (sync)
        {
            // Do not rotate here: an exchange started with an expired code must fail.
            if (!open || code == null || time.GetUtcNow() >= expires || !Safety.Equal(code, reservedCode)) return false;
            open = false; code = null; return true;
        }
    }
    public bool TryConsume(string candidate)
    {
        string? expected = BeginAttempt();
        return expected != null && Safety.Equal(expected, candidate) && CompleteAttempt(expected);
    }
}
