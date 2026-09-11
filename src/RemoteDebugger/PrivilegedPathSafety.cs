using RemoteDebugger.Core;

namespace RemoteDebugger;

internal static class PrivilegedPathSafety
{
    public static string RequireUnderNonReparseRoot(string path, string root, bool includeLeaf = true)
    {
        string fullPath = UpdatePolicy.RequirePathUnderRoot(path, root);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? cursor = includeLeaf ? fullPath : Path.GetDirectoryName(fullPath);
        while (cursor != null && cursor.Length >= fullRoot.Length)
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Privileged path contains a reparse point: " + cursor);
            if (string.Equals(cursor.TrimEnd(Path.DirectorySeparatorChar), fullRoot, StringComparison.OrdinalIgnoreCase)) break;
            cursor = Path.GetDirectoryName(cursor);
        }
        if (cursor == null) throw new UnauthorizedAccessException("Privileged path ancestry could not be verified.");
        return fullPath;
    }
}
