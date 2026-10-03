namespace RemoteDebugger.Core;

public sealed class AutoHideToolbar
{
    private const int HideDelayMilliseconds = 1000;
    private long hideAfter;
    public bool Visible { get; private set; }

    public void Reveal(long nowMilliseconds)
    {
        Visible = true;
        hideAfter = nowMilliseconds + HideDelayMilliseconds;
    }

    public bool Update(long nowMilliseconds, bool atTopEdge, bool overToolbar)
    {
        if (atTopEdge || Visible && overToolbar) Reveal(nowMilliseconds);
        else if (nowMilliseconds >= hideAfter) Visible = false;
        return Visible;
    }
}
