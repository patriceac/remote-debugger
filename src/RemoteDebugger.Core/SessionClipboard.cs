namespace RemoteDebugger.Core;

public sealed record ClipboardChange(long Version, string Text);

/// <summary>Only changes after the current connection baseline are eligible for sharing.</summary>
public sealed class SessionClipboard
{
    public const int MaximumTextLength = 512 * 1024;
    private uint baseline;
    private long version;
    public bool Connected { get; private set; }
    public ClipboardChange? Latest { get; private set; }

    public void Begin(uint sequence)
    {
        baseline = sequence; version = 0; Latest = null; Connected = true;
    }

    public void Pause() { Connected = false; Latest = null; }
    public void Suppress(uint sequence) => baseline = sequence;
    public bool HasNewSequence(uint sequence) => Connected && sequence != baseline;

    public ClipboardChange? Capture(uint sequence, string text)
    {
        if (!HasNewSequence(sequence)) return null;
        baseline = sequence;
        if (text.Length > MaximumTextLength) return null;
        return Latest = new(++version, text);
    }
}
