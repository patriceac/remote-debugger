using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed partial class RemoteClient
{
    internal async Task TransferUpdateAsync(string transactionId, Stream input, long size,
        IProgress<AgentUpdateProgress>? progress, CancellationToken ct)
    {
        await using var channel = await OpenTransportAsync(ct);
        var request = new Request(Guid.NewGuid().ToString(), Connection.Token, "update.transfer",
            Json.Element(new { transactionId }), 300, controllerBinarySha256);
        await Wire.WriteAsync(channel, request, ct);
        var ready = Require(await Wire.ReadAsync<Reply>(channel, ct));
        long offset = ready.Long("offset");
        if (ready.Long("size") != size || offset < 0 || offset > size) throw new InvalidDataException("Invalid update resume offset.");
        input.Position = offset;
        await BulkTransfer.SendAsync(input, channel, offset, size,
            bytes => progress?.Report(new("transferring", bytes, size)), ct);
        var complete = Require(await Wire.ReadAsync<Reply>(channel, ct));
        if (complete.Long("offset") != size) throw new InvalidDataException("Incomplete update transfer.");
    }
}
