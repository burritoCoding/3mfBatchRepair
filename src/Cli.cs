// 3mfBatchRepair command line: batch-repair STL/OBJ/3MF meshes with the Windows 3D printing repair engine.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

static class Cli
{
    const string Usage =
@"3mfBatchRepair - repair STL/OBJ/3MF meshes with the Windows 3D printing repair engine

usage: 3mfBatchRepair <file-or-folder>... [options]

Every file is checked first, in alphabetical order folder by folder (a folder's
files, then its subfolders). Then the broken ones are repaired easiest first:
shortest estimated repair (from triangle count and damage) to longest, so the
quick wins finish early and the hardest files can run overnight.
By default broken .stl and .obj files are repaired in place: each original goes to
the Recycle Bin and the repaired file takes its name. An original is only replaced
when the repair checks clean and the model hasn't moved; otherwise it's kept and
flagged. A repair keeps geometry only, so OBJs with textures/materials and 3MF files
are only repaired with --out.
Clean files are never rewritten. Each file is saved as soon as it's done, so a
stopped run keeps everything it finished.

options:
  -o, --out DIR      write results under DIR instead (mirroring the folder layout;
                     clean files are copied so DIR is a complete set)
  -f, --format F     output format with --out: stl (default), obj or 3mf
  -j, --jobs N       files to repair at once (default: CPU cores - 1)
  --no-backup        in place: replace originals without keeping them in the Recycle Bin
  --overwrite        with --out: redo files whose output already exists
  --force            repair even meshes that already check clean
  --check            only report mesh problems; write nothing
  -v, --verbose      list each repair as it starts, break down where each file's
                     time went, and print in-progress files every --heartbeat seconds
  --heartbeat N      seconds between in-progress reports with -v (default: 30)
  -q, --quiet        don't list files that are clean or already done
  --log FILE         CSV log, one row per file (default: 3mfBatchRepair-log.csv in the
                     output folder, or %LOCALAPPDATA%\3mfBatchRepair)
  --install-menu     add ""Repair with 3mfBatchRepair"" to Explorer's right-click menu
                     (opens 3mfBatchRepair-gui.exe from this folder)
  --uninstall-menu   remove it again

Ctrl+C once: finish the files in progress, then stop. Twice: cancel them too
(they're left as they were). Three times: quit immediately.

exit codes: 0 all good, 1 some files failed or need a look, 2 bad arguments";

    const int DefaultHeartbeatSeconds = 30;   // also in Usage
    const int QuitPresses = 3;                // Ctrl+C presses that quit at once (also in Usage)
    const int StatusIntervalMs = 1000;        // how often the live status line updates
    const int FallbackConsoleWidth = 120, MinStatusWidth = 20;
    const string Ellipsis = "...";

    static readonly object consoleLock = new object();
    static string statusLine = "";        // live status line currently on screen (interactive consoles only)
    static bool interactive;

    static int Main(string[] args)
    {
        EngineOptions opt = new EngineOptions();
        List<string> inputs = new List<string>();
        bool verbose = false, quiet = false;
        int heartbeat = DefaultHeartbeatSeconds;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "-h": case "--help": case "/?": Console.WriteLine(Usage); return ExitCode.Ok;
                    case "-o": case "--out": opt.OutDir = Paths.Long(Args.Next(args, ref i)); break;
                    case "-f": case "--format":
                        opt.Format = Args.Next(args, ref i).ToLowerInvariant().TrimStart('.');
                        if (opt.Format != "stl" && opt.Format != "obj" && opt.Format != "3mf") throw new ArgumentException("--format must be stl, obj or 3mf");
                        break;
                    case "-j": case "--jobs": opt.Jobs = Args.Int(args, ref i, 1); break;
                    case "--in-place": break;   // the default now; accepted for older scripts
                    case "--no-backup": opt.Backup = false; break;
                    case "--overwrite": opt.Overwrite = true; break;
                    case "--force": opt.Force = true; break;
                    case "--check": opt.CheckOnly = true; break;
                    case "-v": case "--verbose": verbose = true; break;
                    case "--heartbeat": heartbeat = Args.Int(args, ref i, 1); break;
                    case "-q": case "--quiet": quiet = true; break;
                    case "--log": opt.LogPath = Paths.Long(Args.Next(args, ref i)); break;
                    case "--install-menu": return InstallMenu();
                    case "--uninstall-menu": ShellMenu.Uninstall(); Console.WriteLine("removed the right-click menu"); return ExitCode.Ok;
                    default:
                        if (a.StartsWith("-")) throw new ArgumentException("unknown option " + a);
                        inputs.Add(a);
                        break;
                }
            }
            if (inputs.Count == 0) { Console.WriteLine(Usage); return ExitCode.Ok; }
            if (opt.InPlace && opt.Format != "stl") throw new ArgumentException("--format only applies with --out");
        }
        catch (ArgumentException e) { return Args.Fail(e, Usage); }

        bool missing = false;
        List<FileJob> found = Engine.Discover(inputs, p => { Console.Error.WriteLine("error: not found: " + p); missing = true; });
        if (missing) return ExitCode.BadArgs;
        Engine engine = new Engine(opt);
        List<FileJob> jobs = engine.Plan(found);
        foreach (FileJob j in found.Where(j => j.Status == JobStatus.Skipped && !quiet)) Say(string.Format("skipped      {0}  ({1})", j.Display, j.Error));
        if (jobs.Count == 0) { Console.Error.WriteLine("no .stl, .obj or .3mf files to process"); return ExitCode.BadArgs; }

        interactive = !Console.IsOutputRedirected;
        Console.WriteLine("{0} file(s), {1} at a time, {2}", jobs.Count, opt.Jobs,
            opt.CheckOnly ? "check only" : opt.InPlace ? (opt.Backup ? "in place (originals to Recycle Bin)" : "in place, no backup") : "writing to " + Paths.Short(opt.OutDir));

        int presses = 0;
        Console.CancelKeyPress += (s, e) =>
        {
            int n = Interlocked.Increment(ref presses);
            if (n >= QuitPresses) return;   // let it kill the process
            e.Cancel = true;
            if (n == 1)
            {
                engine.Stop();
                Say("Stopping after the files in progress finish. Ctrl+C again to cancel them too.");
            }
            else
            {
                engine.Abort();
                Say("Cancelling the files in progress (they're left as they were). Ctrl+C again to quit immediately.");
            }
        };

        int done = 0, checkedCount = 0, repairsDone = 0, repairsStarted = 0;
        Func<int> filesChecked = () => jobs.Count(j => j.Before != null || j.Status != null);
        HashSet<FileJob> repairs = new HashSet<FileJob>();
        Dictionary<string, int> counts = new Dictionary<string, int>();
        engine.Scanned += queue =>
        {
            lock (repairs) repairs.UnionWith(queue);
            int clean = jobs.Count(j => j.Status == JobStatus.Clean);
            string line = string.Format("checked {0} file(s): {1} clean, {2} {3} repair", filesChecked(), clean, queue.Count, queue.Count == 1 ? "needs" : "need");
            if (queue.Count > 0)
                line += string.Format(". Estimated repair time ~{0} ({1} at a time); easiest first, hardest ~{2}",
                    Fmt.Duration(Engine.EstimateWall(queue.Select(j => j.Estimate), opt.Jobs)), opt.Jobs, Fmt.Duration(queue.Last().Estimate));
            string calibration = Calibration.Describe();
            if (queue.Count > 0 && calibration != null) line += " (estimates " + calibration + ")";
            if (engine.Stopping) line += ". Stopped before repairing";
            Say(line);
        };
        if (verbose) engine.Repairing += j =>
        {
            lock (consoleLock)   // number and print together, so the numbers come out in order
                Say(string.Format("repairing    [{0}/{1}] {2}  ({3}, {4}; est. ~{5})",
                    ++repairsStarted, repairs.Count, j.Display, Fmt.Bytes(j.Size), j.Before, Fmt.Duration(j.Estimate)));
        };
        engine.JobFinished += j =>
        {
            Interlocked.Increment(ref done);
            bool isRepair;
            lock (repairs) isRepair = repairs.Contains(j);
            int n = isRepair ? Interlocked.Increment(ref repairsDone) : Interlocked.Increment(ref checkedCount);
            lock (counts) { int c; counts.TryGetValue(j.Status, out c); counts[j.Status] = c + 1; }
            if (quiet && (j.Status == JobStatus.Clean || j.Status == JobStatus.Exists)) return;
            string line = isRepair
                ? string.Format("[repair {0}/{1}] {2,-12} {3}  ({4}) {5}", n, repairs.Count, j.Status, j.Display, j.Problems(), Fmt.Duration(j.Seconds))
                : string.Format("[{0}/{1}] {2,-12} {3}  ({4}) {5}", n, jobs.Count, j.Status, j.Display, j.Problems(), Fmt.Duration(j.Seconds));
            if (verbose && isRepair) line += "  [" + j.Timing() + "; est. ~" + Fmt.Duration(j.Estimate) + "]";
            if (j.Status == JobStatus.Broken) line += "  est. repair ~" + Fmt.Duration(j.Estimate);
            Say(line);
        };

        Stopwatch total = Stopwatch.StartNew();
        Thread runner = new Thread(() => engine.Run(jobs)) { IsBackground = true };
        runner.Start();
        double nextHeartbeat = heartbeat;
        while (!runner.Join(StatusIntervalMs))
        {
            List<FileJob> running = jobs.Where(j => j.State == JobState.Running).ToList();
            if (interactive)
            {
                if (engine.Scanning) ShowStatus(null, 0, 0, 0, string.Format("checking {0}/{1}...", filesChecked(), jobs.Count));
                else ShowStatus(running, repairsDone, repairs.Count, total.Elapsed.TotalSeconds, null);
            }
            if (verbose && total.Elapsed.TotalSeconds >= nextHeartbeat)
            {
                nextHeartbeat += heartbeat;
                if (running.Count > 0)
                    Say(string.Format("  in progress ({0}): {1}", Fmt.Duration(total.Elapsed.TotalSeconds),
                        string.Join("; ", running.Select(j => j.Display + " " + Describe(j)).ToArray())));
            }
        }
        ShowStatus(null, 0, 0, 0, null);

        Console.WriteLine();
        Console.WriteLine("done in {0}: {1} (peak memory {2})", Fmt.Duration(total.Elapsed.TotalSeconds),
            string.Join(", ", counts.OrderBy(k => k.Key).Select(k => k.Value + " " + k.Key).ToArray()),
            Fmt.Bytes(Process.GetCurrentProcess().PeakWorkingSet64));
        if (done < jobs.Count)
        {
            int unrepaired = jobs.Count(j => j.Before != null && j.Status == null);
            Console.WriteLine("stopped early: {0} file(s) not checked, {1} broken file(s) not repaired yet", jobs.Count - filesChecked(), unrepaired);
        }
        if (opt.LogPath != null) Console.WriteLine("log: " + Paths.Short(opt.LogPath));
        if (engine.LogProblem != null) Console.Error.WriteLine("warning: " + engine.LogProblem + "; some rows are missing from it");
        return jobs.Any(j => j.Failed) ? ExitCode.SomeFailed : ExitCode.Ok;
    }

    // "42% fusing overlaps 6m12s" while repairing, otherwise what it's doing ("reading 3.1s").
    static string Describe(FileJob j)
    {
        if (j.Phase != JobPhase.Repair) return j.Activity + " " + Fmt.Duration(j.Elapsed);
        string stage = j.RepairStage;
        return string.Format(CultureInfo.InvariantCulture, "{0:0%}{1} {2}", j.Progress, stage != null ? " " + stage : "", Fmt.Duration(j.Elapsed));
    }

    static void ShowStatus(List<FileJob> running, int done, int count, double seconds, string text)
    {
        string line = text ?? "";
        if (running != null && running.Count > 0)
            line = string.Format("[{0}/{1} {2}] ", done, count, Fmt.Duration(seconds)) +
                string.Join(" | ", running.Select(j => Path.GetFileNameWithoutExtension(j.Input) + " " + Describe(j)).ToArray());
        int width = Math.Max(MinStatusWidth, ConsoleWidth() - 1);   // a full-width line would wrap the cursor
        if (line.Length > width) line = line.Substring(0, width - Ellipsis.Length) + Ellipsis;
        lock (consoleLock)
        {
            Console.Write("\r" + line.PadRight(statusLine.Length) + "\r");
            statusLine = line;
            Console.Write(line);
        }
    }

    static int ConsoleWidth()
    {
        try { return Console.WindowWidth; } catch (IOException) { return FallbackConsoleWidth; }
    }

    // Prints a line above the live status line.
    static void Say(string line)
    {
        lock (consoleLock)
        {
            if (interactive && statusLine.Length > 0) Console.Write("\r" + new string(' ', statusLine.Length) + "\r");
            Console.WriteLine(line);
            if (interactive) Console.Write(statusLine);
        }
    }

    static int InstallMenu()
    {
        string gui = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "3mfBatchRepair-gui.exe");
        if (!File.Exists(gui)) { Console.Error.WriteLine("error: 3mfBatchRepair-gui.exe isn't next to 3mfBatchRepair.exe; build it first"); return ExitCode.BadArgs; }
        ShellMenu.Install(gui);
        Console.WriteLine("added \"Repair with 3mfBatchRepair\" to the right-click menu for .stl/.obj/.3mf files, folders, and the empty space inside a folder");
        Console.WriteLine("(on Windows 11 it's under \"Show more options\"); it opens " + gui);
        return ExitCode.Ok;
    }
}
