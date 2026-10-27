// Small helpers shared by every 3mfBatchRepair tool.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

// Targeting 4.8 turns off .NET's legacy path handling, so \\?\ paths (see Paths.Long) get past the 260-char limit.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

public static class Paths
{
    const string LongPrefix = @"\\?\", LongUncPrefix = @"\\?\UNC\", UncPrefix = @"\\";

    // Added to a file's name while it's being written (see AtomicWrite).
    public const string PartialSuffix = ".partial";

    // Absolute path with the \\?\ prefix, so deep model collections aren't capped at 260 characters.
    public static string Long(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(LongPrefix)) return full;
        return full.StartsWith(UncPrefix) ? LongUncPrefix + full.Substring(UncPrefix.Length) : LongPrefix + full;
    }

    // The same path without the \\?\ prefix, for showing to people.
    public static string Short(string path)
    {
        if (path == null) return null;
        if (path.StartsWith(LongUncPrefix)) return UncPrefix + path.Substring(LongUncPrefix.Length);
        return path.StartsWith(LongPrefix) ? path.Substring(LongPrefix.Length) : path;
    }

    // An exception's message for showing to people. .NET quotes the paths it was given, which here carry the
    // \\?\ prefix, so those are shortened too.
    public static string Message(Exception e)
    {
        return e.Message.Trim().Replace(LongUncPrefix, UncPrefix).Replace(LongPrefix, "");
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Writes to a temp name and swaps it in, so an interrupted write never leaves a half-written file behind.
    public static void AtomicWrite(string path, Action<string> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + PartialSuffix;
        try
        {
            write(tmp);
            SwapIn(tmp, path);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    // Moves a finished temp file to path, replacing whatever is there.
    public static void SwapIn(string tmp, string path)
    {
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    // Every file under dir, sorted the way Explorer sorts names ("part 2" before "part 10"): a folder's own files
    // first, then each subfolder in turn. Folders skipDir accepts are left out, with everything under them.
    public static IEnumerable<string> Walk(string dir, Func<string, bool> skipDir = null)
    {
        foreach (string f in Directory.GetFiles(dir).OrderBy(Path.GetFileName, ExplorerOrder.Instance)) yield return f;
        foreach (string d in Directory.GetDirectories(dir).OrderBy(Path.GetFileName, ExplorerOrder.Instance))
            if (skipDir == null || !skipDir(d))
                foreach (string f in Walk(d, skipDir)) yield return f;
    }
}

// Command-line parsing for the 3mfBatchRepair and 3mfMeshSplit exes. Bad arguments throw ArgumentException; Fail reports one.
public static class Args
{
    // The value after option args[i], moving i onto it.
    public static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException(args[i] + " needs a value");
        return args[++i];
    }

    // The whole number after option args[i] (at least min), moving i onto it.
    public static int Int(string[] args, ref int i, int min)
    {
        string option = args[i];
        int n;
        if (!int.TryParse(Next(args, ref i), out n) || n < min) throw new ArgumentException(option + " needs a whole number, at least " + min);
        return n;
    }

    // Prints the error and the usage; returns the "bad arguments" exit code.
    public static int Fail(ArgumentException e, string usage)
    {
        Console.Error.WriteLine("error: " + e.Message + "\n\n" + usage);
        return ExitCode.BadArgs;
    }
}

// What the command-line tools return; listed under "exit codes" in their usage text.
public static class ExitCode
{
    public const int Ok = 0, SomeFailed = 1, BadArgs = 2;
}

// Compares names the way Explorer sorts them, with numbers by value.
public class ExplorerOrder : IComparer<string>
{
    public static readonly ExplorerOrder Instance = new ExplorerOrder();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);

    public int Compare(string a, string b) { return StrCmpLogicalW(a, b); }
}

public static class Fmt
{
    const int TenthsUnder = 10;   // durations shorter than this many seconds show tenths
    const long KB = 1L << 10, MB = 1L << 20, GB = 1L << 30;

    // "4.2s", "37s", "6m12s", "2h05m". Rounded first, so 59.7 shows as 1m00s rather than 60s.
    public static string Duration(double seconds)
    {
        if (Math.Round(seconds, 1) < TenthsUnder) return seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        TimeSpan t = TimeSpan.FromSeconds(Math.Round(seconds));
        if (t.TotalMinutes < 1) return t.Seconds + "s";
        if (t.TotalHours < 1) return string.Format("{0}m{1:00}s", t.Minutes, t.Seconds);
        return string.Format("{0}h{1:00}m", (int)t.TotalHours, t.Minutes);
    }

    public static string Bytes(long n)
    {
        if (n >= GB) return (n / (double)GB).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        if (n >= MB) return (n / (double)MB).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return Math.Max(1, n / KB) + " KB";
    }
}
