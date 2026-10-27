// Probe: what does Printing3DModel.RepairWithProgressAsync report, and can a repair be cancelled?
// usage: ProgressProbe <file.stl> [cancelAfterSeconds]
using System;
using System.Diagnostics;
using System.IO;
using Windows.Foundation;
using Windows.Graphics.Printing3D;
using Windows.Storage;
using Windows.Storage.Streams;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

static class ProgressProbe
{
    static int Main(string[] args)
    {
        Mesh mesh = MeshIO.ReadStl(args[0]);
        Console.WriteLine("loaded {0} tris; {1}", mesh.TriangleCount, MeshIO.Stats(mesh).Problems());
        string tmp = Path.Combine(Path.GetTempPath(), "3mfBatchRepair_probe.3mf");
        MeshIO.Write3mf(mesh, tmp);
        Printing3DModel model;
        using (IRandomAccessStream ras = FileRandomAccessStream.OpenAsync(tmp, FileAccessMode.Read).AsTask().Result)
            model = new Printing3D3MFPackage().LoadModelFromPackageAsync(ras).AsTask().Result;
        File.Delete(tmp);

        Stopwatch sw = Stopwatch.StartNew();
        double last = -1;
        int reports = 0;
        IAsyncOperationWithProgress<bool, double> op = model.RepairWithProgressAsync();
        op.Progress = (o, p) =>
        {
            reports++;
            if (Math.Abs(p - last) >= 0.01 || p == 0 || p >= 0.999)
            {
                Console.WriteLine("  {0,7:0.0}s  progress={1}", sw.Elapsed.TotalSeconds, p);
                last = p;
            }
        };
        if (args.Length > 1)
        {
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(double.Parse(args[1])));
            Console.WriteLine("  {0,7:0.0}s  calling Cancel()", sw.Elapsed.TotalSeconds);
            op.Cancel();
        }
        try
        {
            bool result = op.AsTask().Result;
            Console.WriteLine("done {0:0.0}s result={1} status={2} reports={3}", sw.Elapsed.TotalSeconds, result, op.Status, reports);
        }
        catch (AggregateException e)
        {
            Console.WriteLine("ended {0:0.0}s with {1}: {2} status={3}", sw.Elapsed.TotalSeconds, e.InnerException.GetType().Name, e.InnerException.Message.Trim(), op.Status);
        }
        return 0;
    }
}
