using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
#if VERSION2
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
#else
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif
public sealed class Fixture : Form
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern void OutputDebugString(string value);
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    private readonly TextBox input = new TextBox { Name = "fixtureInput", Width = 330, Location = new Point(25, 90) };
    private readonly Label result = new Label { Name = "fixtureResult", Width = 560, Height = 40, Location = new Point(25, 180) };
    private readonly Label ticker = new Label { Width = 550, Height = 45, Location = new Point(25, 245), Font = new Font("Consolas", 18) };
    private readonly string output;
    private int tick;
    private readonly Timer timer = new Timer { Interval = 33 };
    public Fixture(string path)
    {
        output = path; Directory.CreateDirectory(output); string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
        Text = "Remote Debugger Fixture v" + version; Name = "FixtureWindow"; Width = 640; Height = 380; StartPosition = FormStartPosition.CenterScreen; BackColor = Color.White; Font = new Font("Segoe UI", 11);
        var title = new Label { Text = "Application de test · version " + version, Location = new Point(25, 25), Width = 570, Height = 40, Font = new Font("Segoe UI", 18, FontStyle.Bold) };
        var save = new Button { Name = "saveButton", Text = "Enregistrer le résultat", Location = new Point(25, 130), Width = 225, Height = 35 };
        save.Click += delegate { string value = input.Text.Replace("\\", "\\\\").Replace("\"", "\\\""); File.WriteAllText(Path.Combine(output, "result.json"), "{\"version\":\"" + version + "\",\"text\":\"" + value + "\",\"saved\":true}"); File.AppendAllText(Path.Combine(output, "fixture.log"), DateTime.UtcNow.ToString("O") + " saved " + version + Environment.NewLine); result.Text = "Résultat enregistré : " + input.Text; OutputDebugString("Fixture saved result " + version); };
        var hang = new Button { Name = "hangButton", Text = "Bloquer 15 s", Location = new Point(270, 130), Width = 135, Height = 35 }; hang.Click += delegate { System.Threading.Thread.Sleep(15000); };
        var crash = new Button { Name = "crashButton", Text = "Simuler plantage", Location = new Point(420, 130), Width = 160, Height = 35 }; crash.Click += delegate { Environment.FailFast("Synthetic RemoteDebugger fixture crash"); };
        Controls.AddRange(new Control[] { title, input, save, hang, crash, result, ticker }); AcceptButton = save;
        timer.Tick += delegate { ticker.Text = "Animation " + (++tick).ToString("D6") + "  " + DateTime.Now.ToString("HH:mm:ss.fff"); ticker.ForeColor = tick % 2 == 0 ? Color.DarkSlateBlue : Color.SeaGreen; }; timer.Start();
        Shown += delegate { input.Focus(); };
    }
    [STAThread] public static void Main(string[] args) { SetErrorMode(2); Application.EnableVisualStyles(); Application.Run(new Fixture(args[0])); }
}
