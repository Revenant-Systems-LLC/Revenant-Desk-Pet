using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopPet;

/// <summary>
/// What this PC can run: the graphics card and its memory (how much is free right
/// now), system memory, the processor, and the models Ollama has installed. From
/// that, which local models fit on the graphics card, which would spill into system
/// memory and crawl, which run on Ollama's servers instead, and the one to suggest.
/// Read-only: it asks, it never changes anything. Takes up to a couple of seconds
/// (nvidia-smi, Ollama's model list), so run it off the UI thread.
/// </summary>
static class SystemScan
{
    public sealed record Gpu(string Name, long TotalMB, long? UsedMB)
    {
        /// <summary>Free for a model now; without a reading, assume the desktop is using a fifth.</summary>
        public long FreeMB => TotalMB - (UsedMB ?? TotalMB / 5);
    }

    public sealed record Model(string Name, long Bytes, string? Family, double ParamsB, bool Cloud, bool Embedding);

    public enum Fit { Gpu, Cloud, TooBig }

    public sealed record Result(Gpu? Gpu, long RamMB, string Cpu, bool OllamaRunning, IReadOnlyList<Model> Models, DateTime When)
    {
        /// <summary>A model that fits needs its own size plus room for the conversation (about 0.8 GB).</summary>
        public Fit FitOf(Model m) =>
            m.Cloud ? Fit.Cloud :
            Gpu is { } g && m.Bytes / 1048576.0 + 800 <= g.FreeMB ? Fit.Gpu : Fit.TooBig;

        /// <summary>The model to suggest: the most capable chat model that fits on the graphics card.</summary>
        public Model? Recommended => Models
            .Where(m => !m.Embedding && FitOf(m) == Fit.Gpu && m.ParamsB >= 1)
            .OrderByDescending(Score)
            .ThenBy(m => m.Name.Length) // a tie: the plain library name (qwen3:8b) over a custom build of it
            .FirstOrDefault();

        public string Summary =>
            (Gpu is { } g ? $"{g.Name}, {g.TotalMB / 1024.0:0.#} GB ({g.FreeMB / 1024.0:0.#} GB free)" : "no graphics card found") +
            $", {RamMB / 1024.0:0} GB memory";
    }

    // A rough "how good is it" for picking: size first, then how well the family chats,
    // and the official Ollama library over re-uploads.
    static readonly string[] Families = ["qwen3", "llama", "gemma3", "gemma4", "qwen35", "granite", "mistral", "qwen2", "phi3", "cohere2", "olmo2"];

    static double Score(Model m)
    {
        int f = m.Family is null ? -1 : Array.IndexOf(Families, m.Family);
        double family = f < 0 ? 0 : 1.0 - f * 0.08;
        string n = m.Name.ToLowerInvariant();
        bool reasoner = n.Contains("-r1") || n.StartsWith("qwq") || n.Contains("think"); // they think out loud: slow, rambling chat
        return m.ParamsB + family + (n.Contains('/') ? 0 : 0.5) - (reasoner ? 1.5 : 0);
    }

    public static Result Run()
    {
        var (running, models) = OllamaModels();
        var gpu = ReadGpu();
        // A model Ollama has loaded right now would be swapped out for the next one: its memory counts as free.
        long held = running ? OllamaLoadedMB() : 0;
        if (gpu is { UsedMB: long used } && held > 0)
            gpu = gpu with { UsedMB = Math.Max(0, used - held) };
        return new Result(gpu, RamMB(), CpuName(), running, models, DateTime.Now);
    }

    static long OllamaLoadedMB()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var doc = JsonDocument.Parse(http.GetStringAsync(OllamaChat.Url + "/api/ps").GetAwaiter().GetResult());
            return doc.RootElement.GetProperty("models").EnumerateArray()
                .Sum(m => m.TryGetProperty("size_vram", out var v) && v.TryGetInt64(out long b) ? b : 0) / 1048576;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // ------------------------------------------------------------ hardware

    static Gpu? ReadGpu()
    {
        // NVIDIA: nvidia-smi reports total and used memory, the used part including other apps.
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total,memory.used --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                string line = p.StandardOutput.ReadLine() ?? "";
                p.WaitForExit(3000);
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 3 && long.TryParse(parts[1], out long total) && long.TryParse(parts[2], out long used))
                    return new Gpu(parts[0], total, used);
            }
        }
        catch (Exception)
        {
            // not NVIDIA, or no nvidia-smi
        }
        // Anything else: the driver's own record of its memory size (64-bit, unlike WMI's).
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            Gpu? best = null;
            foreach (string sub in cls?.GetSubKeyNames() ?? [])
            {
                using var k = cls!.OpenSubKey(sub);
                if (k?.GetValue("HardwareInformation.qwMemorySize") is long bytes && bytes > 0)
                {
                    var g = new Gpu(k.GetValue("DriverDesc") as string ?? "graphics card", bytes / 1048576, null);
                    if (best is null || g.TotalMB > best.TotalMB)
                        best = g;
                }
            }
            return best;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static long RamMB()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? (long)(m.ullTotalPhys / 1048576) : 0;
    }

    static string CpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    // ------------------------------------------------------------ Ollama

    static (bool Running, List<Model> Models) OllamaModels()
    {
        var list = new List<Model>();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string json = http.GetStringAsync(OllamaChat.Url + "/api/tags").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                string name = m.GetProperty("name").GetString() ?? "";
                long size = m.TryGetProperty("size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;
                bool cloud = m.TryGetProperty("remote_host", out var rh) && rh.ValueKind == JsonValueKind.String && rh.GetString()!.Length > 0;
                string? family = null, pars = null;
                if (m.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    family = d.TryGetProperty("family", out var f) ? f.GetString() : null;
                    pars = d.TryGetProperty("parameter_size", out var ps) ? ps.GetString() : null;
                }
                bool embedding = family is "bert" or "nomic-bert" || name.Contains("embed", StringComparison.OrdinalIgnoreCase) || name.StartsWith("bge", StringComparison.OrdinalIgnoreCase);
                list.Add(new Model(name, size, string.IsNullOrEmpty(family) ? null : family, ParseParams(pars, size), cloud, embedding));
            }
            return (true, list);
        }
        catch (Exception)
        {
            return (false, list); // not running (the pet can start it)
        }
    }

    /// <summary>"8.0B" is 8, "566.70M" is 0.57; without it, a 4-bit model is roughly 0.6 GB per billion.</summary>
    static double ParseParams(string? s, long bytes)
    {
        if (!string.IsNullOrWhiteSpace(s))
        {
            char unit = char.ToUpperInvariant(s[^1]);
            if (double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return unit switch { 'T' => v * 1000, 'B' => v, 'M' => v / 1000, _ => v };
        }
        return bytes / 0.6e9;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
