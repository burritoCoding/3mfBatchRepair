// 3mfBatchRepair-gui: a window for picking files/folders, watching repairs as they run, and the target of
// Explorer's "Repair with 3mfBatchRepair" right-click entry.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class GuiProgram
{
    [STAThread]
    static void Main(string[] args)
    {
        if (!SingleInstance.BecomePrimary(args)) return;   // handed the paths to the window that's already open
        SidebarForm.InitApplication();
        MainForm form = new MainForm();
        SingleInstance.Listen(paths => form.BeginInvoke(new Action(() => form.AddPaths(paths, true))));
        form.Shown += (s, e) => form.AddPaths(args, false);
        Application.Run(form);
    }
}

// Explorer starts one process per selected item, so the first one opens the window and the rest pass
// their paths to it over a named pipe.
static class SingleInstance
{
    static Mutex mutex;
    static readonly string PipeName = "3mfBatchRepair-gui-" + Environment.UserName;

    // How long a second process keeps trying to reach the first window before opening its own (it may still be
    // starting up), how long each try waits, and the pause after a failed one.
    const int GiveUpSeconds = 10, ConnectTimeoutMs = 1000, RetryPauseMs = 100;

    public static bool BecomePrimary(string[] args)
    {
        bool created;
        mutex = new Mutex(true, @"Local\3mfBatchRepair-gui", out created);
        if (created) return true;
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < GiveUpSeconds)
        {
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    pipe.Connect(ConnectTimeoutMs);
                    using (StreamWriter w = new StreamWriter(pipe, new UTF8Encoding(false)))
                        foreach (string a in args) w.WriteLine(Path.GetFullPath(a));
                    return false;
                }
            }
            catch (TimeoutException) { }
            catch (IOException) { Thread.Sleep(RetryPauseMs); }
        }
        return true;   // the other window isn't answering; open our own
    }

    public static void Listen(Action<string[]> onPaths)
    {
        Thread t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using (NamedPipeServerStream pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances))
                    {
                        pipe.WaitForConnection();
                        List<string> paths = new List<string>();
                        using (StreamReader r = new StreamReader(pipe, Encoding.UTF8))
                            for (string line; (line = r.ReadLine()) != null; )
                                if (line.Length > 0) paths.Add(line);
                        if (paths.Count > 0) onPaths(paths.ToArray());
                    }
                }
                catch (IOException) { }
                catch (InvalidOperationException) { return; }   // window closed
            }
        });
        t.IsBackground = true;
        t.Start();
    }
}

class BufferedListView : ListView
{
    public BufferedListView() { DoubleBuffered = true; }
}

class MainForm : SidebarForm
{
    const int ColFile = 0, ColSize = 1, ColProblems = 2, ColEstimate = 3, ColStatus = 4, ColProgress = 5, ColTime = 6;
    const int RefreshMs = 500;   // how often the list catches up with running jobs
    static readonly Color BarColor = Color.FromArgb(76, 145, 221);   // progress bar fill, a mid blue
    const int BarInset = 3;                                          // px between the progress bar and its cell

    readonly List<FileJob> jobs = new List<FileJob>();
    readonly Dictionary<FileJob, ListViewItem> items = new Dictionary<FileJob, ListViewItem>();
    readonly HashSet<FileJob> dirty = new HashSet<FileJob>();
    readonly Dictionary<FileJob, int> addOrder = new Dictionary<FileJob, int>();   // tie-break for sorting
    readonly HashSet<FileJob> tickedByUser = new HashSet<FileJob>();   // ticks the user changed; never overridden
    readonly HashSet<FileJob> autoTicked = new HashSet<FileJob>();     // ticks already set from the file check
    bool settingTicks;                                                 // true while code (not the user) changes ticks
    int sortColumn = ColEstimate;
    bool sortDescending;
    static readonly string[] ColumnNames = { "File", "Size", "Problems", "Est. repair", "Status", "Progress", "Time" };
    List<FileJob> runJobs = new List<FileJob>();
    Engine engine;
    Thread runner;
    readonly Stopwatch runClock = new Stopwatch();
    bool closeWhenDone;

    BufferedListView list;
    RadioButton inPlace, toFolder;
    CheckBox backup, checkOnly;
    TextBox outDir;
    Button browse, start, stop, abort, openLog, tickAll, tickNone, tickBroken;
    NumericUpDown jobCount;
    Label summary;
    CheckBox explorerMenu;
    int calibrationSeen = -1;
    System.Windows.Forms.Timer timer;

    public MainForm() : base("3mfBatchRepair - repair STL, OBJ and 3MF meshes", new Size(1260, 720), new Size(760, 420))
    {
        BuildList();
        BuildSidebar();

        timer = new System.Windows.Forms.Timer { Interval = RefreshMs };
        timer.Tick += (s, e) => Tick();
        timer.Start();
        UpdateButtons();
        UpdateSummary();
    }

    // ---------- layout ----------

    void BuildList()
    {
        list = new BufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            OwnerDraw = true,
            HideSelection = false,
            CheckBoxes = true,
        };
        int[] widths = { 280, 66, 200, 82, 100, 125, 56 };
        for (int i = 0; i < ColumnNames.Length; i++)
            list.Columns.Add(ColumnNames[i], S(widths[i]), i == ColSize || i == ColEstimate || i == ColTime ? HorizontalAlignment.Right : HorizontalAlignment.Left);
        ShowSortArrow();
        list.DrawColumnHeader += (s, e) => e.DrawDefault = true;
        list.DrawItem += (s, e) => { };
        list.DrawSubItem += DrawSubItem;
        list.ColumnClick += (s, e) =>
        {
            if (e.Column == sortColumn) sortDescending = !sortDescending;
            else { sortColumn = e.Column; sortDescending = false; }
            ShowSortArrow();
            SortRows();
        };
        // Ticks choose which files a run handles; they're locked while it runs.
        list.ItemCheck += (s, e) => { if (Running && !settingTicks) e.NewValue = e.CurrentValue; };
        list.ItemChecked += (s, e) =>
        {
            if (settingTicks) return;
            tickedByUser.Add((FileJob)e.Item.Tag);
            UpdateSummary();
            UpdateButtons();
        };
        AcceptDrops(list);
        list.DoubleClick += (s, e) => { if (list.SelectedItems.Count == 1) ShowInExplorer(((FileJob)list.SelectedItems[0].Tag).Input); };
        list.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Delete) RemoveSelected();
            else if (e.KeyCode == Keys.Space && list.SelectedItems.Count > 1)
            {
                // Space toggles every selected row together, following the focused one.
                bool tick = list.FocusedItem == null || !list.FocusedItem.Checked;
                TickSelected(tick);
                e.Handled = e.SuppressKeyPress = true;
            }
        };
        list.SelectedIndexChanged += (s, e) => UpdateButtons();

        ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripItem check = menu.Items.Add("Check", null, (s, e) => TickSelected(true));
        ToolStripItem uncheck = menu.Items.Add("Uncheck", null, (s, e) => TickSelected(false));
        menu.Items.Add(new ToolStripSeparator());
        ToolStripItem show = menu.Items.Add("Show in Explorer", null, (s, e) => { if (list.SelectedItems.Count > 0) ShowInExplorer(((FileJob)list.SelectedItems[0].Tag).Input); });
        ToolStripItem removeItem = menu.Items.Add("Remove from list", null, (s, e) => RemoveSelected());
        menu.Opening += (s, e) =>
        {
            bool any = list.SelectedItems.Count > 0;
            check.Enabled = uncheck.Enabled = removeItem.Enabled = any && !Running;
            show.Enabled = any;
        };
        list.ContextMenuStrip = menu;
        Controls.Add(list);
    }

    void ShowSortArrow()
    {
        for (int i = 0; i < list.Columns.Count; i++)
            list.Columns[i].Text = ColumnNames[i] + (i == sortColumn ? (sortDescending ? " ▼" : " ▲") : "");
    }

    // Add files, choose which ones, pick where results go, run. The list on the right is just to look at (and tick).
    void BuildSidebar()
    {
        int inner = S(250);
        Panel side;
        FlowLayoutPanel col = BuildSidebar(inner, "Add files to repair", out side);

        tickAll = MakeButton("All", (s, e) => SetTicks(j => true));
        tickNone = MakeButton("None", (s, e) => SetTicks(j => false));
        tickBroken = MakeButton("Needs repair", (s, e) => SetTicks(NeedsRepair));
        Label choose = Section("2   Choose", false);
        tips.SetToolTip(choose, "Checked files run top to bottom. Click a column header to reorder.");
        col.Controls.Add(choose);
        col.Controls.Add(Row(tickAll, tickNone, tickBroken));

        inPlace = new RadioButton { Text = "Repair in place", Checked = true, AutoSize = true, Margin = new Padding(0, S(2), 0, 0) };
        backup = new CheckBox { Text = "Keep originals in the Recycle Bin", Checked = true, AutoSize = true, Margin = new Padding(S(18), 0, 0, S(4)) };
        toFolder = new RadioButton { Text = "Save to another folder", AutoSize = true, Margin = new Padding(0, S(2), 0, 0) };
        outDir = new TextBox { Width = inner - S(18) - S(78), Enabled = false, Margin = new Padding(0, S(4), S(4), 0) };
        browse = MakeButton("Browse…", (s, e) => BrowseOut());
        browse.Enabled = false;
        inPlace.CheckedChanged += (s, e) => UpdateButtons();
        jobCount = new NumericUpDown { Minimum = 1, Maximum = Environment.ProcessorCount, Value = new EngineOptions().Jobs, Width = S(50), Margin = new Padding(S(6), S(3), 0, 0) };
        checkOnly = new CheckBox { Text = "Check only (change nothing)", AutoSize = true, Margin = new Padding(0, S(6), 0, 0) };
        checkOnly.CheckedChanged += (s, e) => UpdateButtons();
        col.Controls.Add(Section("3   Output", false));
        col.Controls.Add(inPlace);
        col.Controls.Add(backup);
        col.Controls.Add(toFolder);
        FlowLayoutPanel folderRow = Row(outDir, browse);
        folderRow.Margin = new Padding(S(18), 0, 0, 0);
        col.Controls.Add(folderRow);
        Label atOnce = new Label { Text = "Files at once:", AutoSize = true, Margin = new Padding(0, S(6), 0, 0) };
        tips.SetToolTip(atOnce, "Each repair uses one CPU core.");
        tips.SetToolTip(jobCount, "Each repair uses one CPU core.");
        col.Controls.Add(Row(atOnce, jobCount));
        col.Controls.Add(checkOnly);

        start = MakeButton("Start", (s, e) => Start());
        start.Font = new Font(Font, FontStyle.Bold);
        stop = MakeButton("Stop after current", (s, e) => { engine.Stop(); UpdateButtons(); UpdateSummary(); });
        abort = MakeButton("Abort", (s, e) => { engine.Abort(); UpdateButtons(); UpdateSummary(); });
        summary = new Label { AutoSize = true, MaximumSize = new Size(inner, 0), Margin = new Padding(0, S(8), 0, S(4)) };
        openLog = MakeButton("Open log", (s, e) => OpenLog());
        col.Controls.Add(Section("4   Run", false));
        col.Controls.Add(Row(start, stop, abort));
        col.Controls.Add(summary);
        col.Controls.Add(openLog);

        // A setting, not part of the workflow: pinned to the bottom of the column, below a divider.
        explorerMenu = new CheckBox
        {
            Text = "Show in Explorer's right-click menu",
            Checked = ExplorerMenuIsOurs(),
            AutoSize = true,
            Dock = DockStyle.Bottom,
            Padding = new Padding(0, S(8), 0, 0),
        };
        explorerMenu.Click += (s, e) => SetExplorerMenu(explorerMenu.Checked);
        tips.SetToolTip(explorerMenu, "Adds \"Repair with 3mfBatchRepair\" to the right-click menu of .stl/.obj/.3mf files and folders.");
        Label divider = new Label { Dock = DockStyle.Bottom, Height = 1, BackColor = SystemColors.ControlDark };

        side.Controls.Add(col);
        side.Controls.Add(divider);
        side.Controls.Add(explorerMenu);
        Controls.Add(side);
    }

    // ---------- adding files ----------

    bool Running { get { return runner != null && runner.IsAlive; } }

    public override void AddPaths(string[] paths) { AddPaths(paths, false); }

    public void AddPaths(string[] paths, bool fromOtherInstance)
    {
        if (paths == null || paths.Length == 0) return;
        if (fromOtherInstance)
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }
        List<string> missing = new List<string>();
        Cursor = Cursors.WaitCursor;
        List<FileJob> found;
        try { found = Engine.Discover(paths, missing.Add); }
        finally { Cursor = Cursors.Default; }
        HashSet<string> have = new HashSet<string>(jobs.Select(j => j.Input), StringComparer.OrdinalIgnoreCase);
        list.BeginUpdate();
        settingTicks = true;
        foreach (FileJob j in found)
        {
            if (!have.Add(j.Input)) continue;
            jobs.Add(j);
            addOrder[j] = addOrder.Count;
            // Ticked until the check says otherwise: clean files untick themselves (see Tick).
            ListViewItem item = new ListViewItem(new[] { Path.Combine(Path.GetFileName(j.Root), j.Display), Fmt.Bytes(j.Size), "", "", "checking", "", "" })
            {
                Tag = j,
                ToolTipText = Paths.Short(j.Input),
                Checked = true,
            };
            items[j] = item;
            list.Items.Add(item);
            lock (toCheck) toCheck.Enqueue(j);
        }
        settingTicks = false;
        list.EndUpdate();
        StartChecker();
        UpdateButtons();
        UpdateSummary();
        if (missing.Count > 0)
            MessageBox.Show(this, "Not found:\n" + string.Join("\n", missing.ToArray()), "3mfBatchRepair", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else if (found.Count == 0)
            MessageBox.Show(this, "No .stl, .obj or .3mf files there.", "3mfBatchRepair", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------- checking files as they're added ----------

    readonly Queue<FileJob> toCheck = new Queue<FileJob>();
    bool checkerRunning, wasChecking;

    bool Checking { get { lock (toCheck) return checkerRunning; } }

    void StartChecker()
    {
        lock (toCheck)
        {
            if (checkerRunning || toCheck.Count == 0) return;
            checkerRunning = true;
        }
        int workers = new EngineOptions().Jobs;
        Thread t = new Thread(() =>
        {
            while (true)
            {
                List<FileJob> batch;
                lock (toCheck)
                {
                    if (toCheck.Count == 0) { checkerRunning = false; return; }
                    batch = toCheck.ToList();
                    toCheck.Clear();
                }
                Engine.ForEachInOrder(batch, workers, j => { Engine.Inspect(j); MarkDirty(j); });
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    static bool NeedsRepair(FileJob j) { return j.Before != null && !j.Before.Clean && j.Status == null; }

    bool Ticked(FileJob j) { return items[j].Checked; }

    void SetTick(FileJob j, bool tick)
    {
        if (items[j].Checked == tick) return;
        settingTicks = true;
        items[j].Checked = tick;
        settingTicks = false;
    }

    // Tick buttons: the user's choice, so these count as user ticks.
    void SetTicks(Func<FileJob, bool> tick)
    {
        if (Running) return;
        list.BeginUpdate();
        foreach (FileJob j in jobs)
        {
            SetTick(j, tick(j));
            tickedByUser.Add(j);
        }
        list.EndUpdate();
        UpdateSummary();
        UpdateButtons();
    }

    void TickSelected(bool tick)
    {
        if (Running) return;
        foreach (ListViewItem item in list.SelectedItems)
        {
            SetTick((FileJob)item.Tag, tick);
            tickedByUser.Add((FileJob)item.Tag);
        }
        UpdateSummary();
        UpdateButtons();
    }

    // Once a file has been checked, untick it if there's nothing to repair, unless the user has set its tick.
    void AutoTick(FileJob j)
    {
        if (Running || tickedByUser.Contains(j) || j.Before == null && j.Error == null) return;
        SetTick(j, NeedsRepair(j));
        autoTicked.Add(j);
    }

    // Sorts by the chosen column. Rows without a value for it (not checked yet, nothing to repair) stay at the
    // bottom in either direction. Runs repair the ticked files top to bottom, so this is also the repair order.
    void SortRows()
    {
        Comparison<FileJob> byColumn;
        switch (sortColumn)
        {
            case ColFile: byColumn = (a, b) => ExplorerOrder.Instance.Compare(items[a].Text, items[b].Text); break;
            case ColSize: byColumn = (a, b) => a.Size.CompareTo(b.Size); break;
            case ColProblems: byColumn = (a, b) => a.Before.BadEdges.CompareTo(b.Before.BadEdges); break;
            case ColEstimate: byColumn = (a, b) => a.Estimate.CompareTo(b.Estimate); break;
            case ColStatus: byColumn = (a, b) => string.Compare(items[a].SubItems[ColStatus].Text, items[b].SubItems[ColStatus].Text, StringComparison.OrdinalIgnoreCase); break;
            case ColProgress: byColumn = (a, b) => a.Progress.CompareTo(b.Progress); break;
            default: byColumn = (a, b) => TimeOf(a).CompareTo(TimeOf(b)); break;
        }
        List<FileJob> sorted = jobs.ToList();
        sorted.Sort((a, b) =>
        {
            bool ha = HasValue(a), hb = HasValue(b);
            if (ha != hb) return ha ? -1 : 1;
            int c = ha ? byColumn(a, b) : 0;
            if (sortDescending) c = -c;
            return c != 0 ? c : addOrder[a].CompareTo(addOrder[b]);
        });
        if (sorted.SequenceEqual(jobs)) return;
        jobs.Clear();
        jobs.AddRange(sorted);
        ListViewItem focused = list.FocusedItem;
        list.BeginUpdate();
        list.Items.Clear();
        settingTicks = true;   // re-adding items re-raises their tick events
        list.Items.AddRange(jobs.Select(j => items[j]).ToArray());
        settingTicks = false;
        list.EndUpdate();
        if (focused != null && focused.ListView == list) focused.EnsureVisible();
    }

    bool HasValue(FileJob j)
    {
        switch (sortColumn)
        {
            case ColProblems: return j.Before != null;
            case ColEstimate: return j.Before != null && !j.Before.Clean;
            case ColTime: return TimeOf(j) > 0;
            default: return true;
        }
    }

    static double TimeOf(FileJob j) { return j.State == JobState.Done ? j.Seconds : j.State == JobState.Running ? j.Elapsed : 0; }

    // The ticked files that need repair, in list order, and how long they should take run that way.
    string RepairOutlook()
    {
        List<FileJob> ticked = jobs.Where(Ticked).ToList();
        List<double> est = ticked.Where(NeedsRepair).Select(j => j.Estimate).ToList();
        int unreadable = jobs.Count(j => j.Before == null && j.Error != null);
        string text = string.Format("{0} checked · ", ticked.Count) + (est.Count == 0 ? "nothing checked needs repair"
            : string.Format("{0} to repair, est. ~{1} at {2} at once (longest ~{3})", est.Count,
                Fmt.Duration(Engine.EstimateWall(est, (int)jobCount.Value)), jobCount.Value, Fmt.Duration(est.Max())));
        return unreadable > 0 ? text + string.Format(" · {0} unreadable", unreadable) : text;
    }

    void BrowseOut()
    {
        using (FolderBrowserDialog d = new FolderBrowserDialog { Description = "Save repaired files to", ShowNewFolderButton = true })
            if (d.ShowDialog(this) == DialogResult.OK) outDir.Text = d.SelectedPath;
    }

    protected override void RemoveSelected()
    {
        if (Running) return;
        list.BeginUpdate();
        foreach (ListViewItem item in list.SelectedItems.Cast<ListViewItem>().ToList())
        {
            FileJob j = (FileJob)item.Tag;
            jobs.Remove(j);
            items.Remove(j);
            addOrder.Remove(j);
            tickedByUser.Remove(j);
            autoTicked.Remove(j);
            list.Items.Remove(item);
        }
        list.EndUpdate();
        UpdateButtons();
        UpdateSummary();
    }

    protected override void ClearAll()
    {
        if (Running) return;
        jobs.Clear();
        items.Clear();
        addOrder.Clear();
        tickedByUser.Clear();
        autoTicked.Clear();
        list.Items.Clear();
        runJobs = new List<FileJob>();
        UpdateButtons();
        UpdateSummary();
    }

    // ---------- running ----------

    void Start()
    {
        if (Running || jobs.Count == 0) return;
        List<FileJob> ticked = jobs.Where(Ticked).ToList();   // in list order: that's the repair order
        if (ticked.Count == 0)
        {
            MessageBox.Show(this, "Check the files you want to run.", "3mfBatchRepair", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        EngineOptions opt = new EngineOptions
        {
            Jobs = (int)jobCount.Value,
            CheckOnly = checkOnly.Checked,
            Backup = backup.Checked,
            EasiestFirst = false,
        };
        if (toFolder.Checked)
        {
            if (outDir.Text.Trim().Length == 0)
            {
                MessageBox.Show(this, "Pick a folder to save the repaired files to.", "3mfBatchRepair", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            opt.OutDir = outDir.Text.Trim();
        }
        else if (!opt.Backup && !opt.CheckOnly &&
            MessageBox.Show(this, "Broken originals will be overwritten with no backup. Continue?", "3mfBatchRepair",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        engine = new Engine(opt);
        engine.JobStarted += MarkDirty;
        engine.Repairing += MarkDirty;
        engine.JobFinished += MarkDirty;
        runJobs = engine.Plan(ticked);
        foreach (FileJob j in jobs) UpdateItem(j);
        runClock.Restart();
        Engine e = engine;
        List<FileJob> plan = runJobs;
        runner = new Thread(() => e.Run(plan)) { IsBackground = true };
        runner.Start();
        UpdateButtons();
        UpdateSummary();
    }

    void MarkDirty(FileJob j)
    {
        lock (dirty) dirty.Add(j);
    }

    void Tick()
    {
        List<FileJob> changed;
        lock (dirty) { changed = dirty.ToList(); dirty.Clear(); }
        foreach (FileJob j in runJobs.Where(j => j.State == JobState.Running)) if (!changed.Contains(j)) changed.Add(j);
        foreach (FileJob j in changed)
        {
            UpdateItem(j);
            if (!autoTicked.Contains(j)) AutoTick(j);
        }

        // A finished repair can change this PC's speed factor, and with it every estimate on screen.
        int calibration = Calibration.Version;
        if (calibration != calibrationSeen)
        {
            calibrationSeen = calibration;
            foreach (FileJob j in jobs) UpdateItem(j);
        }

        bool checking = Checking;
        if (wasChecking && !checking) SortRows();
        wasChecking = checking;
        UpdateButtons();

        if (runner != null && !runner.IsAlive && runClock.IsRunning)
        {
            runClock.Stop();
            foreach (FileJob j in runJobs) UpdateItem(j);
            UpdateButtons();
            if (closeWhenDone) { Close(); return; }
        }
        UpdateSummary();
    }

    void UpdateItem(FileJob j)
    {
        ListViewItem item;
        if (!items.TryGetValue(j, out item)) return;
        string status, progress = "", time = "";
        if (j.State == JobState.Running)
        {
            status = j.Activity;
            if (j.Phase == JobPhase.Repair) progress = j.Progress.ToString("0%", CultureInfo.InvariantCulture) + (j.RepairStage != null ? " · " + j.RepairStage : "");
            time = Fmt.Duration(j.Elapsed);
        }
        else if (j.State == JobState.Done)
        {
            status = j.Status;
            if (j.Seconds > 0) time = Fmt.Duration(j.Seconds);
        }
        else if (engine != null && engine.Stopping && runJobs.Contains(j)) status = "not started";
        else if (j.Before == null) status = j.Error != null ? "unreadable" : "checking";
        else status = j.Before.Clean ? "clean" : "needs repair";

        Set(item, ColProblems, j.Problems());
        Set(item, ColEstimate, j.Before != null && !j.Before.Clean ? "~" + Fmt.Duration(j.Estimate) : "");
        Set(item, ColStatus, status);
        Set(item, ColProgress, progress);
        Set(item, ColTime, time);
        Color c = j.State == JobState.Queued && j.Before != null && j.Before.Clean ? SystemColors.GrayText
            : j.State != JobState.Done ? SystemColors.WindowText
            : j.Failed ? Color.Firebrick
            : j.Status == JobStatus.Repaired ? Color.ForestGreen
            : j.Status == JobStatus.Broken || j.Status == JobStatus.Aborted ? Color.DarkOrange
            : SystemColors.GrayText;
        if (item.ForeColor != c) item.ForeColor = c;
        if (j.State == JobState.Running) list.Invalidate(item.SubItems[ColProgress].Bounds);
    }

    static void Set(ListViewItem item, int col, string text)
    {
        if (item.SubItems[col].Text != text) item.SubItems[col].Text = text;
    }

    void DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
    {
        FileJob j = (FileJob)e.Item.Tag;
        if (e.ColumnIndex != ColProgress || j.State != JobState.Running || j.Phase != JobPhase.Repair)
        {
            e.DrawDefault = true;
            return;
        }
        Rectangle r = e.Bounds;
        e.Graphics.FillRectangle(e.Item.Selected ? SystemBrushes.Highlight : SystemBrushes.Window, r);
        // Inset from the cell; one pixel less again because DrawRectangle's outline is a pixel wider than the rectangle.
        int inset = S(BarInset);
        Rectangle bar = new Rectangle(r.X + inset, r.Y + inset, r.Width - 2 * inset - 1, r.Height - 2 * inset - 1);
        e.Graphics.FillRectangle(Brushes.Gainsboro, bar);
        int w = (int)(bar.Width * Math.Max(0, Math.Min(1, j.Progress)));
        using (Brush fill = new SolidBrush(BarColor))
            e.Graphics.FillRectangle(fill, bar.X, bar.Y, w, bar.Height);
        e.Graphics.DrawRectangle(SystemPens.ControlDark, bar);
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, list.Font, bar, Color.Black,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    void UpdateButtons()
    {
        bool running = Running;
        start.Enabled = !running && !Checking && jobs.Any(Ticked);
        tickAll.Enabled = tickNone.Enabled = tickBroken.Enabled = !running && jobs.Count > 0;
        stop.Enabled = running && !engine.Stopping;
        abort.Enabled = running && !engine.Aborting;
        addFiles.Enabled = addFolder.Enabled = clear.Enabled = !running;
        remove.Enabled = !running && list.SelectedItems.Count > 0;
        inPlace.Enabled = toFolder.Enabled = checkOnly.Enabled = jobCount.Enabled = !running;
        backup.Enabled = !running && inPlace.Checked && !checkOnly.Checked;
        outDir.Enabled = browse.Enabled = !running && toFolder.Checked;
        list.AllowDrop = !running;
    }

    void UpdateSummary()
    {
        string text;
        if (runJobs.Count == 0)
        {
            if (jobs.Count == 0) text = "Add files or folders to get started.";
            else if (Checking) text = string.Format("Checking files… {0}/{1}", jobs.Count(j => j.Before != null || j.Error != null), jobs.Count);
            else text = string.Format("{0} file(s), {1} · {2}", jobs.Count, Fmt.Bytes(jobs.Sum(j => j.Size)), RepairOutlook());
        }
        else
        {
            int done = runJobs.Count(j => j.State == JobState.Done);
            Dictionary<string, int> counts = runJobs.Where(j => j.State == JobState.Done).GroupBy(j => j.Status).ToDictionary(g => g.Key, g => g.Count());
            int needLook = runJobs.Count(j => j.State == JobState.Done && j.Failed);
            List<string> parts = new List<string> { string.Format("{0}/{1} done", done, runJobs.Count) };
            if (engine.Scanning)
                parts[0] = string.Format("checking {0}/{1}", runJobs.Count(j => j.State == JobState.Done || j.Before != null), runJobs.Count);
            else if (engine.RepairQueue.Count > 0)
                parts.Add(string.Format("repairs {0}/{1}", engine.RepairQueue.Count(j => j.State == JobState.Done), engine.RepairQueue.Count));
            foreach (string s in new[] { JobStatus.Repaired, JobStatus.Clean, JobStatus.Broken, JobStatus.Aborted, JobStatus.Exists })
                if (counts.ContainsKey(s)) parts.Add(counts[s] + " " + s);
            if (needLook > 0) parts.Add(needLook + (needLook == 1 ? " needs a look" : " need a look"));
            parts.Add(Fmt.Duration(runClock.Elapsed.TotalSeconds));
            int notStarted = runJobs.Count - runJobs.Count(j => j.State != JobState.Queued);
            if (!Running && notStarted > 0) parts.Insert(1, notStarted + " not started");
            string state = Running ? (engine.Aborting ? "Aborting… " : engine.Stopping ? "Stopping after current files… " : "")
                : engine.Aborting ? "Aborted. " : engine.Stopping ? "Stopped. " : "Finished. ";
            text = state + string.Join(" · ", parts.ToArray());
            if (engine.LogProblem != null) text += "\n\nWarning: " + engine.LogProblem;
        }
        if (summary.Text != text) summary.Text = text;
        string learned = Calibration.Describe();
        string tip = learned != null ? "Estimated times are scaled " + learned + "." : "Estimated times assume the reference PC until this PC has finished some repairs.";
        if (tips.GetToolTip(summary) != tip) tips.SetToolTip(summary, tip);
    }

    // ---------- misc ----------

    void OpenLog()
    {
        string log = engine != null && engine.Options.LogPath != null ? Paths.Short(engine.Options.LogPath)
            : Path.Combine(EngineOptions.DefaultLogDir, "3mfBatchRepair-log.csv");
        if (File.Exists(log)) ShowInExplorer(log);
        else MessageBox.Show(this, "No log yet. It's written to\n" + log + "\n(or the output folder) once a run starts.", "3mfBatchRepair");
    }

    // Ticked when Explorer's "Repair with 3mfBatchRepair" entry opens this exe. If it points at a copy somewhere else
    // (say, an older build), the box shows unticked and ticking it repoints the entry here.
    static bool ExplorerMenuIsOurs()
    {
        string exe = ShellMenu.InstalledExe();
        return exe != null && string.Equals(Path.GetFullPath(exe), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    void SetExplorerMenu(bool install)
    {
        try
        {
            if (install) ShellMenu.Install(Application.ExecutablePath);
            else ShellMenu.Uninstall();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "3mfBatchRepair", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        explorerMenu.Checked = ExplorerMenuIsOurs();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (Running)
        {
            e.Cancel = true;
            if (closeWhenDone) return;
            if (MessageBox.Show(this, "Repairs are still running. Cancel them and close?\n\nFinished repairs are kept; the files in progress are left as they were.",
                    "3mfBatchRepair", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                closeWhenDone = true;
                engine.Abort();
                UpdateButtons();
                UpdateSummary();
            }
            return;
        }
        base.OnFormClosing(e);
    }
}
