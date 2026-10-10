using Avalonia;
using KospetMapMaker.Core;

namespace KospetMapMaker;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--cli")
            return Cli.Run(args.Skip(1).ToArray());
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}

/// <summary>
/// Headless mode:
///   KospetMapMaker --cli (--region kaliningrad | --pbf file.osm.pbf) --out DIR [--name NAME]
///       [--step 100|50] [--no-contours] [--no-rivers] [--no-extra] [--no-paths] [--no-service] [--no-tracks] [--refresh]
/// </summary>
internal static class Cli
{
    public static int Run(string[] a)
    {
        string? Arg(string k) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        bool Has(string k) => a.Contains(k);
        var job = new JobSpec { OutDir = Arg("--out") ?? ".", MapName = Arg("--name") ?? "", RefreshPbf = Has("--refresh") };
        var o = job.Options;
        o.Contours = !Has("--no-contours"); o.Rivers = !Has("--no-rivers"); o.ExtraNatural = !Has("--no-extra");
        o.Paths = !Has("--no-paths"); o.Service = !Has("--no-service"); o.Tracks = !Has("--no-tracks");
        if (int.TryParse(Arg("--step"), out int st) && st > 0) o.ContourStep = st;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            if (Arg("--pbf") is { } pbf) job.LocalPbf = pbf;
            else if (Arg("--region") is { } rid)
            {
                var regions = Sources.LoadRegionsAsync(false, cts.Token).GetAwaiter().GetResult();
                job.Region = regions.FirstOrDefault(r => r.Id.Equals(rid, StringComparison.OrdinalIgnoreCase))
                             ?? throw new ArgumentException("Unknown region id: " + rid);
            }
            else { Console.WriteLine("need --region ID or --pbf FILE"); return 2; }
            string last = "";
            Pipeline.RunAsync(job, Console.WriteLine, (s, f) =>
            {
                var t = f < 0 ? s : $"{s} {f * 100:F0}%";
                if (t != last) { last = t; Console.Error.Write("\r" + t.PadRight(30)); }
            }, cts.Token).GetAwaiter().GetResult();
            Console.Error.WriteLine();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("\nERROR: " + e.Message);
            return 1;
        }
    }
}
