using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed partial class Operations
{
    internal DesktopClipboard? Clipboard { get; set; }

    private async Task<object> ClipboardAsync(string operation, JsonElement args, CancellationToken ct)
    {
        var clipboard = Clipboard ?? throw new InvalidOperationException("Clipboard sharing is unavailable.");
        switch (operation)
        {
            case "clipboard.begin": return new { session = await clipboard.BeginAsync(ct) };
            case "clipboard.read": return new { change = await clipboard.ReadAsync(args.Str("session"), args.Long("after"), ct) };
            case "clipboard.write":
                await clipboard.ApplyAsync(args.Str("session"), new(args.Long("version"), args.Str("text")), ct);
                return new { applied = true };
            default: throw new ArgumentException("Unknown clipboard operation.");
        }
    }
}
