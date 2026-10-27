// Reading and writing STL, OBJ and 3MF meshes, and mesh health stats.
// Kept to C# 5 so Windows PowerShell 5.1's Add-Type can compile it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

public class Mesh
{
    public List<float> V = new List<float>();   // x,y,z triples
    public List<int> T = new List<int>();       // v1,v2,v3 triples
    public int VertexCount { get { return V.Count / 3; } }
    public int TriangleCount { get { return T.Count / 3; } }
}

public static class MeshIO
{
    const string CoreNs = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    const int WriteBufferBytes = 1 << 16;   // for text formats, which are written a few characters at a time

    public static readonly string[] Extensions = { ".stl", ".obj", ".3mf" };

    public static bool IsMesh(string path)
    {
        return Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;
    }

    // Reads any supported format. `appearance` is true when the file carries more than geometry that a rewrite
    // would lose: an OBJ with materials or textures, or any 3MF (transforms, colors, materials).
    // A file with no triangles is an error rather than an empty mesh, which would otherwise check clean.
    public static Mesh Read(string path, out bool appearance)
    {
        string ext = FormatOf(path);
        appearance = ext == ".3mf";
        Mesh m = ext == ".3mf" ? Read3mf(path) : ext == ".obj" ? ReadObj(path, out appearance) : ReadStl(path);
        if (m.TriangleCount == 0) throw new InvalidDataException("no triangles in the file");
        return m;
    }

    public static Mesh Read(string path)
    {
        bool appearance;
        return Read(path, out appearance);
    }

    // Writes in the format the path's extension names (STL unless it's .obj or .3mf).
    public static void Write(Mesh m, string path)
    {
        string ext = FormatOf(path);
        if (ext == ".3mf") Write3mf(m, path);
        else if (ext == ".obj") WriteObj(m, path);
        else WriteStl(m, path);
    }

    // The format a path names; "x.obj.partial" (a file being written) counts as OBJ.
    // (Paths.PartialSuffix, repeated here because spike scripts compile this file on its own.)
    const string PartialSuffix = ".partial";

    static string FormatOf(string path)
    {
        if (path.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - PartialSuffix.Length);
        return Path.GetExtension(path).ToLowerInvariant();
    }

    // ---------- OBJ ----------

    // Geometry only: vertices and faces, with faces of more than three corners split into a fan of triangles
    // (right for the flat, convex faces modelling tools export). Vertices at the same position are merged, as
    // for STL, so both formats are checked the same way. Materials, textures and normals aren't kept.
    public static Mesh ReadObj(string path, out bool appearance)
    {
        Mesh m = new Mesh();
        Dictionary<VKey, int> weld = new Dictionary<VKey, int>();
        List<int> remap = new List<int>();   // OBJ vertex number - 1 -> merged vertex
        List<int> face = new List<int>();
        char[] space = { ' ', '\t' };
        appearance = false;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length < 2) continue;
            if (line[0] == 'v' && (line[1] == ' ' || line[1] == '\t'))
            {
                string[] p = line.Split(space, StringSplitOptions.RemoveEmptyEntries);
                remap.Add(Weld(m, weld, F(p[1]), F(p[2]), F(p[3])));
            }
            else if (line[0] == 'f' && (line[1] == ' ' || line[1] == '\t'))
            {
                string[] p = line.Split(space, StringSplitOptions.RemoveEmptyEntries);
                face.Clear();
                for (int i = 1; i < p.Length; i++)
                {
                    int slash = p[i].IndexOf('/');
                    int n = int.Parse(slash < 0 ? p[i] : p[i].Substring(0, slash), CultureInfo.InvariantCulture);
                    face.Add(remap[n > 0 ? n - 1 : remap.Count + n]);   // negative numbers count back from the latest vertex
                }
                for (int i = 1; i + 1 < face.Count; i++)
                {
                    m.T.Add(face[0]); m.T.Add(face[i]); m.T.Add(face[i + 1]);
                }
            }
            else if (line.StartsWith("vt ") || line.StartsWith("usemtl") || line.StartsWith("mtllib"))
                appearance = true;
        }
        return m;
    }

    public static void WriteObj(Mesh m, string path)
    {
        using (StreamWriter w = new StreamWriter(File.Create(path), new UTF8Encoding(false), WriteBufferBytes))
        {
            w.Write("# written by 3mfBatchRepair\n");
            for (int i = 0; i < m.V.Count; i += 3)
                w.Write("v " + S(m.V[i]) + " " + S(m.V[i + 1]) + " " + S(m.V[i + 2]) + "\n");
            for (int i = 0; i < m.T.Count; i += 3)
                w.Write("f " + (m.T[i] + 1) + " " + (m.T[i + 1] + 1) + " " + (m.T[i + 2] + 1) + "\n");
        }
    }

    // ---------- STL ----------

    // Binary STL layout: an 80-byte header, the triangle count, then per triangle a normal and three corners
    // (three floats each) and a 2-byte attribute.
    const int StlHeaderBytes = 80;
    const int StlFirstTriangle = StlHeaderBytes + sizeof(uint);
    const int StlPointBytes = 3 * sizeof(float);
    const int StlTriangleBytes = 4 * StlPointBytes + sizeof(ushort);
    const string StlAsciiStart = "solid";

    public static Mesh ReadStl(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        Mesh m = new Mesh();
        Dictionary<VKey, int> weld = new Dictionary<VKey, int>();
        // Binary when the size matches the triangle count exactly. Some exporters pad binary files, so one that's
        // longer than its count needs still counts as binary unless it starts like an ASCII file ("solid").
        long n = data.Length >= StlFirstTriangle ? (long)BitConverter.ToUInt32(data, StlHeaderBytes) : -1;
        bool ascii = data.Length >= StlAsciiStart.Length
            && Encoding.ASCII.GetString(data, 0, StlAsciiStart.Length).Equals(StlAsciiStart, StringComparison.OrdinalIgnoreCase);
        long binaryBytes = StlFirstTriangle + StlTriangleBytes * n;
        bool binary = n >= 0 && (binaryBytes == data.Length || !ascii && binaryBytes <= data.Length);
        if (binary)
        {
            for (uint i = 0; i < n; i++)
            {
                int o = StlFirstTriangle + (int)i * StlTriangleBytes + StlPointBytes;   // skip the normal
                for (int k = 0; k < 3; k++, o += StlPointBytes)
                    m.T.Add(Weld(m, weld, BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + sizeof(float)),
                        BitConverter.ToSingle(data, o + 2 * sizeof(float))));
            }
        }
        else
        {
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (!line.StartsWith("vertex")) continue;
                string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                m.T.Add(Weld(m, weld, F(p[1]), F(p[2]), F(p[3])));
            }
        }
        return m;
    }

    static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }

    struct VKey : IEquatable<VKey>
    {
        public float X, Y, Z;
        public bool Equals(VKey o) { return X.Equals(o.X) && Y.Equals(o.Y) && Z.Equals(o.Z); }
        public override bool Equals(object o) { return o is VKey && Equals((VKey)o); }
        const int HashPrime = 397;   // the usual multiplier for combining hash codes
        public override int GetHashCode() { return (X.GetHashCode() * HashPrime ^ Y.GetHashCode()) * HashPrime ^ Z.GetHashCode(); }
    }

    static int Weld(Mesh m, Dictionary<VKey, int> weld, float x, float y, float z)
    {
        VKey key = new VKey { X = x, Y = y, Z = z };
        int idx;
        if (!weld.TryGetValue(key, out idx))
        {
            idx = m.VertexCount;
            weld[key] = idx;
            m.V.Add(x); m.V.Add(y); m.V.Add(z);
        }
        return idx;
    }

    public static void WriteStl(Mesh m, string path)
    {
        using (BinaryWriter w = new BinaryWriter(File.Create(path)))
        {
            w.Write(new byte[StlHeaderBytes]);
            w.Write((uint)m.TriangleCount);
            for (int t = 0; t < m.T.Count; t += 3)
            {
                int a = m.T[t] * 3, b = m.T[t + 1] * 3, c = m.T[t + 2] * 3;
                double ux = m.V[b] - m.V[a], uy = m.V[b + 1] - m.V[a + 1], uz = m.V[b + 2] - m.V[a + 2];
                double vx = m.V[c] - m.V[a], vy = m.V[c + 1] - m.V[a + 1], vz = m.V[c + 2] - m.V[a + 2];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 0) { nx /= len; ny /= len; nz /= len; }
                w.Write((float)nx); w.Write((float)ny); w.Write((float)nz);
                for (int k = 0; k < 3; k++)
                {
                    int v = m.T[t + k] * 3;
                    w.Write(m.V[v]); w.Write(m.V[v + 1]); w.Write(m.V[v + 2]);
                }
                w.Write((ushort)0);
            }
        }
    }

    // ---------- 3MF ----------

    public static void Write3mf(Mesh m, string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>");
            WriteEntry(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/></Relationships>");
            using (StreamWriter sw = new StreamWriter(zip.CreateEntry("3D/3dmodel.model").Open(), new UTF8Encoding(false)))
            {
                sw.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<model unit=\"millimeter\" xml:lang=\"en-US\" xmlns=\"" + CoreNs + "\">\n<resources><object id=\"1\" type=\"model\"><mesh><vertices>\n");
                for (int i = 0; i < m.V.Count; i += 3)
                    sw.Write("<vertex x=\"" + S(m.V[i]) + "\" y=\"" + S(m.V[i + 1]) + "\" z=\"" + S(m.V[i + 2]) + "\"/>\n");
                sw.Write("</vertices><triangles>\n");
                for (int i = 0; i < m.T.Count; i += 3)
                    sw.Write("<triangle v1=\"" + m.T[i] + "\" v2=\"" + m.T[i + 1] + "\" v3=\"" + m.T[i + 2] + "\"/>\n");
                sw.Write("</triangles></mesh></object></resources>\n<build><item objectid=\"1\"/></build>\n</model>\n");
            }
        }
    }

    static string S(float f) { return f.ToString("R", CultureInfo.InvariantCulture); }

    static void WriteEntry(ZipArchive zip, string name, string text)
    {
        using (StreamWriter sw = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) sw.Write(text);
    }

    // Reads every <mesh> in the package's .model parts into one Mesh (build transforms ignored).
    public static Mesh Read3mf(string path)
    {
        Mesh m = new Mesh();
        using (ZipArchive zip = ZipFile.OpenRead(path))
        {
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                if (!e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase)) continue;
                using (XmlReader r = XmlReader.Create(e.Open()))
                {
                    int baseIdx = 0;
                    while (r.Read())
                    {
                        if (r.NodeType != XmlNodeType.Element) continue;
                        if (r.LocalName == "mesh") baseIdx = m.VertexCount;
                        else if (r.LocalName == "vertex")
                        {
                            m.V.Add(F(r.GetAttribute("x"))); m.V.Add(F(r.GetAttribute("y"))); m.V.Add(F(r.GetAttribute("z")));
                        }
                        else if (r.LocalName == "triangle")
                        {
                            m.T.Add(baseIdx + int.Parse(r.GetAttribute("v1")));
                            m.T.Add(baseIdx + int.Parse(r.GetAttribute("v2")));
                            m.T.Add(baseIdx + int.Parse(r.GetAttribute("v3")));
                        }
                    }
                }
            }
        }
        return m;
    }

    // ---------- Stats ----------

    // How far a repair may move the bounding box and still count as unmoved: MaxShiftMm, or MaxShiftShare of the
    // model's size (its bounding box diagonal) if that's larger.
    const double MaxShiftMm = 0.05, MaxShiftShare = 0.01;

    // Describes how far the repaired mesh's bounding box moved, or null if it stayed put.
    public static string BoundsShift(Mesh before, Mesh after)
    {
        if (after.VertexCount == 0) return "repair returned an empty mesh";
        Box a = Box.Of(before), b = Box.Of(after);
        double tolerance = Math.Max(MaxShiftMm, a.Diagonal * MaxShiftShare), worst = 0;
        for (int k = 0; k < 3; k++)
            worst = Math.Max(worst, Math.Max(Math.Abs(a.Min[k] - b.Min[k]), Math.Abs(a.Max[k] - b.Max[k])));
        return worst <= tolerance ? null : string.Format(CultureInfo.InvariantCulture, "bounding box moved {0:0.###} mm", worst);
    }

    // The mesh's separate pieces: triangles sharing a vertex are in the same piece, and
    // Find(any of a triangle's vertices) names its piece.
    public static UnionFind Pieces(Mesh m)
    {
        UnionFind pieces = new UnionFind(m.VertexCount);
        for (int t = 0; t < m.T.Count; t += 3)
        {
            pieces.Union(m.T[t], m.T[t + 1]);
            pieces.Union(m.T[t], m.T[t + 2]);
        }
        return pieces;
    }

    public static MeshStats Stats(Mesh m)
    {
        Dictionary<long, int> undirected = new Dictionary<long, int>();
        Dictionary<long, int> directed = new Dictionary<long, int>();
        long n = m.VertexCount;
        int degenerate = 0;
        for (int t = 0; t < m.T.Count; t += 3)
        {
            int a = m.T[t], b = m.T[t + 1], c = m.T[t + 2];
            if (a == b || b == c || a == c) { degenerate++; continue; }
            for (int k = 0; k < 3; k++)   // edges a-b, b-c, c-a
            {
                int from = m.T[t + k], to = m.T[t + (k + 1) % 3];
                Count(undirected, Math.Min(from, to) * n + Math.Max(from, to));
                Count(directed, from * n + to);
            }
        }
        int boundary = 0, nonManifold = 0, flipped = 0;
        // In a closed, manifold mesh every edge is shared by exactly two triangles.
        foreach (int c in undirected.Values) { if (c == 1) boundary++; else if (c > 2) nonManifold++; }
        foreach (int c in directed.Values) if (c > 1) flipped++;
        MeshStats s = new MeshStats();
        s.Triangles = m.TriangleCount; s.Vertices = m.VertexCount; s.Degenerate = degenerate;
        s.BoundaryEdges = boundary; s.NonManifoldEdges = nonManifold; s.BadOrientationEdges = flipped;
        s.Shells = CountShells(m);
        return s;
    }

    static void Count(Dictionary<long, int> counts, long key)
    {
        int c;
        counts.TryGetValue(key, out c);
        counts[key] = c + 1;
    }

    // Number of separate pieces. Presupported models are typically a body plus hundreds of support pieces pushed
    // into it; fusing those is most of a repair's work.
    static int CountShells(Mesh m)
    {
        UnionFind pieces = Pieces(m);
        HashSet<int> roots = new HashSet<int>();
        for (int t = 0; t < m.T.Count; t += 3) roots.Add(pieces.Find(m.T[t]));
        return roots.Count;
    }
}

// Groups the numbers 0..n-1 into sets: Union joins two sets, Find names the set a number is in.
public class UnionFind
{
    readonly int[] parent;

    public UnionFind(int n)
    {
        parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
    }

    public int Find(int x)
    {
        while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
        return x;
    }

    public void Union(int a, int b) { parent[Find(b)] = Find(a); }
}

// An axis-aligned bounding box; empty until something is added.
public class Box
{
    public readonly double[] Min = { double.MaxValue, double.MaxValue, double.MaxValue };
    public readonly double[] Max = { double.MinValue, double.MinValue, double.MinValue };

    public static Box Of(Mesh m)
    {
        Box b = new Box();
        for (int v = 0; v < m.VertexCount; v++) b.Add(m, v);
        return b;
    }

    // Adds vertex v of m.
    public void Add(Mesh m, int v)
    {
        for (int k = 0; k < 3; k++)
        {
            Min[k] = Math.Min(Min[k], m.V[v * 3 + k]);
            Max[k] = Math.Max(Max[k], m.V[v * 3 + k]);
        }
    }

    public void Add(Box b)
    {
        for (int k = 0; k < 3; k++) { Min[k] = Math.Min(Min[k], b.Min[k]); Max[k] = Math.Max(Max[k], b.Max[k]); }
    }

    public double Size(int axis) { return Max[axis] - Min[axis]; }
    public double Center(int axis) { return (Min[axis] + Max[axis]) / 2; }
    public double Diagonal { get { return Math.Sqrt(Size(0) * Size(0) + Size(1) * Size(1) + Size(2) * Size(2)); } }

    public bool Overlaps(Box o)
    {
        for (int k = 0; k < 3; k++) if (Min[k] > o.Max[k] || o.Min[k] > Max[k]) return false;
        return true;
    }
}

public class MeshStats
{
    public int Triangles, Vertices, Degenerate, BoundaryEdges, NonManifoldEdges, BadOrientationEdges, Shells;

    // Edge-topology check only: overlapping-but-closed shells still count as clean.
    public bool Clean { get { return BadEdges == 0; } }

    // Every problem counted together (degenerate triangles included).
    public int BadEdges { get { return NonManifoldEdges + BadOrientationEdges + BoundaryEdges + Degenerate; } }

    public string Problems()
    {
        if (Clean) return "clean";
        List<string> p = new List<string>();
        if (NonManifoldEdges > 0) p.Add(NonManifoldEdges + " non-manifold");
        if (BadOrientationEdges > 0) p.Add(BadOrientationEdges + " flipped");
        if (BoundaryEdges > 0) p.Add(BoundaryEdges + " open");
        if (Degenerate > 0) p.Add(Degenerate + " degenerate");
        return string.Join(", ", p.ToArray());
    }

    public override string ToString()
    {
        return Triangles + " tris, " + Shells + (Shells == 1 ? " piece; " : " pieces; ") + Problems();
    }
}
