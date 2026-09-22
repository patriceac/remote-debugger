using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

// A worker has one active request. Its input reader stays available for cancellation.
internal static class ConnectedWorker
{
    internal static async Task RunAsync(TextReader input, TextWriter output, Func<IConnectedRemote> load,
        CancellationToken ct, Func<IncidentLog>? reports = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task active = Task.CompletedTask;
        CancellationTokenSource? requestLifetime = null;
        string activeId = "";
        try
        {
            while (await input.ReadLineAsync(lifetime.Token) is { } line)
            {
                if (line.Length > 2 * 1024 * 1024) throw new InvalidDataException("Worker request exceeds 2 MiB.");
                var request = JsonSerializer.Deserialize<JsonElement>(line);
                if (request.TryGetProperty("cancel", out var cancelled))
                {
                    if (cancelled.GetString() == activeId) requestLifetime?.Cancel();
                    continue;
                }
                if (!active.IsCompleted) await active;
                requestLifetime?.Dispose();
                requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                activeId = request.Str("id");
                var token = requestLifetime.Token;
                active = Task.Run(async () =>
                {
                    var reply = await ConnectedCli.ExecuteAsync(request, load, token, reports);
                    await output.WriteLineAsync(Json.Text(reply));
                    await output.FlushAsync();
                }, CancellationToken.None);
            }
        }
        finally
        {
            lifetime.Cancel();
            try { await active; } finally { requestLifetime?.Dispose(); }
        }
    }
}
