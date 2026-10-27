// 3mfBatchRepair-setup.exe: installs 3mfBatchRepair and 3mfMeshSplit for the current Windows user (no admin rights), and uninstalls
// them again. The tools are embedded as resources by installer\build-installer.cmd.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

static class Setup
{
    public const string Version = "1.0";
    const int BytesPerKB = 1024;
    const int RemoveFolderDelaySeconds = 2;   // lets this exe exit before its folder is deleted
    public static readonly string[] Tools = { "3mfBatchRepair.exe", "3mfBatchRepair-gui.exe", "3mfMeshSplit.exe", "3mfMeshSplit-gui.exe" };
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\3mfBatchRepair";

    public static string InstallDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "3mfBatchRepair"); }
    }

    static string StartMenuDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "3mfBatchRepair"); }
    }

    static string Desktop { get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); } }

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static int Main(string[] args)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase))) return Uninstall() ? 0 : 1;
        Application.Run(new SetupForm());
        return 0;
    }

    // Tools from this install that are running would lock their files.
    public static string RunningTools()
    {
        string[] names = Process.GetProcesses().Where(p =>
        {
            if (!Tools.Contains(p.ProcessName + ".exe", StringComparer.OrdinalIgnoreCase)) return false;
            try { return p.MainModule.FileName.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }).Select(p => p.ProcessName).Distinct().ToArray();
        return names.Length == 0 ? null : string.Join(", ", names);
    }

    // The repair engine is part of Windows 10 and 11.
    public static bool HasRepairEngine()
    {
        return Type.GetType("Windows.Graphics.Printing3D.Printing3DModel, Windows.Graphics, ContentType=WindowsRuntime", false) != null;
    }

    public static void Install(bool startMenu, bool desktop, bool explorerMenu)
    {
        Directory.CreateDirectory(InstallDir);
        Assembly me = Assembly.GetExecutingAssembly();
        long bytes = 0;
        foreach (string name in Tools.Concat(new[] { "README.txt" }))
        {
            string target = Path.Combine(InstallDir, name);
            Paths.AtomicWrite(target, tmp =>
            {
                using (Stream src = me.GetManifestResourceStream(name))
                using (FileStream dst = File.Create(tmp))
                    src.CopyTo(dst);
            });
            bytes += new FileInfo(target).Length;
        }
        // A copy of this setup stays behind as the uninstaller.
        string uninstaller = Path.Combine(InstallDir, "uninstall.exe");
        if (!string.Equals(Application.ExecutablePath, uninstaller, StringComparison.OrdinalIgnoreCase))
            File.Copy(Application.ExecutablePath, uninstaller, true);
        bytes += new FileInfo(uninstaller).Length;

        RemoveShortcuts();
        if (startMenu) AddShortcuts(StartMenuDir);
        if (desktop) AddShortcuts(Desktop);

        string gui = Path.Combine(InstallDir, "3mfBatchRepair-gui.exe");
        if (explorerMenu) ShellMenu.Install(gui);
        else if (OurExplorerMenu()) ShellMenu.Uninstall();

        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "3mfBatchRepair");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "3mfBatchRepair");
            k.SetValue("DisplayIcon", gui);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
            k.SetValue("EstimatedSize", (int)(bytes / BytesPerKB), RegistryValueKind.DWord);   // Windows reads it as KB
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    static void AddShortcuts(string dir)
    {
        Directory.CreateDirectory(dir);
        Shortcut(Path.Combine(dir, "3mfBatchRepair.lnk"), "3mfBatchRepair-gui.exe", "Repair STL, OBJ and 3MF files");
        Shortcut(Path.Combine(dir, "3mfMeshSplit.lnk"), "3mfMeshSplit-gui.exe", "Split STL, OBJ and 3MF files into parts");
    }

    static bool OurExplorerMenu()
    {
        string exe = ShellMenu.InstalledExe();
        return exe != null && exe.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase);
    }

    static void Shortcut(string lnk, string exe, string description)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
        Type linkType = link.GetType();
        string target = Path.Combine(InstallDir, exe);
        linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
        linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { InstallDir });
        linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { description });
        linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
        linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        Marshal.FinalReleaseComObject(link);
        Marshal.FinalReleaseComObject(shell);
    }

    static void RemoveShortcuts()
    {
        if (Directory.Exists(StartMenuDir)) Directory.Delete(StartMenuDir, true);
        foreach (string name in new[] { "3mfBatchRepair.lnk", "3mfMeshSplit.lnk" })
        {
            string lnk = Path.Combine(Desktop, name);
            if (File.Exists(lnk)) File.Delete(lnk);
        }
    }

    static bool Uninstall()
    {
        if (MessageBox.Show("Remove 3mfBatchRepair and 3mfMeshSplit from this PC?", "3mfBatchRepair", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return false;
        string running = RunningTools();
        if (running != null)
        {
            MessageBox.Show("Close " + running + " first, then run the uninstaller again.", "3mfBatchRepair", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        try
        {
            if (OurExplorerMenu()) ShellMenu.Uninstall();
            RemoveShortcuts();
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            foreach (string name in Tools.Concat(new[] { "README.txt" }))
            {
                string f = Path.Combine(InstallDir, name);
                if (File.Exists(f)) File.Delete(f);
            }
            // This exe is running from the install folder, so a short-lived cmd removes it and the folder after exit.
            // ping is cmd's way to wait: it pauses a second between tries.
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c ping -n " + (RemoveFolderDelaySeconds + 1) + " 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception e)
        {
            MessageBox.Show("Couldn't remove everything: " + e.Message, "3mfBatchRepair", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        MessageBox.Show("3mfBatchRepair has been removed.\n\nRepair logs and speed calibration stay in\n" +
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "3mfBatchRepair") +
            "\n(delete that folder too if you don't want them).", "3mfBatchRepair", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }
}

class SetupForm : Form
{
    CheckBox startMenu, desktop, explorerMenu;
    Button install, cancel;
    Label status;
    bool installed;
    float scale = 1;

    const float BaseDpi = 96;          // 100% display scaling; S() sizes are written for it
    const int TextWidth = 420;         // px at BaseDpi before text wraps
    const float TitleFontScale = 1.4f; // the title against the dialog's font

    int S(int px) { return (int)Math.Round(px * scale); }

    public SetupForm()
    {
        Text = "3mfBatchRepair setup";
        Font = SystemFonts.MessageBoxFont;
        using (Graphics g = CreateGraphics()) scale = g.DpiX / BaseDpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(S(16));
        int textWidth = S(TextWidth);

        bool update = File.Exists(Path.Combine(Setup.InstallDir, "3mfBatchRepair-gui.exe"));
        FlowLayoutPanel col = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        col.Controls.Add(new Label
        {
            Text = "3mfBatchRepair " + Setup.Version,
            AutoSize = true,
            Font = new Font(Font.FontFamily, Font.Size * TitleFontScale, FontStyle.Bold),
            ForeColor = SystemColors.HotTrack,
        });
        col.Controls.Add(new Label
        {
            Text = "Repairs broken STL, OBJ and 3MF files with the repair engine built into Windows, and splits files that " +
                   "hold several parts into one STL per part.\n\nInstalls for this Windows user only; no administrator rights needed.",
            AutoSize = true,
            MaximumSize = new Size(textWidth, 0),
            Margin = new Padding(0, S(8), 0, S(8)),
        });
        col.Controls.Add(new Label { Text = "Installs to: " + Setup.InstallDir, AutoSize = true, MaximumSize = new Size(textWidth, 0), ForeColor = SystemColors.GrayText });

        startMenu = new CheckBox { Text = "Start menu shortcuts", Checked = true, AutoSize = true, Margin = new Padding(0, S(12), 0, 0) };
        desktop = new CheckBox { Text = "Desktop shortcuts", Checked = false, AutoSize = true };
        explorerMenu = new CheckBox { Text = "\"Repair with 3mfBatchRepair\" in Explorer's right-click menu", Checked = true, AutoSize = true };
        col.Controls.AddRange(new Control[] { startMenu, desktop, explorerMenu });

        status = new Label { AutoSize = true, MaximumSize = new Size(textWidth, 0), Margin = new Padding(0, S(12), 0, 0) };
        if (!Setup.HasRepairEngine())
        {
            status.Text = "This PC doesn't have the Windows 3D printing repair engine (Windows 10 or 11 needed). 3mfMeshSplit will work, 3mfBatchRepair won't.";
            status.ForeColor = Color.Firebrick;
        }
        col.Controls.Add(status);

        install = new Button { Text = update ? "Update" : "Install", AutoSize = true, Padding = new Padding(S(8), S(2), S(8), S(2)) };
        install.Font = new Font(Font, FontStyle.Bold);
        install.Click += (s, e) =>
        {
            if (!installed) DoInstall();
            else { Process.Start(Path.Combine(Setup.InstallDir, "3mfBatchRepair-gui.exe")); Close(); }
        };
        cancel = new Button { Text = "Cancel", AutoSize = true, Padding = new Padding(S(8), S(2), S(8), S(2)) };
        cancel.Click += (s, e) => Close();
        FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, S(12), 0, 0) };
        buttons.Controls.AddRange(new Control[] { install, cancel });
        col.Controls.Add(buttons);

        Controls.Add(col);
        AcceptButton = install;
        CancelButton = cancel;
    }

    void DoInstall()
    {
        string running = Setup.RunningTools();
        if (running != null)
        {
            MessageBox.Show(this, "Close " + running + " first.", "3mfBatchRepair setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            Cursor = Cursors.WaitCursor;
            Setup.Install(startMenu.Checked, desktop.Checked, explorerMenu.Checked);
        }
        catch (Exception ex)
        {
            Cursor = Cursors.Default;
            MessageBox.Show(this, "Install failed: " + ex.Message, "3mfBatchRepair setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Cursor = Cursors.Default;
        status.ForeColor = Color.ForestGreen;
        status.Text = "Installed. Find 3mfBatchRepair and 3mfMeshSplit in the Start menu" + (explorerMenu.Checked ? ", or right-click mesh files and folders." : ".");
        startMenu.Enabled = desktop.Enabled = explorerMenu.Enabled = false;
        installed = true;
        install.Text = "Open 3mfBatchRepair";
        cancel.Text = "Close";
    }
}
