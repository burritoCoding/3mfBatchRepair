// Shared repair engine for the 3mfBatchRepair CLI and GUI: finds files, checks them, repairs the broken ones
// with the Windows 3D printing repair engine (Windows.Graphics.Printing3D) and writes the results.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Printing3D;
using Windows.Storage;
using Windows.Storage.Streams;

public class EngineOptions
{
    public string OutDir;          // null: repair in place
    public string Format = "stl";  // output format when writing to OutDir
    // Each repair runs on a single core, so one job per core (leaving one free) scales almost linearly.
    public int Jobs = Math.Max(1, Environment.ProcessorCount - 1);
    public bool Overwrite, Force, CheckOnly;
    public bool Backup = true;     // in place: send originals to the Recycle Bin before replacing them
    public bool EasiestFirst = true;   // repair the shortest estimates first; false keeps the order the jobs were given in
    public string LogPath;

    public bool InPlace { get { return OutDir == null; } }

    public static string DefaultLogDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "3mfBatchRepair"); }
    }
}

public enum JobState { Queued, Running, Done }

// FileJob.Status values once a job is done. They're written to the log as is, so changing one changes the log.
public static class JobStatus
{
    public const string
        Clean = "clean",                // nothing to repair
        Broken = "broken",              // needs repair (check only)
        Repaired = "repaired",
        StillBroken = "still-broken",   // the repair didn't fix everything
        Suspicious = "suspicious",      // the repair moved the model
        Failed = "failed",
        Aborted = "aborted",            // cancelled while repairing; file left as it was
        Skipped = "skipped",            // not something this run can handle (see Error)
        Exists = "exists";              // output already there
}

// FileJob.Phase values, in the order a repair goes through them. They name the log's *_s columns.
public static class JobPhase
{
    public const string Read = "read", Prepare = "prepare", Repair = "repair", Write = "write";
    public static readonly string[] All = { Read, Prepare, Repair, Write };
}

public class FileJob
{
    public string Input, Root, Display, Output;
    public long Size;

    public volatile JobState State;
    public volatile string Phase;      // a JobPhase while running
    public double Progress;            // repair progress, 0..1 (uneven: don't extrapolate a finish time from it)
    public string Status, Error;
    public bool Wrote;                 // Output was written (or, in place, the original replaced)
    public MeshStats Before, After;
    public double BaseEstimate;        // predicted repair seconds on the reference PC (Engine.EstimateSeconds); 0 until checked

    // Predicted repair seconds on this PC; follows Calibration as it learns, even mid-run.
    public double Estimate { get { return BaseEstimate * Calibration.Factor; } }
    public string ScanStamp;           // file timestamp+size when Before was measured, so a recheck can be skipped
    public bool Appearance;            // the file has textures/materials (OBJ) or more than geometry (3MF) a rewrite would lose
    public double Seconds;
    public readonly Dictionary<string, double> PhaseSeconds = new Dictionary<string, double>();
    public readonly Stopwatch Clock = new Stopwatch();
    double phaseStart;

    public double Elapsed { get { return Clock.Elapsed.TotalSeconds; } }

    // Clears the results of a previous run; keeps the check (Before/Estimate) for as long as the file is unchanged.
    public void Reset()
    {
        State = JobState.Queued; Phase = null; Progress = 0;
        Status = Error = null; Wrote = false; After = null; Seconds = 0;
        lock (PhaseSeconds) PhaseSeconds.Clear();
        Clock.Reset();
    }

    public static string Stamp(string path)
    {
        FileInfo f = new FileInfo(path);
        return f.LastWriteTimeUtc.Ticks + ":" + f.Length;
    }

    public void Enter(string phase)
    {
        EndPhase();
        Phase = phase;
        phaseStart = Clock.Elapsed.TotalSeconds;
    }

    public void EndPhase()
    {
        if (Phase == null) return;
        lock (PhaseSeconds)
        {
            double s;
            PhaseSeconds.TryGetValue(Phase, out s);
            PhaseSeconds[Phase] = s + Clock.Elapsed.TotalSeconds - phaseStart;
        }
        Phase = null;
    }

    public string Problems()
    {
        if (Error != null) return Error;
        if (Before == null) return "";
        string before = Before.Problems() + (Before.Shells > 1 ? "; " + Before.Shells + " pieces" : "")
            + (Appearance && Input.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ? "; has textures" : "");
        return After != null ? before + " -> " + After.Problems() : before;
    }

    // What the Windows engine is doing, where we can tell. Its progress jumps to exactly 0.863 and then sits
    // there for most of a long repair: that stage fuses overlapping pieces into one solid (e.g. supports
    // pushed into a model). A single-piece mesh passes it in under a second; one with hundreds of support
    // pieces can spend most of its repair there. Inferred from timing, not documented by Microsoft.
    const double FusingFrom = 0.862, FusingTo = 0.872;   // a little either side of the 0.863 it sits at

    public string RepairStage
    {
        get
        {
            if (Phase != JobPhase.Repair) return null;
            return Progress >= FusingFrom && Progress < FusingTo ? "fusing overlaps" : null;
        }
    }

    // What a running job is doing: "reading", "preparing", "repairing" (or the repair stage), "writing",
    // or "starting" before its first phase.
    public string Activity
    {
        get
        {
            switch (Phase)
            {
                case JobPhase.Read: return "reading";
                case JobPhase.Prepare: return "preparing";
                case JobPhase.Repair: return RepairStage ?? "repairing";
                case JobPhase.Write: return "writing";
                default: return "starting";
            }
        }
    }

    public string Timing()
    {
        lock (PhaseSeconds)
            return string.Join(" · ", JobPhase.All
                .Where(p => PhaseSeconds.ContainsKey(p))
                .Select(p => p + " " + Fmt.Duration(PhaseSeconds[p])).ToArray());
    }

    public bool Failed
    {
        get { return Status == JobStatus.Failed || Status == JobStatus.StillBroken || Status == JobStatus.Suspicious; }
    }
}

public class Engine
{
    public readonly EngineOptions Options;
    public event Action<FileJob> JobStarted, Repairing, JobFinished;   // raised on worker threads

    // Raised once, with RepairQueue, between the two passes.
    public event Action<List<FileJob>> Scanned;

    // True while the first pass is checking files.
    public volatile bool Scanning;

    // The files the second pass repairs, in order; set once the first pass is done.
    public List<FileJob> RepairQueue = new List<FileJob>();

    int stopRequested;
    readonly CancellationTokenSource abort = new CancellationTokenSource();
    CsvLog log;

    public Engine(EngineOptions options) { Options = options; }

    public bool Stopping { get { return stopRequested != 0; } }
    public bool Aborting { get { return abort.IsCancellationRequested; } }

    // Why the log couldn't be written (rows are kept and retried), or null.
    public string LogProblem { get { return log != null ? log.Problem : null; } }

    // Don't start any more files; the ones in progress finish normally.
    public void Stop() { Interlocked.Exchange(ref stopRequested, 1); }

    // Also cancel repairs in progress. Finished files are kept; cancelled ones are left as they were.
    public void Abort() { Stop(); abort.Cancel(); }

    // ---------- finding files ----------

    // Expands files and folders (recursively) into .stl/.obj/.3mf jobs, in the order given and alphabetically within folders.
    public static List<FileJob> Discover(IEnumerable<string> inputs, Action<string> notFound)
    {
        List<FileJob> jobs = new List<FileJob>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in inputs)
        {
            string input = Paths.Long(raw.Trim().Trim('"'));
            if (File.Exists(input))
            {
                if (MeshIO.IsMesh(input)) Add(jobs, seen, input, Path.GetDirectoryName(input));
            }
            else if (Directory.Exists(input))
            {
                // Runs process files in this order, so whole folders finish one after another and a stopped
                // run's progress is easy to follow.
                foreach (string f in Paths.Walk(input).Where(MeshIO.IsMesh)) Add(jobs, seen, f, input);
            }
            else if (notFound != null) notFound(raw);
        }
        return jobs;
    }

    static void Add(List<FileJob> jobs, HashSet<string> seen, string file, string root)
    {
        if (!seen.Add(file)) return;
        root = root.TrimEnd(Path.DirectorySeparatorChar);
        jobs.Add(new FileJob
        {
            Input = file,
            Root = root,
            Display = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar),
            Size = new FileInfo(file).Length,
        });
    }

    // Works out each job's output for the current options. Returns the jobs to run: in place, 3MF files are
    // skipped (rewriting them would drop transforms, colors and materials; OBJs with textures are caught when
    // they're read); with an output folder, files already inside it are skipped.
    public List<FileJob> Plan(IEnumerable<FileJob> jobs)
    {
        List<FileJob> run = new List<FileJob>();
        foreach (FileJob j in jobs)
        {
            j.Reset();
            if (Options.InPlace)
            {
                if (Path.GetExtension(j.Input).Equals(".3mf", StringComparison.OrdinalIgnoreCase) && !Options.CheckOnly)
                {
                    j.State = JobState.Done;
                    j.Status = JobStatus.Skipped;
                    j.Error = "3MF files aren't repaired in place";
                    continue;
                }
                j.Output = j.Input;
            }
            else
            {
                string outDir = Paths.Long(Options.OutDir);
                if (j.Input.StartsWith(outDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    j.State = JobState.Done;
                    j.Status = JobStatus.Skipped;
                    j.Error = "already inside the output folder";
                    continue;
                }
                // Mirror the file's place under the folder it was found in (a single file lands directly in OutDir).
                string relDir = Path.GetDirectoryName(j.Input).Substring(j.Root.Length).TrimStart(Path.DirectorySeparatorChar);
                j.Output = Path.Combine(outDir, relDir, Path.GetFileNameWithoutExtension(j.Input) + "." + Options.Format);
            }
            run.Add(j);
        }
        return run;
    }

    // ---------- running ----------

    // Runs in two passes (blocking) until done, stopped or aborted:
    //  1. check every file in the order given; clean ones are finished right there (and copied with an output folder);
    //  2. repair the broken ones, easiest (shortest estimated repair) first, so the quick wins land before the
    //     long ones and a big batch can be left to finish its hardest files overnight.
    // Jobs never started stay Queued.
    public void Run(IList<FileJob> jobs)
    {
        if (Options.LogPath == null && !Options.CheckOnly)
            Options.LogPath = Path.Combine(Options.OutDir != null ? Paths.Long(Options.OutDir) : EngineOptions.DefaultLogDir, "3mfBatchRepair-log.csv");
        log = Options.LogPath != null && !Options.CheckOnly ? new CsvLog(Paths.Long(Options.LogPath)) : null;

        Scanning = true;
        HashSet<FileJob> broken = new HashSet<FileJob>();
        ForEachInOrder(jobs, job => { if (Scan(job)) lock (broken) broken.Add(job); });
        Scanning = false;

        // OrderBy is stable, so equal estimates keep the folder order.
        RepairQueue = Options.EasiestFirst
            ? jobs.Where(broken.Contains).OrderBy(j => j.Estimate).ToList()
            : jobs.Where(broken.Contains).ToList();
        if (Scanned != null) Scanned(RepairQueue);
        ForEachInOrder(RepairQueue, RepairJob);
        if (log != null) log.Flush();   // a last try for rows that waited on a locked log
    }

    void ForEachInOrder(IList<FileJob> jobs, Action<FileJob> work)
    {
        ForEachInOrder(jobs, Options.Jobs, job => { if (!Stopping) work(job); });
    }

    // Runs work on up to `workers` items at once, starting them in list order.
    public static void ForEachInOrder<T>(IList<T> items, int workers, Action<T> work)
    {
        // NoBuffering hands out one item at a time in list order; a plain list would be split into fixed ranges.
        Parallel.ForEach(Partitioner.Create(items, EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = workers }, work);
    }

    // Repair time in seconds predicted from the mesh's size, damage and number of separate pieces, on the reference
    // PC (a 6-core desktop running 5 repairs at once); Calibration scales it for the PC it's running on. Fitted
    // (log-log least squares) to 140 logged repairs from 3K to 3M triangles, mostly presupported minis:
    // triangles explain most of it (R² 0.60), pieces add more than bad edges (R² 0.72 all together), and it
    // ranks files in the right order well (Spearman 0.87). Typically off by about 3x either way, partly
    // because the logged times were measured with several repairs sharing the CPU: good for ordering and
    // overnight planning, not a promise. spike/refit.ps1 redoes the fit from the logs.
    //   seconds = e^LogScale * triangles^TriangleExp * (1 + bad edges)^BadEdgeExp * pieces^PieceExp
    const double LogScale = -7.904, TriangleExp = 0.877, BadEdgeExp = 0.117, PieceExp = 0.273;

    public static double EstimateSeconds(MeshStats s)
    {
        return Math.Exp(LogScale) * Math.Pow(Math.Max(1, s.Triangles), TriangleExp) * Math.Pow(1 + s.BadEdges, BadEdgeExp)
            * Math.Pow(Math.Max(1, s.Shells), PieceExp);
    }

    // Rough wall-clock time for a list of estimates run in order on `workers` cores.
    public static double EstimateWall(IEnumerable<double> estimates, int workers)
    {
        double[] free = new double[Math.Max(1, workers)];
        foreach (double e in estimates)
        {
            int k = Array.IndexOf(free, free.Min());
            free[k] += e;
        }
        return free.Max();
    }

    // Checks a file outside a run (the GUI does this as files are added) so its problems and estimate show
    // right away; the run's first pass then skips it unless the file changes.
    public static void Inspect(FileJob job)
    {
        try
        {
            string stamp = FileJob.Stamp(job.Input);
            if (job.Before != null && job.ScanStamp == stamp) return;
            job.Before = MeshIO.Stats(MeshIO.Read(job.Input, out job.Appearance));
            job.ScanStamp = stamp;
            job.BaseEstimate = EstimateSeconds(job.Before);
            job.Error = null;
        }
        catch (Exception e)
        {
            job.Error = "couldn't read: " + Message(e);
        }
    }

    // Pass 1. Returns true if the file needs repairing; otherwise the job is finished here.
    bool Scan(FileJob job)
    {
        job.State = JobState.Running;
        job.Clock.Restart();
        Raise(JobStarted, job);
        bool needsRepair = false;
        try
        {
            if (!Options.CheckOnly && !Options.InPlace && !Options.Overwrite && File.Exists(job.Output))
            {
                job.Status = JobStatus.Exists;
                job.Error = "output already there; overwrite to redo";
                return false;
            }

            Mesh mesh = null;
            string stamp = FileJob.Stamp(job.Input);
            if (job.Before == null || job.ScanStamp != stamp)   // the GUI may already have checked it
            {
                job.Enter(JobPhase.Read);
                mesh = MeshIO.Read(job.Input, out job.Appearance);
                job.Before = MeshIO.Stats(mesh);
                job.ScanStamp = stamp;
            }
            job.BaseEstimate = EstimateSeconds(job.Before);

            if (Options.CheckOnly)
            {
                job.Status = job.Before.Clean ? JobStatus.Clean : JobStatus.Broken;
                return false;
            }
            if (job.Before.Clean && !Options.Force)
            {
                job.Status = JobStatus.Clean;
                if (!Options.InPlace)
                {
                    job.Enter(JobPhase.Write);
                    if (Path.GetExtension(job.Input).Equals("." + Options.Format, StringComparison.OrdinalIgnoreCase))
                        Paths.AtomicWrite(job.Output, tmp => File.Copy(job.Input, tmp, true));
                    else
                        Paths.AtomicWrite(job.Output, tmp => MeshIO.Write(mesh ?? MeshIO.Read(job.Input), tmp));
                    job.Wrote = true;
                }
                return false;
            }
            if (Options.InPlace && job.Appearance)
            {
                job.Status = JobStatus.Skipped;
                job.Error = "has textures or materials a repair would drop; use an output folder";
                return false;
            }
            needsRepair = true;
            return true;
        }
        catch (Exception e)
        {
            job.Status = JobStatus.Failed;
            job.Error = Message(e);
            return false;
        }
        finally
        {
            job.EndPhase();
            job.Clock.Stop();
            if (needsRepair) job.State = JobState.Queued;
            else Finish(job);
        }
    }

    // Pass 2.
    void RepairJob(FileJob job)
    {
        job.State = JobState.Running;
        job.Clock.Start();
        Raise(Repairing, job);
        try
        {
            job.Enter(JobPhase.Read);
            string stamp = FileJob.Stamp(job.Input);
            Mesh mesh = MeshIO.Read(job.Input);
            if (job.ScanStamp != stamp)   // changed since the check pass measured it
            {
                job.Before = MeshIO.Stats(mesh);
                job.ScanStamp = stamp;
                job.BaseEstimate = EstimateSeconds(job.Before);
            }
            Mesh repaired = Repair(job, mesh);
            double repairSeconds;
            lock (job.PhaseSeconds) job.PhaseSeconds.TryGetValue(JobPhase.Repair, out repairSeconds);
            Calibration.Record(job.BaseEstimate, repairSeconds);

            job.Enter(JobPhase.Write);
            WriteVerified(job, mesh, repaired);
        }
        catch (Exception e)
        {
            if (Unwrap(e) is OperationCanceledException)
            {
                job.Status = JobStatus.Aborted;
                job.Error = "repair cancelled; file left as it was";
            }
            else
            {
                job.Status = JobStatus.Failed;
                job.Error = Message(e);
            }
        }
        finally
        {
            job.EndPhase();
            job.Clock.Stop();
            Finish(job);
        }
    }

    void Finish(FileJob job)
    {
        job.Seconds = job.Elapsed;
        job.State = JobState.Done;
        if (log != null) log.Write(job);
        Raise(JobFinished, job);
    }

    static Exception Unwrap(Exception e)
    {
        while (e is AggregateException && e.InnerException != null) e = e.InnerException;
        return e;
    }

    static string Message(Exception e) { return Paths.Message(Unwrap(e)); }

    static void Raise(Action<FileJob> handler, FileJob job)
    {
        if (handler != null) handler(job);
    }

    // Stores repair progress without any marshalling: the engine reports hundreds of thousands of updates per file.
    class ProgressSink : IProgress<double>
    {
        readonly FileJob job;
        public ProgressSink(FileJob job) { this.job = job; }
        public void Report(double value) { job.Progress = value; }
    }

    Mesh Repair(FileJob job, Mesh mesh)
    {
        string id = Guid.NewGuid().ToString("N");
        string inPath = Path.Combine(Path.GetTempPath(), "3mfBatchRepair_" + id + "_in.3mf");
        try
        {
            job.Enter(JobPhase.Prepare);
            MeshIO.Write3mf(mesh, inPath);
            Printing3DModel model;
            // Printing3D clones the stream, so it needs a native WinRT stream rather than a wrapped .NET one.
            using (IRandomAccessStream ras = FileRandomAccessStream.OpenAsync(inPath, FileAccessMode.Read).AsTask().Result)
                model = new Printing3D3MFPackage().LoadModelFromPackageAsync(ras).AsTask().Result;

            job.Enter(JobPhase.Repair);
            bool ok = model.RepairWithProgressAsync().AsTask(abort.Token, new ProgressSink(job)).Result;
            // A cancelled repair can come back as a plain false rather than a cancellation.
            abort.Token.ThrowIfCancellationRequested();
            if (!ok) throw new Exception("the Windows repair engine couldn't repair this mesh");
            job.Progress = 1;

            job.Enter(JobPhase.Write);
            return ReadModel(model);
        }
        finally
        {
            Paths.TryDelete(inPath);
        }
    }

    // Copies the repaired meshes out of the engine's own buffers at full (double) precision. Saving the model
    // as 3MF instead rounds every coordinate to 0.001 mm, which can land separate vertices on the same point;
    // once written as STL (which has no shared vertices) those merge and the "repaired" file is broken again.
    static Mesh ReadModel(Printing3DModel model)
    {
        Mesh m = new Mesh();
        foreach (Printing3DMesh pm in model.Meshes)
        {
            int baseIdx = m.VertexCount;
            uint verts = pm.VertexCount, tris = pm.IndexCount;
            if (verts == 0 || tris == 0) continue;

            // Positions come as doubles or floats, indices as 32-bit unsigned ints; anything else would be misread.
            Printing3DBufferFormat vf = pm.VertexPositionsDescription.Format, tf = pm.TriangleIndicesDescription.Format;
            bool doubles = vf == Printing3DBufferFormat.Printing3DDouble;
            if (!doubles && vf != Printing3DBufferFormat.R32G32B32Float && vf != Printing3DBufferFormat.R32G32B32A32Float)
                throw new Exception("the repair engine returned vertex positions as " + vf + ", which 3mfBatchRepair can't read");
            if (tf != Printing3DBufferFormat.Printing3DUInt && tf != Printing3DBufferFormat.R32G32B32UInt && tf != Printing3DBufferFormat.R32G32B32A32UInt)
                throw new Exception("the repair engine returned triangles as " + tf + ", which 3mfBatchRepair can't read");

            byte[] vb = pm.GetVertexPositions().ToArray();
            int vStride = vb.Length / (int)verts;
            for (int i = 0; i < verts; i++)
                for (int k = 0; k < 3; k++)
                    m.V.Add(doubles
                        ? (float)BitConverter.ToDouble(vb, i * vStride + k * sizeof(double))
                        : BitConverter.ToSingle(vb, i * vStride + k * sizeof(float)));

            byte[] ib = pm.GetTriangleIndices().ToArray();
            int iStride = ib.Length / (int)tris;
            for (int t = 0; t < tris; t++)
                for (int k = 0; k < 3; k++)
                    m.T.Add(baseIdx + (int)BitConverter.ToUInt32(ib, t * iStride + k * sizeof(uint)));
        }
        if (m.TriangleCount == 0) throw new Exception("the repair engine returned no mesh");
        return m;
    }

    // Writes the repaired mesh to a temp file, reads that back the way a slicer would, and judges the repair by
    // what's actually on disk. In place, the original is only replaced when that copy checks clean and the model
    // hasn't moved; otherwise the original stays and the file is flagged.
    void WriteVerified(FileJob job, Mesh original, Mesh repaired)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(job.Output));
        string tmp = job.Output + Paths.PartialSuffix;   // written and read back in the output's format (STL, OBJ or 3MF)
        bool keepTmp = false;
        try
        {
            MeshIO.Write(repaired, tmp);
            Mesh written = MeshIO.Read(tmp);
            job.After = MeshIO.Stats(written);
            string shift = MeshIO.BoundsShift(original, written);

            if (Options.InPlace)
            {
                if (!job.After.Clean)
                {
                    job.Status = JobStatus.StillBroken;
                    job.Error = "original kept: the repaired file would still have " + job.After.Problems();
                    return;
                }
                if (shift != null)
                {
                    job.Status = JobStatus.Suspicious;
                    job.Error = "original kept: " + shift;
                    return;
                }
                if (Options.Backup)
                {
                    RecycleBin.Send(job.Input);
                    keepTmp = true;   // from here on the temp file is the only copy of the repair
                    try { File.Move(tmp, job.Input); }
                    catch (Exception e)
                    {
                        throw new IOException("original is in the Recycle Bin, but the repaired file couldn't be moved into place (" +
                            Paths.Message(e) + "); it's at " + Paths.Short(tmp), e);
                    }
                    keepTmp = false;
                }
                else File.Replace(tmp, job.Input, null);
            }
            else
            {
                Paths.SwapIn(tmp, job.Output);
                if (shift != null) job.Error = "written, but check it: " + shift;
                else if (!job.After.Clean) job.Error = "written, but it still has " + job.After.Problems();
            }
            job.Wrote = true;
            job.Status = !job.After.Clean ? JobStatus.StillBroken : shift != null ? JobStatus.Suspicious : JobStatus.Repaired;
        }
        finally
        {
            if (!keepTmp) Paths.TryDelete(tmp);
        }
    }

}

// How fast this PC repairs compared with the reference PC the estimate was fitted on. Learned from this PC's own
// finished repairs (actual repair time / base estimate), so it also absorbs how many files usually run at once.
// Each repair counts by how long it took: a batch of sub-second repairs, whose times are mostly fixed overhead and
// noise, can't outvote the long repairs that estimates matter for. It starts at 1.0 and leans toward 1.0 until a
// few minutes of repairs have been seen; the most recent repairs count, so it keeps up with a new PC or different
// habits. Updated after every repair, which also corrects a batch's remaining estimates while it runs.
public static class Calibration
{
    const int Keep = 50;               // most recent repairs used
    const double PriorSeconds = 180;   // weight of the reference PC's 1.0, in seconds of repair time
    const double OldEntrySeconds = 30; // weight for entries saved before durations were recorded

    struct Entry { public double LogRatio, Seconds; }

    static readonly object gate = new object();
    static List<Entry> entries;
    static double factor = 1;
    static int version;

    static string FilePath { get { return Path.Combine(EngineOptions.DefaultLogDir, "calibration.txt"); } }

    public static double Factor { get { lock (gate) { Load(); return factor; } } }
    public static int Samples { get { lock (gate) { Load(); return entries.Count; } } }
    public static int Version { get { lock (gate) return version; } }   // changes whenever Factor might have

    // "x0.82 for this PC, learned from 12 repairs (1h04m)", or null while there's nothing learned yet.
    public static string Describe()
    {
        lock (gate)
        {
            Load();
            if (entries.Count == 0) return null;
            return string.Format(CultureInfo.InvariantCulture, "x{0:0.00} for this PC, learned from {1} repair{2} ({3})",
                factor, entries.Count, entries.Count == 1 ? "" : "s", Fmt.Duration(entries.Sum(e => e.Seconds)));
        }
    }

    public static void Record(double baseEstimate, double actualSeconds)
    {
        if (baseEstimate <= 0 || actualSeconds <= 0) return;
        lock (gate)
        {
            Load();
            entries.Add(new Entry { LogRatio = Math.Log(actualSeconds / baseEstimate), Seconds = actualSeconds });
            if (entries.Count > Keep) entries.RemoveRange(0, entries.Count - Keep);
            Recompute();
            version++;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[] { "# 3mfBatchRepair speed calibration: ln(actual / estimated repair time) and repair seconds, newest last" }
                    .Concat(entries.Select(e => e.LogRatio.ToString("R", CultureInfo.InvariantCulture) + " " + e.Seconds.ToString("R", CultureInfo.InvariantCulture))));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static void Load()
    {
        if (entries != null) return;
        entries = new List<Entry>();
        try
        {
            if (File.Exists(FilePath))
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    if (line.StartsWith("#")) continue;
                    string[] p = line.Split(' ');
                    double r, s = OldEntrySeconds;
                    if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r)) continue;
                    if (p.Length > 1) double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out s);
                    if (s > 0) entries.Add(new Entry { LogRatio = r, Seconds = s });
                }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (entries.Count > Keep) entries.RemoveRange(0, entries.Count - Keep);
        Recompute();
    }

    // Time-weighted median of the log ratios, shrunk toward 0 (factor 1.0) while little repair time has been seen.
    static void Recompute()
    {
        if (entries.Count == 0) { factor = 1; return; }
        List<Entry> sorted = entries.OrderBy(e => e.LogRatio).ToList();
        double total = sorted.Sum(e => e.Seconds), seen = 0, median = sorted[sorted.Count - 1].LogRatio;
        foreach (Entry e in sorted)
        {
            seen += e.Seconds;
            if (seen >= total / 2) { median = e.LogRatio; break; }
        }
        factor = Math.Exp(median * total / (total + PriorSeconds));
    }
}

public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom, pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;
    const int MAX_PATH = 260;   // SHFileOperation can't take \\?\ paths, so it's held to the old limit
    static readonly object gate = new object();

    // Moves a file to the Recycle Bin. If Windows would have to delete it permanently instead (too big for the
    // bin, or a drive without one), it asks first; saying no leaves the file where it is and throws.
    public static void Send(string path)
    {
        string plain = Paths.Short(path);
        if (plain.Length >= MAX_PATH)
            throw new IOException("path too long for the Recycle Bin; original left untouched (turn off the backup to replace it)");
        SHFILEOPSTRUCT op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = plain + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        int rc;
        lock (gate) rc = SHFileOperation(ref op);   // the shell's file operations aren't meant to run concurrently
        if (rc != 0 || op.fAnyOperationsAborted || File.Exists(path))
            throw new IOException(string.Format("couldn't move the original to the Recycle Bin (code 0x{0:X}); left it untouched", rc));
    }
}

// The log never stops a run: if the file can't be written (typically because it's open in Excel, which locks it),
// rows wait and go out with the next one that can be, and Problem says why.
class CsvLog
{
    const string Header = "time,input,output,status,before,after,seconds,read_s,prepare_s,repair_s,write_s,error";
    readonly string path;
    readonly object gate = new object();
    readonly StringBuilder pending = new StringBuilder();
    string problem;

    public CsvLog(string path)
    {
        this.path = path;
        bool needHeader;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            needHeader = !File.Exists(path) || File.ReadLines(path).FirstOrDefault() != Header;   // new, or from an older version
        }
        catch (Exception e)
        {
            if (!(e is IOException || e is UnauthorizedAccessException)) throw;
            needHeader = !File.Exists(path);
        }
        if (needHeader) pending.Append(Header + "\r\n");
    }

    // Why rows couldn't be written, or null once everything is in the file.
    public string Problem { get { lock (gate) return problem; } }

    public void Write(FileJob job)
    {
        List<string> cols = new List<string> {
            DateTime.Now.ToString("s"), Q(Paths.Short(job.Input)), Q(job.Wrote ? Paths.Short(job.Output) : ""), job.Status,
            Q(job.Before != null ? job.Before.ToString() : ""), Q(job.After != null ? job.After.ToString() : ""), S(job.Seconds) };
        lock (job.PhaseSeconds)
            foreach (string p in JobPhase.All)
            {
                double s;
                cols.Add(job.PhaseSeconds.TryGetValue(p, out s) ? S(s) : "");
            }
        cols.Add(Q(job.Error ?? ""));
        lock (gate)
        {
            pending.Append(string.Join(",", cols.ToArray()) + "\r\n");
            Flush();
        }
    }

    public void Flush()
    {
        lock (gate)
        {
            if (pending.Length == 0) return;
            try
            {
                File.AppendAllText(path, pending.ToString(), Encoding.UTF8);
                pending.Clear();
                problem = null;
            }
            catch (Exception e)
            {
                if (!(e is IOException || e is UnauthorizedAccessException)) throw;
                // The system's message names the file, so it isn't repeated here.
                problem = "couldn't write the log (open in another program?): " + Paths.Message(e).TrimEnd('.');
            }
        }
    }

    static string S(double s) { return s.ToString("0.0", CultureInfo.InvariantCulture); }
    static string Q(string s) { return "\"" + s.Replace("\"", "\"\"") + "\""; }
}
