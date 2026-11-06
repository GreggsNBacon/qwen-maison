using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Maison.Models;

namespace Maison.Services;

public sealed record ProcUsage(int Pid, string Name, double CpuPercent, double RamGb, double VramGb);

public sealed record StatsSnapshot(
    DateTime At,
    GpuInfo? Gpu,
    double CpuPercent, string CpuName, int Cores, int Threads, int? CpuMhz,
    double RamUsedGb, double RamTotalGb,
    double OllamaCpuPercent, double OllamaRamGb, double OllamaVramGb,
    IReadOnlyList<ProcUsage> VramUsers,
    IReadOnlyList<ProcUsage> TopCpu,
    IReadOnlyList<ProcUsage> TopRam,
    TimeSpan Uptime)
{
    public double RamPercent => RamTotalGb > 0 ? 100 * RamUsedGb / RamTotalGb : 0;
    /// <summary>GPU memory used by other apps (not Ollama).</summary>
    public double OtherVramGb => VramUsers.Where(p => !StatsService.IsOllama(p.Name)).Sum(p => p.VramGb);
}

/// <summary>
/// Polls CPU, RAM, GPU (nvidia-smi) and per-process GPU memory every 2 s. Shared by the Dashboard, the Terminals
/// strip, the System page and the web portal.
/// </summary>
public static class StatsService
{
    public static StatsSnapshot? Latest { get; private set; }
    public static event Action<StatsSnapshot>? Updated;

    /// <summary>Set while the System page is visible: also gathers per-process CPU/RAM (costlier).</summary>
    public static bool Detailed { get; set; }

    private static readonly string CpuNameValue = ReadCpuName();
    private static readonly int? CpuMhzValue = ReadCpuMhz();
    private static readonly int PhysicalCores = CountCores();
    private static long _prevIdle, _prevKernel, _prevUser;
    private static readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> PrevProc = [];
    private static readonly Dictionary<int, string> ProcNames = [];
    private static Task? _loop;

    public static bool IsOllama(string name) =>
        name.StartsWith("ollama", StringComparison.OrdinalIgnoreCase) || name.StartsWith("llama-server", StringComparison.OrdinalIgnoreCase);

    public static void Start()
    {
        _loop ??= Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var s = await SampleAsync();
                    Latest = s;
                    Updated?.Invoke(s);
                }
                catch { /* keep polling */ }
                await Task.Delay(2000);
            }
        });
    }

    private static async Task<StatsSnapshot> SampleAsync()
    {
        var gpuTask = SystemProbe.QueryGpuAsync();
        var cpu = CpuPercent();
        var (used, total) = Memory();
        var vram = ReadVramByPid();

        // Ollama's own processes (CPU use rises when a model doesn't fit on the GPU).
        double oCpu = 0, oRam = 0, oVram = 0;
        var procs = new List<ProcUsage>();
        var now = DateTime.UtcNow;
        var all = Process.GetProcesses();
        var seen = new HashSet<int>();
        foreach (var p in all)
        {
            try
            {
                bool ollama = IsOllama(p.ProcessName);
                bool wanted = Detailed || ollama || vram.ContainsKey(p.Id);
                if (!wanted) continue;
                seen.Add(p.Id);
                double cpuPct = 0;
                try
                {
                    var t = p.TotalProcessorTime;
                    if (PrevProc.TryGetValue(p.Id, out var prev))
                    {
                        var wall = (now - prev.At).TotalMilliseconds;
                        if (wall > 0) cpuPct = 100 * (t - prev.Cpu).TotalMilliseconds / wall / Environment.ProcessorCount;
                    }
                    PrevProc[p.Id] = (t, now);
                }
                catch { /* access denied for system processes */ }
                var ram = p.WorkingSet64 / 1073741824.0;
                var v = vram.TryGetValue(p.Id, out var vb) ? vb / 1073741824.0 : 0;
                var name = FriendlyName(p);
                procs.Add(new ProcUsage(p.Id, name, Math.Max(0, cpuPct), ram, v));
                if (ollama) { oCpu += cpuPct; oRam += ram; oVram += v; }
            }
            catch { }
            finally { p.Dispose(); }
        }
        foreach (var gone in PrevProc.Keys.Where(k => !seen.Contains(k)).ToList()) PrevProc.Remove(gone);

        var gpu = await gpuTask;
        return new StatsSnapshot(DateTime.Now, gpu,
            cpu, CpuNameValue, PhysicalCores, Environment.ProcessorCount, CpuMhzValue,
            used, total, Math.Max(0, oCpu), oRam, oVram,
            procs.Where(p => p.VramGb >= 0.05).OrderByDescending(p => p.VramGb).Take(8).ToList(),
            Detailed ? procs.OrderByDescending(p => p.CpuPercent).Take(8).ToList() : [],
            Detailed ? procs.OrderByDescending(p => p.RamGb).Take(8).ToList() : [],
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private static string FriendlyName(Process p)
    {
        if (ProcNames.TryGetValue(p.Id, out var n)) return n;
        n = p.ProcessName;
        try
        {
            var desc = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc) && desc.Length < 48) n = desc.Trim();
        }
        catch { /* other user / protected */ }
        if (p.ProcessName.Equals("llama-server", StringComparison.OrdinalIgnoreCase)) n = "Ollama model runner (llama-server)";
        return ProcNames[p.Id] = n;
    }

    // ------------------------------------------------------------------ GPU memory per process

    /// <summary>Dedicated GPU memory per PID from the Windows "GPU Process Memory" performance counters.</summary>
    private static Dictionary<int, long> ReadVramByPid()
    {
        var result = new Dictionary<int, long>();
        try
        {
            var cat = new PerformanceCounterCategory("GPU Process Memory");
            var data = cat.ReadCategory();
            if (!data.Contains("Dedicated Usage")) return result;
            foreach (InstanceData inst in data["Dedicated Usage"]!.Values)
            {
                // instance: pid_1234_luid_0x..._phys_0
                var parts = inst.InstanceName.Split('_');
                if (parts.Length < 2 || parts[0] != "pid" || !int.TryParse(parts[1], out var pid)) continue;
                result[pid] = result.GetValueOrDefault(pid) + inst.RawValue;
            }
        }
        catch { /* counters unavailable */ }
        return result;
    }

    // ------------------------------------------------------------------ CPU / RAM

    private static double CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        long i = idle.Value, k = kernel.Value, u = user.Value;
        long di = i - _prevIdle, dk = k - _prevKernel, du = u - _prevUser;
        bool first = _prevKernel == 0;
        _prevIdle = i; _prevKernel = k; _prevUser = u;
        if (first || dk + du == 0) return 0;
        return Math.Clamp(100.0 * (dk + du - di) / (dk + du), 0, 100);   // kernel time includes idle
    }

    private static (double Used, double Total) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0);
        double total = m.ullTotalPhys / 1073741824.0, avail = m.ullAvailPhys / 1073741824.0;
        return (total - avail, total);
    }

    private static string ReadCpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch { return "CPU"; }
    }

    private static int? ReadCpuMhz()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return k?.GetValue("~MHz") as int?;
        }
        catch { return null; }
    }

    private static int CountCores()
    {
        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(0 /* RelationProcessorCore */, IntPtr.Zero, ref len);
            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return Environment.ProcessorCount;
                int count = 0, offset = 0;
                while (offset < len)
                {
                    int size = Marshal.ReadInt32(buf, offset + 4);   // { Relationship:int, Size:int, ... }
                    if (size <= 0) break;
                    count++;
                    offset += size;
                }
                return count > 0 ? count : Environment.ProcessorCount;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return Environment.ProcessorCount; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME64 { public uint Low, High; public readonly long Value => ((long)High << 32) | Low; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME64 idle, out FILETIME64 kernel, out FILETIME64 user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    /// <summary>Disk free space for the drive holding <paramref name="path"/>.</summary>
    public static (double FreeGb, double TotalGb, string Drive)? Disk(string path)
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            return (d.AvailableFreeSpace / 1073741824.0, d.TotalSize / 1073741824.0, d.Name.TrimEnd('\\'));
        }
        catch { return null; }
    }
}
