using System.Text.Json;
using System.Text.Json.Serialization;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    [JsonIgnore]
    public Rectangle Bounds => new(X, Y, Width, Height);
}

internal static class WindowPlacementStore
{
    private const string FileName = "window-placement.json";

    public static WindowPlacement? Load(string root, IReadOnlyList<Rectangle> workingAreas)
    {
        try
        {
            string path = Path.Combine(root, FileName);
            if (!File.Exists(path)) return null;
            var saved = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path));
            return saved == null ? null : Normalize(saved, workingAreas);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string root, Rectangle bounds, bool maximized)
    {
        try
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, FileName);
            string temporary = path + ".tmp";
            var placement = new WindowPlacement(bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized);
            File.WriteAllText(temporary, JsonSerializer.Serialize(placement));
            File.Move(temporary, path, true);
        }
        catch
        {
            // Window placement is a convenience and must never prevent shutdown.
        }
    }

    internal static WindowPlacement? Normalize(WindowPlacement saved, IReadOnlyList<Rectangle> workingAreas)
    {
        if (saved.Width <= 0 || saved.Height <= 0 || workingAreas.Count == 0) return null;

        Rectangle requested = saved.Bounds;
        Rectangle workArea = workingAreas
            .OrderByDescending(area => IntersectionArea(area, requested))
            .First();

        // Bounds and Screen.WorkingArea use device pixels. The form's MinimumSize
        // is DPI-scaled separately by WinForms, so applying it here would enlarge
        // a correctly saved window every time it starts on a scaled display.
        int width = Math.Min(saved.Width, workArea.Width);
        int height = Math.Min(saved.Height, workArea.Height);
        int x = Math.Clamp(saved.X, workArea.Left, workArea.Right - width);
        int y = Math.Clamp(saved.Y, workArea.Top, workArea.Bottom - height);

        if (IntersectionArea(workArea, requested) == 0)
        {
            x = workArea.Left + (workArea.Width - width) / 2;
            y = workArea.Top + (workArea.Height - height) / 2;
        }

        return new WindowPlacement(x, y, width, height, saved.Maximized);
    }

    private static long IntersectionArea(Rectangle first, Rectangle second)
    {
        Rectangle intersection = Rectangle.Intersect(first, second);
        return (long)intersection.Width * intersection.Height;
    }

    public static IReadOnlyList<Rectangle> WorkingAreas()
        => Forms.Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray();
}
