using System.Drawing;
using Forms = System.Windows.Forms;

namespace RemoteDebugger;

public sealed class PagePanel : Forms.Panel
{
    public PagePanel(string title) { Text = title; }
}

// Plain native buttons keep navigation accessible without nested TabControl providers.
public sealed class PageSwitcher : Forms.UserControl
{
    private readonly Forms.FlowLayoutPanel navigation = new() { Dock = Forms.DockStyle.Top, Height = 45, Padding = new Forms.Padding(4, 3, 0, 0), WrapContents = false, BackColor = Color.FromArgb(238, 241, 245) };
    private readonly Forms.Panel body = new() { Dock = Forms.DockStyle.Fill };
    private readonly List<PagePanel> pages = [];
    private readonly List<Forms.Button> buttons = [];
    private int selected = -1;
    public PageSwitcher() { Controls.Add(body); Controls.Add(navigation); TabPages = new PageCollection(this); }
    public PageCollection TabPages { get; }
    public int SelectedIndex
    {
        get => selected;
        set
        {
            if (value < 0 || value >= pages.Count) throw new ArgumentOutOfRangeException(nameof(value));
            selected = value;
            for (int i = 0; i < pages.Count; i++) { pages[i].Visible = i == value; buttons[i].BackColor = i == value ? Color.FromArgb(39, 70, 106) : Color.FromArgb(238, 241, 245); buttons[i].ForeColor = i == value ? Color.White : Color.FromArgb(27, 42, 61); }
            pages[value].BringToFront();
        }
    }
    public sealed class PageCollection(PageSwitcher owner)
    {
        public void Add(PagePanel page)
        {
            int index = owner.pages.Count; page.Dock = Forms.DockStyle.Fill; page.Visible = false; owner.pages.Add(page); owner.body.Controls.Add(page);
            var button = new Forms.Button { Text = page.Text, AccessibleName = page.Text, UseMnemonic = false, Name = "navigate" + index, AutoSize = true, FlatStyle = Forms.FlatStyle.Flat, Height = 35, Padding = new Forms.Padding(9, 3, 9, 3), Margin = new Forms.Padding(2) }; button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => owner.SelectedIndex = index; owner.buttons.Add(button); owner.navigation.Controls.Add(button);
            if (owner.selected < 0) owner.SelectedIndex = 0;
        }
    }
}
