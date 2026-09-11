using System.Windows.Forms;

namespace RemoteDebugger;

// PictureBox is non-selectable by default. Remote input is sent only while this
// surface owns focus, so it must explicitly opt in to keyboard selection.
internal sealed class RemoteScreenView : PictureBox
{
    public RemoteScreenView()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    protected override bool IsInputKey(Keys keyData) => true;
}
