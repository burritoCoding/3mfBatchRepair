// 3mfMeshSplit command line: split STL/OBJ/3MF files into one STL per separate part.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

static class MeshSplit
{
    const string Usage =
@"3mfMeshSplit - split STL/OBJ/3MF files into one STL per separate part

usage: 3mfMeshSplit <file-or-folder>... [options]

Each separate part is written as <name>_01.stl, <name>_02.stl, ... in a <name>_parts
folder next to the file, numbered in plate order (back row first, left to right).
Folders are searched recursively; files that are already a single part are skipped.

A part is a group of connected triangles. Pieces whose bounding boxes overlap are kept
together as one part (supports pushed into a model, the inner wall of a hollow model,
details sunk into a surface), so parts that only touch or overlap aren't torn apart.

options:
  -o, --out DIR      write all parts into DIR instead (named <name>_NN.stl)
  --dedupe           write identical parts once, named with how many to print,
                     e.g. Door_01 (print 4x).stl. Copies may be moved or turned
                     90/180/270 degrees; mirror images count as different parts
  --strict           split every connected piece, even overlapping ones
  --min-tris N       leave out parts with fewer than N triangles (stray fragments)
  --center           move each part to the origin, sitting on z = 0
                     (default: keep its position on the original plate)
  --list             show the parts without writing anything
  --overwrite        replace an earlier split (its part files are removed first)
  -q, --quiet        only print a line per file, not per part

exit codes: 0 ok, 1 some files failed, 2 bad arguments";

    static int Main(string[] args)
    {
        List<string> inputs = new List<string>();
        SplitOptions o = new SplitOptions();
        bool listOnly = false, quiet = false;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-h": case "--help": case "/?": Console.WriteLine(Usage); return ExitCode.Ok;
                    case "-o": case "--out": o.OutDir = Paths.Long(Args.Next(args, ref i)); break;
                    case "--strict": o.Strict = true; break;
                    case "--min-tris": o.MinTris = Args.Int(args, ref i, 0); break;
                    case "--center": o.Center = true; break;
                    case "--dedupe": o.Dedupe = true; break;
                    case "--list": listOnly = true; break;
                    case "--overwrite": o.Overwrite = true; break;
                    case "-q": case "--quiet": quiet = true; break;
                    default:
                        if (args[i].StartsWith("-")) throw new ArgumentException("unknown option " + args[i]);
                        inputs.Add(args[i]);
                        break;
                }
            }
        }
        catch (ArgumentException e) { return Args.Fail(e, Usage); }
        if (inputs.Count == 0) { Console.WriteLine(Usage); return ExitCode.Ok; }

        List<string> files = new List<string>();
        foreach (string raw in inputs)
        {
            string p = Paths.Long(raw);
            if (File.Exists(p)) files.Add(p);
            else if (Directory.Exists(p)) files.AddRange(Splitter.Walk(p));
            else { Console.Error.WriteLine("error: not found: " + raw); return ExitCode.BadArgs; }
        }

        int failed = 0, split = 0, single = 0, written = 0;
        Dictionary<string, string> names = Splitter.PartNames(files, o);
        foreach (string file in files)
        {
            try
            {
                SplitPlan p = Splitter.Plan(file, o, names[file]);
                string droppedNote = p.Dropped > 0 ? string.Format(" ({0} fragment(s) under {1} triangles left out)", p.Dropped, o.MinTris) : "";
                if (p.Single && p.Copies == 0)
                {
                    single++;
                    Console.WriteLine("{0}: a single part, nothing to split{1}", Paths.Short(file), droppedNote);
                    continue;
                }
                if (!listOnly && p.AlreadySplit && !o.Overwrite)
                {
                    failed++;
                    Console.WriteLine("{0}: already split into {1}; --overwrite to redo", Paths.Short(file), Paths.Short(p.Dir));
                    continue;
                }

                string dedupeNote = !o.Dedupe ? "" : p.Copies == 0 ? ", no identical parts"
                    : string.Format(" ({0} identical {1} folded into \"print Nx\" files)", p.Copies, p.Copies == 1 ? "copy" : "copies");
                Console.WriteLine("{0}: {1} parts{2}{3}{4}", Paths.Short(file), p.Parts.Count, dedupeNote, listOnly ? "" : " -> " + Paths.Short(p.Dir), droppedNote);
                if (!quiet)
                {
                    int nameWidth = p.OutNames.Max(n => Path.GetFileName(n).Length);
                    for (int i = 0; i < p.Parts.Count; i++)
                    {
                        Part part = p.Parts[i];
                        Console.WriteLine("  {0}  {1,8} tris  {2} x {3} x {4} mm{5}", Path.GetFileName(p.OutNames[i]).PadRight(nameWidth), part.Triangles.Count,
                            Mm(part.Box.Size(0)), Mm(part.Box.Size(1)), Mm(part.Box.Size(2)), part.Pieces > 1 ? "  (" + part.Pieces + " overlapping pieces)" : "");
                    }
                }
                if (!listOnly)
                {
                    Splitter.Write(p, o);
                    written += p.Parts.Count;
                }
                split++;
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine("{0}: failed: {1}", Paths.Short(file), Paths.Message(e));
            }
        }
        if (files.Count > 1)
            Console.WriteLine("\n{0} file(s): {1} split{2}, {3} single part, {4} failed or skipped",
                files.Count, split, listOnly ? "" : " into " + written + " parts", single, failed);
        return failed > 0 ? ExitCode.SomeFailed : ExitCode.Ok;
    }

    static string Mm(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
}
