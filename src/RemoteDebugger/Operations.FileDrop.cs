using System.Text.Json;
using RemoteDebugger.Core;

namespace RemoteDebugger;

public sealed partial class Operations
{
    private async Task<object> FileDropAsync(string operation, JsonElement args, CancellationToken ct)
    {
        if (operation is "shell.dropTarget" or "shell.selection") return await ExplorerFiles.QueryAsync(operation, args.Int("x"), args.Int("y"), ct);
        if (operation == "files.manifest")
        {
            var paths = args.Strings("paths");
            if (paths.Length is < 1 or > 256) throw new ArgumentException("Select between 1 and 256 top-level items.");
            return await Task.Run(() => TransferTree.Read(paths.Select(Resolve), ct), ct);
        }
        string directory = ResolveAbsoluteUpload(args.Str("directory"));
        var entries = args.GetProperty("entries").Deserialize<TransferEntry[]>(Json.Options) ?? [];
        if (entries.Length is < 1 or > TransferTree.MaximumEntries) throw new ArgumentException("Invalid transfer manifest.");
        var targets = entries.Select(entry => (Entry: entry, Path: TransferTree.Destination(directory, entry.RelativePath))).ToArray();
        if (operation == "files.conflicts")
        {
            foreach (var item in targets)
                if (item.Entry.Directory ? File.Exists(item.Path) : Directory.Exists(item.Path))
                    throw new IOException("A file and a folder share the same destination name: " + item.Entry.RelativePath);
            return targets.Where(item => !item.Entry.Directory && File.Exists(item.Path)).Select(item => item.Entry.RelativePath).ToArray();
        }
        if (operation == "files.createDirectories")
        {
            foreach (var item in targets.Where(item => item.Entry.Directory)) { ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(item.Path); }
            return new { created = true };
        }
        throw new ArgumentException("Unknown file-drop operation.");
    }
}
