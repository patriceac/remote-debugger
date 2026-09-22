namespace RemoteDebugger.Core;

public sealed record TransferEntry(string SourcePath, string RelativePath, bool Directory, long Size, DateTimeOffset ModifiedUtc);

public static class TransferTree
{
    public const int MaximumEntries = 10000;

    public static TransferEntry[] Read(IEnumerable<string> sourcePaths, CancellationToken ct = default)
    {
        var entries = new List<TransferEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.GetFullPath(source);
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            Add(path, name);
        }
        if (entries.Count == 0) throw new ArgumentException("Choose at least one file or folder.");
        return entries.ToArray();

        void Add(string path, string relative)
        {
            ct.ThrowIfCancellationRequested(); ValidateRelativePath(relative);
            if (entries.Count >= MaximumEntries) throw new IOException("A transfer can contain at most 10,000 files and folders.");
            if (!names.Add(relative)) throw new IOException("Selected items contain duplicate destination names.");
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Copying links or junctions is not supported: " + path);
            bool directory = attributes.HasFlag(FileAttributes.Directory);
            entries.Add(new(path, relative, directory, directory ? 0 : new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));
            if (directory)
                foreach (string child in System.IO.Directory.EnumerateFileSystemEntries(path).Order(StringComparer.OrdinalIgnoreCase))
                    Add(child, Path.Combine(relative, Path.GetFileName(child)));
        }
    }

    public static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 259 || Path.IsPathRooted(path))
            throw new ArgumentException("A copied item must have a relative name of at most 259 characters.");
        foreach (string part in path.Replace('/', '\\').Split('\\'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new ArgumentException("Invalid copied item name.");
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]))
                throw new ArgumentException("Windows device names cannot be copied.");
        }
    }

    public static string Destination(string directory, string relative)
    {
        ValidateRelativePath(relative);
        return Safety.UnderRoot(Path.GetFullPath(directory), relative);
    }
}
