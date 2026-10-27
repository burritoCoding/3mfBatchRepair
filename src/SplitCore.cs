// Splitting STL/OBJ/3MF files into one STL per separate part, shared by the 3mfMeshSplit command line and window.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

public class SplitOptions
{
    public string OutDir;       // null: a <name>_parts folder next to each file
    public bool Strict;         // split overlapping pieces too
    public bool Dedupe;         // write identical parts once, named "(print Nx)"
    public bool Center;         // move each part to the origin, on z = 0
    public bool Overwrite;      // replace an earlier split of the same file
    public int MinTris;         // leave out parts with fewer triangles
}

// What splitting one file will produce: worked out by Splitter.Plan, written by Splitter.Write.
public class SplitPlan
{
    public string File, Dir;
    public Mesh Mesh;
    public List<Part> Parts;        // the parts to write, in plate order
    public int[] Counts;            // how many identical copies each part stands for (with Dedupe)
    public string[] OutNames;
    public int Dropped;             // fragments under MinTris
    public int Copies;              // identical copies folded away (with Dedupe)
    public List<string> Earlier;    // part files from an earlier split of this file

    public bool Single { get { return Parts.Count <= 1; } }
    public bool AlreadySplit { get { return Earlier.Count > 0; } }
}

public class Part
{
    public readonly List<int> Triangles = new List<int>();   // triangle numbers in the source mesh
    public readonly Box Box = new Box();
    public int Pieces = 1;                                    // connected pieces grouped into this part
}

public static class Splitter
{
    const string PartsFolderSuffix = "_parts";   // <name>_parts, next to the file
    const int MinNumberDigits = 2;               // part numbers are at least _01, _02, ...

    // Splits a mesh into parts. A piece is a set of triangles connected through shared corners. With
    // mergeOverlaps, pieces whose bounding boxes overlap are kept together as one part: real parts are often
    // several overlapping pieces (supports pushed into a body, the inner wall of a hollow model, a detail
    // modelled separately and sunk into the surface). Parts come back in plate order: back row first, left to right.
    public static List<Part> Split(Mesh m, bool mergeOverlaps)
    {
        UnionFind connected = MeshIO.Pieces(m);
        Dictionary<int, Part> byRoot = new Dictionary<int, Part>();
        for (int t = 0; t < m.TriangleCount; t++)
        {
            int r = connected.Find(m.T[t * 3]);
            Part p;
            if (!byRoot.TryGetValue(r, out p)) byRoot[r] = p = new Part();
            p.Triangles.Add(t);
            for (int k = 0; k < 3; k++) p.Box.Add(m, m.T[t * 3 + k]);
        }
        List<Part> pieces = byRoot.Values.ToList();
        List<Part> parts = mergeOverlaps ? MergeOverlapping(pieces) : pieces;
        return PlateOrder(parts);
    }

    static List<Part> MergeOverlapping(List<Part> pieces)
    {
        int n = pieces.Count;
        UnionFind groups = new UnionFind(n);
        // Sweep along x so only pieces that could overlap get compared.
        int[] order = Enumerable.Range(0, n).OrderBy(i => pieces[i].Box.Min[0]).ToArray();
        for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n && pieces[order[b]].Box.Min[0] <= pieces[order[a]].Box.Max[0]; b++)
                if (pieces[order[a]].Box.Overlaps(pieces[order[b]].Box))
                    groups.Union(order[a], order[b]);

        Dictionary<int, Part> merged = new Dictionary<int, Part>();
        for (int i = 0; i < n; i++)
        {
            int r = groups.Find(i);
            Part p;
            if (!merged.TryGetValue(r, out p)) { merged[r] = pieces[i]; continue; }
            p.Triangles.AddRange(pieces[i].Triangles);
            p.Box.Add(pieces[i].Box);
            p.Pieces += pieces[i].Pieces;
        }
        foreach (Part p in merged.Values) p.Triangles.Sort();
        return merged.Values.ToList();
    }

    // Rows from the back of the plate (largest y) to the front, each row left to right. A part joins the
    // current row when its y range overlaps the row's first part.
    static List<Part> PlateOrder(List<Part> parts)
    {
        List<Part> byY = parts.OrderByDescending(p => p.Box.Center(1)).ToList();
        List<Part> result = new List<Part>();
        int i = 0;
        while (i < byY.Count)
        {
            Box first = byY[i].Box;
            List<Part> row = new List<Part>();
            while (i < byY.Count && byY[i].Box.Max[1] >= first.Min[1] && byY[i].Box.Min[1] <= first.Max[1]) row.Add(byY[i++]);
            result.AddRange(row.OrderBy(p => p.Box.Min[0]));
        }
        return result;
    }

    // ---------- files ----------

    // The folder a file's parts go in.
    public static string PartsDir(string file, SplitOptions o)
    {
        return o.OutDir ?? Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + PartsFolderSuffix);
    }

    // The name each file's parts are written under (<name>_01.stl, ...), for a batch in order. Normally the file's own
    // name; when two files would write into the same folder under the same name (door.stl next to door.obj, or two
    // door.stl files with OutDir), the later ones get "door (2)", "door (3)", ... so none overwrites or clears
    // another's parts.
    public static Dictionary<string, string> PartNames(IEnumerable<string> files, SplitOptions o)
    {
        Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            if (names.ContainsKey(file)) continue;
            string stem = Path.GetFileNameWithoutExtension(file), dir = PartsDir(file, o), name = stem;
            for (int n = 2; !taken.Add(Path.Combine(dir, name)); n++) name = stem + " (" + n + ")";
            names[file] = name;
        }
        return names;
    }

    // name: what to call the parts (from PartNames); the file's own name if null.
    public static SplitPlan Plan(string file, SplitOptions o, string name = null)
    {
        SplitPlan p = new SplitPlan { File = file };
        p.Mesh = MeshIO.Read(file);
        List<Part> parts = Split(p.Mesh, !o.Strict);
        p.Dropped = parts.Count(x => x.Triangles.Count < o.MinTris);
        parts = parts.Where(x => x.Triangles.Count >= o.MinTris).ToList();

        int[] counts = Enumerable.Repeat(1, parts.Count).ToArray();
        if (o.Dedupe && parts.Count > 1)
        {
            int[] copyOf = FindCopies(p.Mesh, parts);
            for (int i = 0; i < parts.Count; i++)
                if (copyOf[i] != -1) counts[copyOf[i]]++;
            p.Copies = copyOf.Count(c => c != -1);
            counts = counts.Where((c, i) => copyOf[i] == -1).ToArray();
            parts = parts.Where((x, i) => copyOf[i] == -1).ToList();
        }
        p.Parts = parts;
        p.Counts = counts;

        name = name ?? Path.GetFileNameWithoutExtension(file);
        p.Dir = PartsDir(file, o);
        int digits = Math.Max(MinNumberDigits, parts.Count.ToString().Length);
        p.OutNames = Enumerable.Range(0, parts.Count).Select(i => Path.Combine(p.Dir,
            name + "_" + (i + 1).ToString("D" + digits) + (counts[i] > 1 ? " (print " + counts[i] + "x)" : "") + ".stl")).ToArray();
        p.Earlier = Directory.Exists(p.Dir) ? Directory.GetFiles(p.Dir, name + "_*.stl").Where(f => IsPartName(name, f)).ToList() : new List<string>();
        return p;
    }

    // Writes the plan's parts (each through a temp file). An earlier split of the same file is cleared first,
    // since a redo can produce fewer parts or different names; without Overwrite that's refused.
    public static void Write(SplitPlan p, SplitOptions o)
    {
        if (p.AlreadySplit)
        {
            if (!o.Overwrite) throw new IOException("already split into " + Paths.Short(p.Dir));
            foreach (string f in p.Earlier) File.Delete(f);
        }
        for (int i = 0; i < p.Parts.Count; i++)
        {
            Part part = p.Parts[i];
            Paths.AtomicWrite(p.OutNames[i], tmp => MeshIO.WriteStl(Extract(p.Mesh, part, o.Center), tmp));
        }
    }

    // .stl/.obj/.3mf files under a folder, skipping the _parts folders earlier splits wrote.
    public static IEnumerable<string> Walk(string dir)
    {
        return Paths.Walk(dir, d => Path.GetFileName(d).EndsWith(PartsFolderSuffix, StringComparison.OrdinalIgnoreCase)).Where(MeshIO.IsMesh);
    }

    // True for files an earlier split of `name` wrote: name_NN.stl or name_NN (print Nx).stl.
    static bool IsPartName(string name, string file)
    {
        return Regex.IsMatch(Path.GetFileName(file), "^" + Regex.Escape(name) + @"_\d+( \(print \d+x\))?\.stl$", RegexOptions.IgnoreCase);
    }

    // ---------- duplicates ----------

    // How close two parts must be to count as copies of each other. Calibrated on a plate with four copies of
    // one hatch (mean 0.003 mm, under 0.5% of points apart) and doors that are deliberate variants of each other
    // (mean 0.03-0.07 mm, 3-7% of points over 0.1 mm apart): the gap between the two is wide.
    const double MaxMeanMm = 0.02, FarMm = 0.1, MaxFarShare = 0.02;

    // Quick checks before comparing shapes: copies have nearly the same triangle count (meshing varies a little)
    // and the same bounding box size, give or take SizeSlackMm per side.
    const double TriangleCountSlack = 0.05, SizeSlackMm = 0.2;

    const int QuarterTurns = 4;   // copies may be turned 0, 90, 180 or 270 degrees

    // For each part, the index of the earlier part it's a copy of, or -1. Copies may sit anywhere on the plate and
    // be turned by 90, 180 or 270 degrees; mirror images are not copies (left and right versions are different parts).
    // Triangle counts may differ slightly: the same shape is often meshed a little differently each time.
    public static int[] FindCopies(Mesh m, List<Part> parts)
    {
        int[] copyOf = new int[parts.Count];
        float[][] points = new float[parts.Count][];
        for (int i = 0; i < parts.Count; i++) copyOf[i] = -1;
        for (int i = 0; i < parts.Count; i++)
        {
            for (int j = 0; j < i; j++)
            {
                if (copyOf[j] != -1 || !SimilarSize(parts[i], parts[j])) continue;
                if (points[j] == null) points[j] = Points(m, parts[j]);
                if (points[i] == null) points[i] = Points(m, parts[i]);
                if (SameShape(points[j], points[i])) { copyOf[i] = j; break; }
            }
        }
        return copyOf;
    }

    static bool SimilarSize(Part a, Part b)
    {
        int ta = a.Triangles.Count, tb = b.Triangles.Count;
        if (Math.Abs(ta - tb) > TriangleCountSlack * Math.Max(ta, tb)) return false;
        Box x = a.Box, y = b.Box;
        if (!Close(x.Size(2), y.Size(2))) return false;
        bool same = Close(x.Size(0), y.Size(0)) && Close(x.Size(1), y.Size(1));
        bool turned = Close(x.Size(0), y.Size(1)) && Close(x.Size(1), y.Size(0));
        return same || turned;
    }

    static bool Close(double a, double b) { return Math.Abs(a - b) <= SizeSlackMm; }

    // The part's distinct vertices as x,y,z triples.
    static float[] Points(Mesh m, Part p)
    {
        HashSet<int> seen = new HashSet<int>();
        List<float> pts = new List<float>();
        foreach (int t in p.Triangles)
            for (int k = 0; k < 3; k++)
            {
                int v = m.T[t * 3 + k];
                if (seen.Add(v)) { pts.Add(m.V[v * 3]); pts.Add(m.V[v * 3 + 1]); pts.Add(m.V[v * 3 + 2]); }
            }
        return pts.ToArray();
    }

    static bool SameShape(float[] a, float[] b)
    {
        float[] na = Normalize(a, 0);
        PointGrid ga = new PointGrid(na);
        for (int turn = 0; turn < QuarterTurns; turn++)
        {
            float[] nb = Normalize(b, turn);
            int farA, farB;
            double meanA = new PointGrid(nb).MeanNearest(na, out farA);
            double meanB = ga.MeanNearest(nb, out farB);
            double share = (farA + farB) / (double)(na.Length / 3 + nb.Length / 3);
            if ((meanA + meanB) / 2 <= MaxMeanMm && share <= MaxFarShare) return true;
        }
        return false;
    }

    // Turns the points by `turns` quarter turns about z, then moves their bounding box to start at the origin.
    static float[] Normalize(float[] p, int turns)
    {
        float[] o = new float[p.Length];
        float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue };
        for (int i = 0; i < p.Length; i += 3)
        {
            float x = p[i], y = p[i + 1];
            for (int q = 0; q < turns; q++) { float t = x; x = -y; y = t; }   // 90 degrees anticlockwise
            o[i] = x; o[i + 1] = y; o[i + 2] = p[i + 2];
            for (int k = 0; k < 3; k++) mn[k] = Math.Min(mn[k], o[i + k]);
        }
        for (int i = 0; i < o.Length; i += 3)
            for (int k = 0; k < 3; k++) o[i + k] -= mn[k];
        return o;
    }

    // Points bucketed into a grid so nearest-point lookups only search the neighbourhood.
    class PointGrid
    {
        const float Cell = 0.25f;                // mm
        const int Reach = 2;                     // cells searched each way from a query point's own cell
        const double MaxDistance = Cell * Reach; // all a search that far can promise; nearer points are found exactly
        const int KeyBits = 20, KeyMask = (1 << KeyBits) - 1;   // bits per axis in a cell key
        readonly float[] pts;
        readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();

        public PointGrid(float[] pts)
        {
            this.pts = pts;
            for (int i = 0; i < pts.Length; i += 3)
            {
                long key = Key((int)(pts[i] / Cell), (int)(pts[i + 1] / Cell), (int)(pts[i + 2] / Cell));
                List<int> l;
                if (!cells.TryGetValue(key, out l)) cells[key] = l = new List<int>();
                l.Add(i);
            }
        }

        // Mean distance from each query point to the nearest grid point (capped at MaxDistance), and how many are over FarMm.
        public double MeanNearest(float[] q, out int far)
        {
            double sum = 0;
            far = 0;
            for (int i = 0; i < q.Length; i += 3)
            {
                int cx = (int)(q[i] / Cell), cy = (int)(q[i + 1] / Cell), cz = (int)(q[i + 2] / Cell);
                double best = MaxDistance;
                for (int dx = -Reach; dx <= Reach; dx++)
                    for (int dy = -Reach; dy <= Reach; dy++)
                        for (int dz = -Reach; dz <= Reach; dz++)
                        {
                            List<int> l;
                            if (!cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out l)) continue;
                            foreach (int j in l)
                            {
                                double ex = q[i] - pts[j], ey = q[i + 1] - pts[j + 1], ez = q[i + 2] - pts[j + 2];
                                double d = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                                if (d < best) best = d;
                            }
                        }
                sum += best;
                if (best > FarMm) far++;
            }
            return sum / Math.Max(1, q.Length / 3);
        }

        static long Key(int x, int y, int z)
        {
            return ((long)(x & KeyMask) << (2 * KeyBits)) + ((long)(y & KeyMask) << KeyBits) + (z & KeyMask);
        }
    }

    // The part as its own mesh; with center, moved to sit centred on the origin with its bottom at z = 0.
    public static Mesh Extract(Mesh m, Part part, bool center)
    {
        Mesh o = new Mesh();
        Dictionary<int, int> map = new Dictionary<int, int>();
        float dx = 0, dy = 0, dz = 0;
        if (center)
        {
            dx = (float)-part.Box.Center(0);
            dy = (float)-part.Box.Center(1);
            dz = (float)-part.Box.Min[2];
        }
        foreach (int t in part.Triangles)
            for (int k = 0; k < 3; k++)
            {
                int v = m.T[t * 3 + k], nv;
                if (!map.TryGetValue(v, out nv))
                {
                    nv = o.VertexCount;
                    map[v] = nv;
                    o.V.Add(m.V[v * 3] + dx); o.V.Add(m.V[v * 3 + 1] + dy); o.V.Add(m.V[v * 3 + 2] + dz);
                }
                o.T.Add(nv);
            }
        return o;
    }
}

