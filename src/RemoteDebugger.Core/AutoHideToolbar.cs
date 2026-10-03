namespace RemoteDebugger.Core;

public sealed class AutoHideToolbar
{
    private const int HideDelayMilliseconds = 1000;
    private long hideAfter;
    private long slideStarted;
    private double slideFrom;
    private bool slidingVisible;
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

    public double VisibleFraction(long nowMilliseconds)
    {
        double progress = Math.Clamp((nowMilliseconds - slideStarted) / 200d, 0, 1);
        double fraction = slideFrom + ((slidingVisible ? 1 : 0) - slideFrom) * (1 - Math.Pow(1 - progress, 3));
        if (slidingVisible != Visible)
        {
            slideFrom = fraction;
            slidingVisible = Visible;
            slideStarted = nowMilliseconds;
        }
        return fraction;
    }
}
