using System.Security.Cryptography;
using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed partial class Operations
{
    internal async Task TransferAsync(Stream channel, Request request, Action activity, CancellationToken ct)
    {
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
                    await Wire.WriteAsync(channel, Reply.Success(request.Id, new { offset = file.Length, size = upload.Size }), ct);
                    await BulkTransfer.ReceiveAsync(channel, file, file.Length, upload.Size, _ => activity(), ct);
                }
                var result = await ExecuteAsync("upload.commit", Json.Element(new { transfer = id }), ct);
                Record(request.Id, "upload.commit", DateTimeOffset.UtcNow, true, null, Json.Element(result));
                await Wire.WriteAsync(channel, Reply.Success(request.Id, result), ct);
            }
            else
            {
                string path = Resolve(request.Args.Str("path"));
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BulkTransfer.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                long offset = request.Args.Str("sha256") == hash ? request.Args.Long("offset") : 0;
                if (offset < 0 || offset > file.Length) offset = 0;
                file.Position = offset;
                await Wire.WriteAsync(channel, Reply.Success(request.Id, new { offset, size = file.Length, sha256 = hash }), ct);
                await BulkTransfer.SendAsync(file, channel, offset, file.Length, _ => activity(), ct);
                await Wire.WriteAsync(channel, Reply.Success(request.Id, new { verified = true, sha256 = hash }), ct);
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException))
        {
            await Wire.WriteAsync(channel, Reply.Failure(request.Id, "transfer_failed", ex.Message), ct);
        }
    }
}
