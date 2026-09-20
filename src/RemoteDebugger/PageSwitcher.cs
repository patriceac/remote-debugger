using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed class PagePanel : Forms.Panel
{
    public PagePanel(string title) { Text = title; BackColor = Color.Transparent; }
    public PagePanel(Func<string> title) : this(title()) => this.SetText(title);
}

/// <summary>
/// Lightweight native page host. MainForm supplies the stable navigation rail;
/// this control keeps page visibility and keyboard navigation deterministic without
/// nesting a TabControl inside the working surface.
/// </summary>
public sealed class PageSwitcher : Forms.UserControl
{
    private readonly Forms.Panel body = new() { Dock = Forms.DockStyle.Fill, BackColor = Color.Transparent };
    private readonly Forms.FlowLayoutPanel navigation = new() { Dock = Forms.DockStyle.Top, Height = 45, Padding = new Forms.Padding(4, 3, 0, 0), WrapContents = false, BackColor = Color.Transparent };
    private readonly List<PagePanel> pages = [];
    private readonly List<Forms.Button> buttons = [];
    private int selected = -1;
    private bool navigationVisible = true;

    public PageSwitcher()
    {
        Controls.Add(body); Controls.Add(navigation); TabPages = new PageCollection(this); NavigationVisible = true;
    }

    public PageCollection TabPages { get; }
    public Forms.Panel Body => body;
    public Forms.FlowLayoutPanel Navigation => navigation;
    public bool NavigationVisible
    {
        get => navigationVisible;
        set { navigationVisible = value; navigation.Visible = value; navigation.Height = value ? 45 : 0; }
    }

    public int SelectedIndex
    {
        get => selected;
        set
        {
            if (value < 0 || value >= pages.Count) throw new ArgumentOutOfRangeException(nameof(value));
            selected = value;
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Visible = i == value;
                if (i < buttons.Count) SetButtonState(buttons[i], i == value);
            }
            pages[value].BringToFront();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SelectedIndexChanged;

    private static void SetButtonState(Forms.Button button, bool selected)
    {
        button.BackColor = selected ? Color.FromArgb(36, 68, 78) : Color.Transparent;
        button.ForeColor = selected ? Color.White : Color.FromArgb(99, 119, 128);
    }

    public sealed class PageCollection(PageSwitcher owner)
    {
        public void Add(PagePanel page)
        {
            ArgumentNullException.ThrowIfNull(page);
            int index = owner.pages.Count;
            page.Dock = Forms.DockStyle.Fill; page.Visible = false; owner.pages.Add(page); owner.body.Controls.Add(page);
            var button = new Forms.Button { Text = page.Text, AccessibleName = page.Text, UseMnemonic = false, Name = "navigate" + index, AutoSize = true, FlatStyle = Forms.FlatStyle.Flat, Height = 35, Padding = new Forms.Padding(9, 3, 9, 3), Margin = new Forms.Padding(2), Cursor = Forms.Cursors.Hand }; button.FlatAppearance.BorderSize = 0; button.Click += (_, _) => owner.SelectedIndex = index; owner.buttons.Add(button); owner.navigation.Controls.Add(button);
            page.TextChanged += (_, _) => button.Text = button.AccessibleName = page.Text;
            if (owner.selected < 0) owner.SelectedIndex = 0;
        }
    }
}
