using System.Runtime.CompilerServices;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

/// <summary>Explicit bindings for app-owned text. Remote content is never translated.</summary>
internal static class LiveText
{
    private sealed record Binding(Func<string> Text);
    private static readonly ConditionalWeakTable<object, Binding> bindings = new();

    public static T WithText<T>(this T control, Func<string> text) where T : Forms.Control
    {
        control.SetText(text);
        return control;
    }

    public static string SetText(this Forms.Control control, Func<string> text)
    {
        bindings.AddOrUpdate(control, new Binding(text));
        string value = text();
        if (!string.Equals(control.Text, value, StringComparison.Ordinal)) control.Text = value;
        return value;
    }

    public static string SetText(this Forms.Control control, string text)
    {
        bindings.Remove(control);
        if (!string.Equals(control.Text, text, StringComparison.Ordinal)) control.Text = text;
        return text;
    }

    public static Forms.ColumnHeader WithText(this Forms.ColumnHeader column, Func<string> text)
    {
        bindings.AddOrUpdate(column, new Binding(text)); column.Text = text(); return column;
    }

    public static string Caption(Forms.ColumnHeader column) => bindings.TryGetValue(column, out var binding)
        ? binding.Text() : column.Text.Replace("  ▼", "").Replace("  ▲", "");

    public static Forms.ToolStripItem WithText(this Forms.ToolStripItem item, Func<string> text)
    {
        bindings.AddOrUpdate(item, new Binding(text)); item.Text = text(); return item;
    }

    public static void Refresh(Forms.Control control)
    {
        if (bindings.TryGetValue(control, out var binding)) control.Text = binding.Text();
        if (control is Forms.ListView list)
            foreach (Forms.ColumnHeader column in list.Columns)
                if (bindings.TryGetValue(column, out var caption)) column.Text = caption.Text();
        foreach (Forms.Control child in control.Controls) Refresh(child);
    }

    public static void Refresh(Forms.ToolStripItem item)
    {
        if (bindings.TryGetValue(item, out var binding)) item.Text = binding.Text();
    }
}

internal class LocalizedComboBox : Forms.ComboBox
{
    // Refresh cached display strings without changing the selected monitor.
    public void RefreshLabels() => RefreshItems();
}
