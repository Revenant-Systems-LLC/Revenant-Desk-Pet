using System.Diagnostics;
using System.Text;

namespace DesktopPet;

/// <summary>
/// Frame timing, on with --perf. Every 5 s it logs, for the gap between ticks,
/// the time inside a tick and each job a tick runs, the p50, p95 and max in
/// milliseconds; plus redraws per second and the process's CPU use. Off, each
/// call costs one bool check.
/// </summary>
static class Perf
{
    public static bool Enabled;
    const double ReportEvery = 5;

    static readonly Dictionary<string, List<double>> samples = new();
    static readonly List<string> order = new();
    static double windowStart = -1;
    static TimeSpan cpuStart;
    static int presents;
    static TimeSpan gcStart;
    static int gen2Start;

    public static long Now => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void Add(string name, double ms)
    {
        if (!Enabled)
            return;
        if (!samples.TryGetValue(name, out var list))
        {
            samples[name] = list = new List<double>();
            order.Add(name);
        }
        list.Add(ms);
    }

    /// <summary>Record the time since <paramref name="start"/> (from <see cref="Now"/>) under <paramref name="name"/>.</summary>
    public static void Since(string name, long start)
    {
        if (Enabled)
            Add(name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    public static void Presented()
    {
        if (Enabled)
            presents++;
    }

    /// <summary>Once per tick: logs and starts a new window every 5 s.</summary>
    public static void Report(double t)
    {
        if (!Enabled)
            return;
        if (windowStart < 0)
        {
            windowStart = t;
            cpuStart = ProcessCpu();
            gcStart = GC.GetTotalPauseDuration();
            gen2Start = GC.CollectionCount(2);
            return;
        }
        double wall = t - windowStart;
        if (wall < ReportEvery)
            return;
        var cpu = ProcessCpu();
        var sb = new StringBuilder();
        var gc = GC.GetTotalPauseDuration();
        int gen2 = GC.CollectionCount(2);
        sb.Append($"perf {wall:0.0}s cpu={(cpu - cpuStart).TotalSeconds / wall * 100:0.00}% of one core redraws={presents / wall:0.0}/s gc-pause={(gc - gcStart).TotalMilliseconds:0}ms gen2={gen2 - gen2Start}");
        foreach (string name in order)
        {
            var list = samples[name];
            if (list.Count == 0)
                continue;
            list.Sort();
            sb.Append($" | {name} n={list.Count} p50={At(list, 0.5):0.00} p95={At(list, 0.95):0.00} max={list[^1]:0.00}");
            list.Clear();
        }
        Log.Info(sb.ToString());
        windowStart = t;
        cpuStart = cpu;
        gcStart = gc;
        gen2Start = gen2;
        presents = 0;
    }

    static TimeSpan ProcessCpu()
    {
        using var p = Process.GetCurrentProcess();
        return p.TotalProcessorTime;
    }

    static double At(List<double> sorted, double q) => sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];
}
