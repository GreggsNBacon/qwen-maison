using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Maison.Models;

namespace Maison.Services;

/// <summary>Local machine facts: Ollama install files, GPU, CLIs on PATH; plus start/stop/repair of Ollama.</summary>
public static class SystemProbe
{
    public static readonly string OllamaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama");
    public static string OllamaExe => Path.Combine(OllamaDir, "ollama.exe");
    public static string OllamaApp => Path.Combine(OllamaDir, "ollama app.exe");
    private static string LibDir => Path.Combine(OllamaDir, "lib", "ollama");

    public static bool OllamaInstalled => File.Exists(OllamaExe);
    public static bool RuntimeOk => File.Exists(Path.Combine(LibDir, "llama-server.exe"));

    public static string[] CudaDirs =>
        Directory.Exists(LibDir)
            ? Directory.GetDirectories(LibDir, "cuda*").Select(Path.GetFileName).OfType<string>().ToArray()
            : [];

    public static double RamGb => Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0, 1);

    /// <summary>
    /// PATH as it is *now* in the registry (machine + user), so tools installed while the app is open are found.
    /// </summary>
    public static string FreshPath =>
        string.Join(';', new[]
        {
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process),
        }.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Finds a command on PATH the way a shell would (exe/cmd/bat/ps1).</summary>
    public static string? FindOnPath(string name)
    {
        string[] exts = [".exe", ".cmd", ".bat", ".ps1"];
        var dirs = FreshPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs")])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
            foreach (var e in exts)
            {
                try
                {
                    var p = Path.Combine(Environment.ExpandEnvironmentVariables(d), name + e);
                    if (File.Exists(p)) return p;
                }
                catch { /* bad PATH entry */ }
            }
        return null;
    }

    public static bool HasWindowsTerminal => FindOnPath("wt") is not null;

    public static async Task<GpuInfo?> QueryGpuAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,memory.used,memory.total,utilization.gpu,temperature.gpu,power.draw,power.limit,fan.speed,clocks.sm --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var line = (await p.StandardOutput.ReadLineAsync())?.Split(',', StringSplitOptions.TrimEntries);
            await p.WaitForExitAsync();
            if (line is not { Length: >= 9 }) return null;
            return new GpuInfo(line[0],
                (Num(line[1]) ?? 0) / 1024, (Num(line[2]) ?? 0) / 1024, (int)(Num(line[3]) ?? 0),
                Num(line[4]), Num(line[5]), Num(line[6]), (int?)Num(line[7]), (int?)Num(line[8]));
        }
        catch { return null; }
    }

    private static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    // ------------------------------------------------------------ control

    public static async Task<bool> StartOllamaAsync()
    {
        if (await OllamaClient.GetVersionAsync(2) is not null) return true;
        if (File.Exists(OllamaApp))
            Process.Start(new ProcessStartInfo(OllamaApp) { UseShellExecute = true, WorkingDirectory = OllamaDir });
        else if (OllamaInstalled)
            Process.Start(new ProcessStartInfo(OllamaExe, "serve") { UseShellExecute = false, CreateNoWindow = true });
        else
            return false;

        for (int i = 0; i < 40; i++)
        {
            if (await OllamaClient.GetVersionAsync(2) is not null) return true;
            await Task.Delay(500);
        }
        return false;
    }

    public static async Task StopOllamaAsync()
    {
        foreach (var name in new[] { "ollama app", "ollama", "llama-server" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone / access denied */ }
                p.Dispose();
            }
        await Task.Delay(1500);
    }

    /// <summary>Extracts the bundled Repair-Ollama.ps1 and runs it in a visible PowerShell window.</summary>
    public static void RunRepair()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Maison");
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "Repair-Ollama.ps1");
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Repair-Ollama.ps1")!)
        using (var f = File.Create(script))
            s.CopyTo(f);

        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}
