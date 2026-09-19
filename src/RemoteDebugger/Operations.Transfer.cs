using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed partial class Operations
{
    public sealed record TransferStatus(string Path, bool Receiving, string Stage, long TransferredBytes, long TotalBytes, long BytesThisAttempt, TimeSpan Elapsed, string? Error = null);
    private TransferStatus? fileTransfer;
    public TransferStatus? FileTransfer => Volatile.Read(ref fileTransfer);

    internal async Task TransferAsync(Stream channel, Request request, Action activity, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        long initial = 0;
        TransferStatus? current = null;
        void Report(string path, bool receiving, string stage, long bytes, long total, string? error = null)
        {
            current = new(path, receiving, stage, bytes, total, Math.Max(0, bytes - initial), watch.Elapsed, error);
            Volatile.Write(ref fileTransfer, current);
        }
        try
        {
            if (request.Operation == "file.upload")
            {
                string id = request.Args.Str("transfer");
                if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid transfer identity.");
                string meta = Path.Combine(Transfers, id + ".json"), partial = Path.Combine(Transfers, id + ".partial");
                var upload = JsonSerializer.Deserialize<Upload>(await File.ReadAllTextAsync(meta, ct), Json.Options)
                    ?? throw new InvalidDataException("Missing transfer metadata.");
                await using (var file = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None, BulkTransfer.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    file.Position = file.Length;
                    initial = file.Length; watch.Restart();
                    Report(upload.Path, true, "transferring", file.Length, upload.Size);
                    await Wire.WriteAsync(channel, Reply.Success(request.Id, new { offset = file.Length, size = upload.Size }), ct);
                    await BulkTransfer.ReceiveAsync(channel, file, file.Length, upload.Size, bytes => { Report(upload.Path, true, "transferring", bytes, upload.Size); activity(); }, ct);
                }
                Report(upload.Path, true, "verifying", upload.Size, upload.Size);
                var result = await ExecuteAsync("upload.commit", Json.Element(new { transfer = id }), ct);
                Record(request.Id, "upload.commit", DateTimeOffset.UtcNow, true, null, Json.Element(result));
                Report(upload.Path, true, "complete", upload.Size, upload.Size);
                await Wire.WriteAsync(channel, Reply.Success(request.Id, result), ct);
            }
            else
            {
                string path = Resolve(request.Args.Str("path"));
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BulkTransfer.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                Report(path, false, "preparing", 0, file.Length);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                long offset = request.Args.Str("sha256") == hash ? request.Args.Long("offset") : 0;
                if (offset < 0 || offset > file.Length) offset = 0;
                file.Position = offset;
                initial = offset; watch.Restart();
                Report(path, false, "transferring", offset, file.Length);
                await Wire.WriteAsync(channel, Reply.Success(request.Id, new { offset, size = file.Length, sha256 = hash }), ct);
                await BulkTransfer.SendAsync(file, channel, offset, file.Length, bytes => { Report(path, false, "transferring", bytes, file.Length); activity(); }, ct);
                Report(path, false, "complete", file.Length, file.Length);
                await Wire.WriteAsync(channel, Reply.Success(request.Id, new { verified = true, sha256 = hash }), ct);
            }
        }
        catch (Exception ex)
        {
            if (current != null && current.Stage != "complete")
                Report(current.Path, current.Receiving, ex is OperationCanceledException or IOException ? "paused" : "failed", current.TransferredBytes, current.TotalBytes, ex.Message);
            if (ex is OperationCanceledException or IOException) throw;
            await Wire.WriteAsync(channel, Reply.Failure(request.Id, "transfer_failed", ex.Message), ct);
        }
    }
}
