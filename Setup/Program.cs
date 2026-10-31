using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ShibaPlayerSetup;

/// <summary>
/// Per-user installer for ShibaPlayer (a copy of ShibaDownloader's): copies the embedded exe to %LOCALAPPDATA%\Programs\ShibaPlayer,
/// adds Start menu (and optionally desktop) shortcuts and an "Installed apps" entry whose Uninstall runs the
/// uninstall.cmd written next to the exe. No admin rights, nothing outside the user's profile.
/// Run with /silent to install without UI (exit code 0 = installed).
/// </summary>
internal static class Program
{
    internal const string AppName = "ShibaPlayer";
    internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName;

    internal static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
    internal static readonly string InstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    internal static readonly string AppExe = Path.Combine(InstallDir, AppName + ".exe");
    private static readonly string StartMenuLink =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    private static readonly string DesktopLink =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase)))
        {
            try { Install(desktopShortcut: false, _ => { }); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
        return 0;
    }

    /// <summary>Version currently registered as installed, or null.</summary>
    internal static string InstalledVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("DisplayVersion") as string;
    }

    internal static void Install(bool desktopShortcut, Action<int> progress)
    {
        Directory.CreateDirectory(InstallDir);

        // Unpack to a temp name first so a failed copy never leaves a half-written exe behind.
        var temp = AppExe + ".new";
        using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.exe")
                         ?? throw new InvalidOperationException("Setup is damaged: the app is missing inside it."))
        using (var dst = File.Create(temp))
        {
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                dst.Write(buf, 0, n);
                done += n;
                progress((int)(done * 90 / src.Length));
            }
        }

        try
        {
            File.Copy(temp, AppExe, overwrite: true);
        }
        catch (IOException)
        {
            throw new IOException($"{AppName} is running. Close it and try again.");
        }
        finally
        {
            File.Delete(temp);
        }
        progress(94);

        WriteShortcut(StartMenuLink);
        if (desktopShortcut) WriteShortcut(DesktopLink);
        progress(97);

        var uninstallCmd = Path.Combine(InstallDir, "uninstall.cmd");
        File.WriteAllText(uninstallCmd, string.Join("\r\n",
            "@echo off",
            $"taskkill /im {AppName}.exe /f >nul 2>&1",
            $"del /q \"{StartMenuLink}\" >nul 2>&1",
            $"del /q \"{DesktopLink}\" >nul 2>&1",
            $"reg delete \"HKCU\\{UninstallKey}\" /f >nul 2>&1",
            // (goto) ends this script first so cmd isn't holding the file it deletes; retry for ~5 s because
            // a just-killed ShibaPlayer.exe stays locked for a moment.
            $"(goto) 2>nul & for /l %%i in (1,1,5) do if exist \"{InstallDir}\" (rmdir /s /q \"{InstallDir}\" 2>nul & if exist \"{InstallDir}\" ping -n 2 127.0.0.1 >nul)",
            ""));

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            var uninstall = $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /c \"{uninstallCmd}\"";
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "iaski");
            key.SetValue("DisplayIcon", AppExe);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("UninstallString", uninstall);
            key.SetValue("QuietUninstallString", uninstall);
            key.SetValue("EstimatedSize", (int)(new FileInfo(AppExe).Length / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        progress(100);
    }

    // WScript.Shell via late binding: the stock way to write a .lnk without extra interop assemblies.
    private static void WriteShortcut(string path)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        var shell = Activator.CreateInstance(shellType);
        var link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
        var linkType = link.GetType();
        void Set(string name, object value) => linkType.InvokeMember(name, BindingFlags.SetProperty, null, link, new[] { value });
        Set("TargetPath", AppExe);
        Set("WorkingDirectory", InstallDir);
        Set("IconLocation", AppExe + ",0");
        Set("Description", "Shiba-themed MP3 and M4A player");
        linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
    }
}

/// <summary>Shibaberg-styled installer window: fur sidebar with the shiba icon, cream body.</summary>
internal sealed class SetupForm : Form
{
    private static readonly Color Bg = Color.FromArgb(0xFF, 0xF6, 0xEA);
    private static readonly Color Fur = Color.FromArgb(0xE8, 0x92, 0x4A);
    private static readonly Color FurDark = Color.FromArgb(0xD2, 0x7A, 0x34);
    private static readonly Color Cream = Color.FromArgb(0xFF, 0xF2, 0xDF);
    private static readonly Color Surface2 = Color.FromArgb(0xFB, 0xEB, 0xD8);
    private static readonly Color Line = Color.FromArgb(0xEF, 0xD9, 0xBF);
    private static readonly Color Ink = Color.FromArgb(0x3A, 0x24, 0x18);
    private static readonly Color Muted = Color.FromArgb(0x9B, 0x7A, 0x60);
    private static readonly Color Ok = Color.FromArgb(0x3F, 0x9A, 0x58);
    private static readonly Color Err = Color.FromArgb(0xCF, 0x4A, 0x3F);

    private readonly Bitmap _shiba;
    private readonly Label _status;
    private readonly CheckBox _desktop;
    private readonly Button _primary;
    private readonly Button _secondary;
    private int _percent = -1;
    private bool _installed;

    public SetupForm()
    {
        _shiba = LargestIconFrame();
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        var previous = Program.InstalledVersion();
        var isUpdate = previous is not null;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"{Program.AppName} {Program.Version} Setup";
        ClientSize = new Size(600, 350);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        Font = new Font("Segoe UI", 9.75f);
        DoubleBuffered = true;

        const int x = 232, w = 340;
        Controls.Add(new Label
        {
            Text = isUpdate ? $"Update {Program.AppName}" : $"Install {Program.AppName}",
            Font = new Font("Segoe UI", 17f, FontStyle.Bold), ForeColor = Ink,
            Location = new Point(x, 28), Size = new Size(w, 38)
        });
        Controls.Add(new Label
        {
            Text = isUpdate && previous != Program.Version
                ? $"Version {previous}  →  {Program.Version}"
                : $"Version {Program.Version}",
            ForeColor = Muted, Location = new Point(x + 2, 66), Size = new Size(w, 20)
        });
        Controls.Add(new Label
        {
            Text = "Plays your MP3 and M4A music: albums, genres, playlists, shuffle and cover lookup.\n\n" +
                   "Installs just for you (no admin needed) and adds it to the Start menu.",
            ForeColor = Ink, Location = new Point(x + 2, 100), Size = new Size(w, 100)
        });

        _desktop = new CheckBox
        {
            Text = "Also add a desktop shortcut", ForeColor = Ink, FlatStyle = FlatStyle.System,
            Location = new Point(x + 2, 204), Size = new Size(w, 24)
        };
        Controls.Add(_desktop);

        _status = new Label { ForeColor = Muted, Location = new Point(x + 2, 238), Size = new Size(w, 20) };
        Controls.Add(_status);

        _primary = MakeButton(isUpdate ? "Update" : "Install", Fur, Color.White, new Point(x + w - 248, 294), 130);
        _secondary = MakeButton("Cancel", Surface2, Ink, new Point(x + w - 108, 294), 108);
        _primary.Click += Primary_Click;
        _secondary.Click += (_, _) => Close();
        Controls.Add(_primary);
        Controls.Add(_secondary);
        AcceptButton = _primary;
        CancelButton = _secondary;
        ResumeLayout(false);
    }

    /// <summary>
    /// app.ico stores every frame as PNG, which System.Drawing.Icon on .NET Framework can't decode
    /// ("Requested range extends past the end of the array"), so read the biggest frame's PNG bytes directly.
    /// </summary>
    private static Bitmap LargestIconFrame()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var ico = ms.ToArray();
        int count = BitConverter.ToUInt16(ico, 4), best = 0, bestSize = -1;
        for (var i = 0; i < count; i++)
        {
            var size = ico[6 + 16 * i] == 0 ? 256 : ico[6 + 16 * i];   // width byte 0 means 256
            if (size > bestSize) { bestSize = size; best = i; }
        }
        var length = BitConverter.ToInt32(ico, 6 + 16 * best + 8);
        var offset = BitConverter.ToInt32(ico, 6 + 16 * best + 12);
        return new Bitmap(new MemoryStream(ico, offset, length));
    }

    private static Button MakeButton(string text, Color back, Color fore, Point at, int width)
    {
        var b = new Button
        {
            Text = text, BackColor = back, ForeColor = fore, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold), Location = at, Size = new Size(width, 38),
            Cursor = Cursors.Hand, UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderColor = back == Fur ? Fur : Line;
        b.FlatAppearance.MouseOverBackColor = back == Fur ? Color.FromArgb(0xEE, 0x9F, 0x5D) : Color.FromArgb(0xF5, 0xDE, 0xC4);
        b.FlatAppearance.MouseDownBackColor = back == Fur ? FurDark : Line;
        return b;
    }

    private async void Primary_Click(object sender, EventArgs e)
    {
        if (_installed)
        {
            Process.Start(new ProcessStartInfo(Program.AppExe) { WorkingDirectory = Program.InstallDir, UseShellExecute = true });
            Close();
            return;
        }

        _primary.Enabled = _secondary.Enabled = _desktop.Enabled = false;
        SetStatus("Installing...", Muted);
        try
        {
            var desktop = _desktop.Checked;
            await Task.Run(() => Program.Install(desktop, p => BeginInvoke((Action)(() => { _percent = p; Invalidate(); }))));
            _installed = true;
            SetStatus($"All done! Find {Program.AppName} in the Start menu.", Ok);
            _primary.Text = "Launch";
            _secondary.Text = "Close";
            _primary.Enabled = _secondary.Enabled = true;
        }
        catch (Exception ex)
        {
            _percent = -1;
            Invalidate();
            SetStatus(ex.Message, Err);
            _primary.Enabled = _secondary.Enabled = _desktop.Enabled = true;
        }
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        float s = DeviceDpi / 96f;

        // fur sidebar with the shiba on a cream disc (same composition as the app)
        var side = new RectangleF(0, 0, 200 * s, ClientSize.Height);
        using (var b = new SolidBrush(Fur)) g.FillRectangle(b, side);
        using (var b = new SolidBrush(Cream)) g.FillEllipse(b, 30 * s, 40 * s, 140 * s, 140 * s);
        g.DrawImage(_shiba, 42 * s, 50 * s, 116 * s, 116 * s);
        using (var f = new Font("Segoe UI", 12.5f, FontStyle.Bold))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center })
        {
            g.DrawString(Program.AppName, f, Brushes.White, new RectangleF(0, 196 * s, side.Width, 30 * s), sf);
            using var small = new Font("Segoe UI", 8.5f);
            using var cream = new SolidBrush(Cream);
            g.DrawString($"v{Program.Version}  ·  made with paws", small, cream,
                new RectangleF(0, ClientSize.Height - 30 * s, side.Width, 20 * s), sf);
        }

        // progress bar under the status line
        if (_percent >= 0)
        {
            var track = new RectangleF(234 * s, 264 * s, 336 * s, 8 * s);
            using (var b = new SolidBrush(Surface2)) g.FillRectangle(b, track);
            using (var b = new SolidBrush(Fur)) g.FillRectangle(b, track.X, track.Y, track.Width * _percent / 100f, track.Height);
        }
    }
}
