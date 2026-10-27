// 3mfMeshSplit-gui: a small window for splitting STL/OBJ/3MF files into one STL per part, in the same style as 3mfBatchRepair-gui.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

static class SplitGuiProgram
{
    [STAThread]
    static void Main(string[] args)
    {
        SidebarForm.InitApplication();
        SplitForm form = new SplitForm();
        form.Shown += (s, e) => form.AddPaths(args);
        Application.Run(form);
    }
}

enum SplitState { Checking, Ready, Single, AlreadySplit, Unreadable, Splitting, Split, Failed }

class SplitRow
{
    public string File, Display;
    public long Size;
    public volatile string Parts = "";
    public volatile SplitState State = SplitState.Checking;
    public volatile string Message;   // the error, for Unreadable and Failed (set before State)
    public string PartsDir;

    public bool Done { get { return State == SplitState.Split; } }

    public string StatusText
    {
        get
        {
            switch (State)
            {
                case SplitState.Ready: return "ready";
                case SplitState.Single: return "nothing to split";
                case SplitState.AlreadySplit: return "already split";
                case SplitState.Unreadable: return "unreadable: " + Message;
                case SplitState.Splitting: return "splitting";
                case SplitState.Split: return "split";
                case SplitState.Failed: return "failed: " + Message;
                default: return "checking";
            }
        }
    }
}

class SplitForm : SidebarForm
{
    const int ColParts = 2, ColStatus = 3;
    const int RefreshMs = 300;   // how often the list catches up with the background work

    readonly List<SplitRow> rows = new List<SplitRow>();
    readonly Dictionary<SplitRow, ListViewItem> items = new Dictionary<SplitRow, ListViewItem>();
    ListView list;
    CheckBox keepOverlaps, dedupe, center, overwrite;
    Button split, openFolder;
    Label summary;
    Thread worker;
    volatile int analysisGeneration;   // bumped when options change, so stale analyses are ignored

    public SplitForm() : base("3mfMeshSplit - split STL, OBJ and 3MF files into parts", new Size(1000, 560), new Size(700, 420))
    {
        BuildList();
        BuildSidebar();
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = RefreshMs };
        timer.Tick += (s, e) => UpdateView();
        timer.Start();
        UpdateView();
    }

    bool Busy { get { return worker != null && worker.IsAlive; } }

    void BuildList()
    {
        list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
        list.Columns.Add("File", S(330));
        list.Columns.Add("Size", S(70), HorizontalAlignment.Right);
        list.Columns.Add("Parts", S(140));
        list.Columns.Add("Status", S(130));
        AcceptDrops(list);
        list.DoubleClick += (s, e) => OpenSelected();
        list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
        list.SelectedIndexChanged += (s, e) => UpdateView();
        Controls.Add(list);
    }

    void BuildSidebar()
    {
        int inner = S(230);
        Panel side;
        FlowLayoutPanel col = BuildSidebar(inner, "Add files to split", out side);

        keepOverlaps = Option("Keep overlapping pieces together", true,
            "Supports, hollow inner walls and sunk details stay with their part. Untick to split every piece.");
        dedupe = Option("One file per identical part", false,
            "Identical parts are written once, named with how many to print, e.g. Door_01 (print 4x).stl.");
        center = Option("Move parts to the origin", false, "Otherwise each part keeps its place on the original plate.");
        overwrite = Option("Replace earlier splits", false, "Redo files that already have a _parts folder.");
        Label options = Section("2   Options", false);
        tips.SetToolTip(options, "Parts go in a <name>_parts folder next to each file.");
        col.Controls.Add(options);
        col.Controls.AddRange(new Control[] { keepOverlaps, dedupe, center, overwrite });

        split = MakeButton("Split", (s, e) => StartSplit());
        split.Font = new Font(Font, FontStyle.Bold);
        openFolder = MakeButton("Open parts folder", (s, e) => OpenSelected());
        summary = new Label { AutoSize = true, MaximumSize = new Size(inner, 0), Margin = new Padding(0, S(8), 0, S(4)) };
        col.Controls.Add(Section("3   Split", false));
        col.Controls.Add(Row(split, openFolder));
        col.Controls.Add(summary);

        side.Controls.Add(col);
        Controls.Add(side);
    }

    CheckBox Option(string text, bool on, string tip)
    {
        CheckBox c = new CheckBox { Text = text, Checked = on, AutoSize = true, Margin = new Padding(0, S(3), 0, 0) };
        tips.SetToolTip(c, tip);
        c.CheckedChanged += (s, e) => Analyze(rows);   // options change the part counts
        return c;
    }

    SplitOptions Options()
    {
        return new SplitOptions { Strict = !keepOverlaps.Checked, Dedupe = dedupe.Checked, Center = center.Checked, Overwrite = overwrite.Checked };
    }

    // Each file's part name, from the whole list in order, so two files can't write over each other's parts.
    Dictionary<string, string> PartNames(SplitOptions o)
    {
        return Splitter.PartNames(rows.Select(r => r.File), o);
    }

    // ---------- files ----------

    public override void AddPaths(string[] paths)
    {
        if (paths == null || paths.Length == 0 || Busy) return;
        HashSet<string> have = new HashSet<string>(rows.Select(r => r.File), StringComparer.OrdinalIgnoreCase);
        List<SplitRow> added = new List<SplitRow>();
        foreach (string raw in paths)
        {
            string p = Paths.Long(raw);
            string root = File.Exists(p) ? Path.GetDirectoryName(p) : Path.GetDirectoryName(p.TrimEnd('\\'));
            IEnumerable<string> files = File.Exists(p) ? new[] { p } : Directory.Exists(p) ? Splitter.Walk(p) : Enumerable.Empty<string>();
            foreach (string f in files)
            {
                if (!have.Add(f)) continue;
                SplitRow r = new SplitRow { File = f, Size = new FileInfo(f).Length, Display = f.Substring(root.Length).TrimStart('\\') };
                ListViewItem item = new ListViewItem(new[] { r.Display, Fmt.Bytes(r.Size), "", r.StatusText }) { Tag = r, ToolTipText = Paths.Short(f) };
                rows.Add(r);
                items[r] = item;
                list.Items.Add(item);
                added.Add(r);
            }
        }
        Analyze(added);
    }

    protected override void RemoveSelected()
    {
        if (Busy) return;
        foreach (ListViewItem item in list.SelectedItems.Cast<ListViewItem>().ToList())
        {
            rows.Remove((SplitRow)item.Tag);
            items.Remove((SplitRow)item.Tag);
            list.Items.Remove(item);
        }
        UpdateView();
    }

    protected override void ClearAll()
    {
        if (Busy) return;
        rows.Clear();
        items.Clear();
        list.Items.Clear();
        UpdateView();
    }

    void OpenSelected()
    {
        SplitRow r = list.SelectedItems.Count > 0 ? (SplitRow)list.SelectedItems[0].Tag : rows.LastOrDefault(x => x.PartsDir != null);
        if (r == null) return;
        string dir = r.PartsDir ?? Splitter.PartsDir(r.File, Options());
        if (Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + Paths.Short(dir) + "\"");
        else ShowInExplorer(r.File);
    }

    // ---------- analysing and splitting ----------

    // Works out each file's parts in the background so the list shows what a split will produce.
    void Analyze(IEnumerable<SplitRow> which)
    {
        if (Busy) return;
        List<SplitRow> todo = which.Where(r => !r.Done).ToList();
        foreach (SplitRow r in todo) { r.State = SplitState.Checking; r.Parts = ""; }
        int generation = ++analysisGeneration;
        SplitOptions o = Options();
        Dictionary<string, string> names = PartNames(o);
        Thread t = new Thread(() =>
        {
            foreach (SplitRow r in todo)
            {
                if (generation != analysisGeneration) return;
                try
                {
                    SplitPlan p = Splitter.Plan(r.File, o, names[r.File]);
                    if (generation != analysisGeneration) return;
                    r.Parts = Describe(p);
                    r.State = p.Single && p.Copies == 0 ? SplitState.Single : p.AlreadySplit ? SplitState.AlreadySplit : SplitState.Ready;
                }
                catch (Exception e)
                {
                    r.Parts = "";
                    r.Message = Paths.Message(e);
                    r.State = SplitState.Unreadable;
                }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    static string Describe(SplitPlan p)
    {
        if (p.Single && p.Copies == 0) return "1 part";
        string s = p.Parts.Count + " parts";
        if (p.Copies > 0) s += string.Format(" (+{0} {1})", p.Copies, p.Copies == 1 ? "copy" : "copies");
        return s;
    }

    bool WillSplit(SplitRow r, bool overwrite)
    {
        return r.State == SplitState.Ready || r.State == SplitState.AlreadySplit && overwrite;
    }

    void StartSplit()
    {
        if (Busy) return;
        analysisGeneration++;   // stop any analysis still running
        SplitOptions o = Options();
        Dictionary<string, string> names = PartNames(o);
        List<SplitRow> todo = rows.Where(r => WillSplit(r, o.Overwrite) || r.State == SplitState.Checking).ToList();
        worker = new Thread(() =>
        {
            foreach (SplitRow r in todo)
            {
                r.State = SplitState.Splitting;
                try
                {
                    SplitPlan p = Splitter.Plan(r.File, o, names[r.File]);
                    if (p.Single && p.Copies == 0) { r.State = SplitState.Single; continue; }
                    if (p.AlreadySplit && !o.Overwrite) { r.State = SplitState.AlreadySplit; continue; }
                    Splitter.Write(p, o);
                    r.Parts = Describe(p);
                    r.PartsDir = p.Dir;
                    r.State = SplitState.Split;
                }
                catch (Exception e)
                {
                    r.Message = Paths.Message(e);
                    r.State = SplitState.Failed;
                }
            }
        });
        worker.IsBackground = true;
        worker.Start();
        UpdateView();
    }

    void UpdateView()
    {
        foreach (SplitRow r in rows)
        {
            ListViewItem item = items[r];
            string status = r.StatusText;
            if (item.SubItems[ColParts].Text != r.Parts) item.SubItems[ColParts].Text = r.Parts;
            if (item.SubItems[ColStatus].Text != status) item.SubItems[ColStatus].Text = status;
            Color c = r.State == SplitState.Failed || r.State == SplitState.Unreadable ? Color.Firebrick
                : r.Done ? Color.ForestGreen
                : r.State == SplitState.Single || r.State == SplitState.AlreadySplit ? SystemColors.GrayText
                : SystemColors.WindowText;
            if (item.ForeColor != c) item.ForeColor = c;
        }
        bool busy = Busy;
        int ready = rows.Count(r => WillSplit(r, overwrite.Checked));
        split.Enabled = !busy && ready > 0;
        addFiles.Enabled = addFolder.Enabled = clear.Enabled = keepOverlaps.Enabled = dedupe.Enabled = center.Enabled = overwrite.Enabled = !busy;
        remove.Enabled = !busy && list.SelectedItems.Count > 0;
        openFolder.Enabled = rows.Any(r => r.PartsDir != null) || list.SelectedItems.Count > 0;
        list.AllowDrop = !busy;

        string text;
        if (rows.Count == 0) text = "Add files or folders to get started.";
        else if (busy) text = string.Format("Splitting… {0} done", rows.Count(r => r.Done));
        else if (rows.Any(r => r.State == SplitState.Checking))
            text = string.Format("Checking files… {0}/{1}", rows.Count(r => r.State != SplitState.Checking), rows.Count);
        else
        {
            int done = rows.Count(r => r.Done), failed = rows.Count(r => r.State == SplitState.Failed);
            text = string.Format("{0} file(s) · {1} to split", rows.Count, ready);
            if (done > 0) text += string.Format(" · {0} split", done);
            if (failed > 0) text += string.Format(" · {0} failed", failed);
        }
        if (summary.Text != text) summary.Text = text;
    }
}
