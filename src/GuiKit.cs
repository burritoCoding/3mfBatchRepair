// What 3mfBatchRepair-gui and 3mfMeshSplit-gui share: a file list on the right and every control in one left column, top to
// bottom in the order they're used, starting with a section for adding files.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

abstract class SidebarForm : Form
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    // Call first thing in Main, before any window exists.
    public static void InitApplication()
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    const float BaseDpi = 96;              // 100% display scaling; S() sizes are written for it
    const float SectionFontScale = 1.15f;  // section headings against the window's font

    protected readonly ToolTip tips = new ToolTip();
    protected Button addFiles, addFolder, remove, clear;
    readonly float scale = 1;

    protected SidebarForm(string title, Size client, Size minimum)
    {
        Text = title;
        Font = SystemFonts.MessageBoxFont;
        using (Graphics g = CreateGraphics()) scale = g.DpiX / BaseDpi;
        ClientSize = new Size(S(client.Width), S(client.Height));
        MinimumSize = new Size(S(minimum.Width), S(minimum.Height));
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }
    }

    // Pixels at 96 DPI scaled to the screen's.
    protected int S(int px) { return (int)Math.Round(px * scale); }

    public abstract void AddPaths(string[] paths);
    protected abstract void RemoveSelected();
    protected abstract void ClearAll();

    // Lets files and folders be dropped on the list.
    protected void AcceptDrops(ListView list)
    {
        list.AllowDrop = true;
        list.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        list.DragDrop += (s, e) => AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));
    }

    // The left column, already holding the "1   Add files" section; `side` is the panel to dock it in.
    protected FlowLayoutPanel BuildSidebar(int inner, string pickTitle, out Panel side)
    {
        // Room for a scrollbar too, so a short window scrolls the column instead of clipping it.
        side = new Panel { Dock = DockStyle.Left, Width = inner + S(48), Padding = new Padding(S(12), S(2), S(8), S(8)), AutoScroll = true };
        FlowLayoutPanel col = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };

        addFiles = MakeButton("Add files…", (s, e) => PickFiles(pickTitle));
        addFolder = MakeButton("Add folder…", (s, e) => PickFolder());
        remove = MakeButton("Remove", (s, e) => RemoveSelected());
        clear = MakeButton("Clear", (s, e) => ClearAll());
        Label add = Section("1   Add files", true);
        tips.SetToolTip(add, "You can also drop files and folders on the list.");
        col.Controls.Add(add);
        col.Controls.Add(Row(addFiles, addFolder));
        col.Controls.Add(Row(remove, clear));
        return col;
    }

    protected Label Section(string text, bool first)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(Font.FontFamily, Font.Size * SectionFontScale, FontStyle.Bold),
            ForeColor = SystemColors.HotTrack,
            Margin = new Padding(0, first ? S(6) : S(12), 0, S(3)),
        };
    }

    protected FlowLayoutPanel Row(params Control[] controls)
    {
        FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    protected Button MakeButton(string text, EventHandler onClick)
    {
        Button b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(6), S(2), S(6), S(2)) };
        b.Click += onClick;
        return b;
    }

    void PickFiles(string title)
    {
        using (OpenFileDialog d = new OpenFileDialog { Multiselect = true, Filter = "Meshes (*.stl;*.obj;*.3mf)|*.stl;*.obj;*.3mf|All files|*.*", Title = title })
            if (d.ShowDialog(this) == DialogResult.OK) AddPaths(d.FileNames);
    }

    void PickFolder()
    {
        using (FolderBrowserDialog d = new FolderBrowserDialog { Description = "Add a folder (its subfolders are included)", ShowNewFolderButton = false })
            if (d.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { d.SelectedPath });
    }

    // Opens Explorer with the file selected.
    protected static void ShowInExplorer(string path)
    {
        Process.Start("explorer.exe", "/select,\"" + Paths.Short(path) + "\"");
    }
}
