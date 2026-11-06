using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Maison.Services;

namespace Maison.Views;

public partial class SystemView : UserControl
{
    public IShell Shell { get; set; } = null!;
    private string? _driver;
    private string? _ollamaVersion;

    private sealed record Row(string Name, string Value, string Tip);

    public SystemView()
    {
        InitializeComponent();
        StatsService.Updated += s => Dispatcher.BeginInvoke(() => { if (IsVisible) Show(s); });
    }

    public async void Refresh()
    {
        OsName.Text = OsDisplay();
        if (StatsService.Latest is { } s) Show(s);
        _driver ??= await DriverAsync();
        _ollamaVersion = await OllamaClient.GetVersionAsync(2);
        if (StatsService.Latest is { } s2) Show(s2);
    }

    private void Show(StatsSnapshot s)
    {
        CpuName.Text = s.CpuName;
        CpuSub.Text = $"{s.Cores} cores · {s.Threads} threads" + (s.CpuMhz is { } m ? $" · {m:N0} MHz base" : "");
        GCpu.Value = s.CpuPercent;
        GCpu.Display = $"{s.CpuPercent:0}%";

        RamName.Text = $"{s.RamTotalGb:0} GB";
        RamSub.Text = $"{s.RamUsedGb:0.0} GB in use · {s.RamTotalGb - s.RamUsedGb:0.0} GB free";
        GRam.Maximum = Math.Max(1, s.RamTotalGb);
        GRam.Warn = GRam.Maximum * 0.85;
        GRam.Danger = GRam.Maximum * 0.95;
        GRam.Value = s.RamUsedGb;
        GRam.Display = $"{s.RamUsedGb:0.0} GB";

        if (s.Gpu is { } g)
        {
            GpuName.Text = g.Name;
            GpuSub.Text = string.Join(" · ", new[]
            {
                $"{g.TotalGb:0} GB VRAM",
                g.TempC is { } t ? $"{t:0}°C" : null,
                g.PowerW is { } p ? $"{p:0} W of {g.PowerLimitW ?? 0:0} W" : null,
                g.ClockMhz is { } c ? $"{c:N0} MHz" : null,
                g.FanPercent is { } f ? $"fan {f}%" : null,
                _driver is null ? null : $"driver {_driver}",
            }.Where(x => x is not null));
            GVram.Maximum = Math.Max(1, g.TotalGb);
            GVram.Warn = GVram.Maximum * 0.9;
            GVram.Danger = GVram.Maximum * 0.97;
            GVram.Value = g.UsedGb;
            GVram.Display = $"{g.UsedGb:0.0} GB";
        }
        else
        {
            GpuName.Text = "No NVIDIA GPU detected";
            GpuSub.Text = "Models run on the CPU (slow). GPU stats need an NVIDIA card with nvidia-smi.";
        }

        OsSub.Text = $"{Environment.MachineName} · up {Up(s.Uptime)}";
        if (StatsService.Disk(ModelStorage.Current) is { } d)
        {
            DiskName.Text = $"{d.FreeGb:N0} GB free";
            DiskSub.Text = $"of {d.TotalGb:N0} GB on {d.Drive} · {ModelStorage.Current}";
        }

        OllamaName.Text = _ollamaVersion is null ? "Not running" : $"v{_ollamaVersion}";
        OllamaSub.Text = $"{s.OllamaCpuPercent:0}% CPU · {s.OllamaRamGb:0.0} GB RAM · {s.OllamaVramGb:0.0} GB VRAM";

        var warn = HealthAdvice.VramWarning(s);
        WarnBox.Visibility = warn is null ? Visibility.Collapsed : Visibility.Visible;
        WarnText.Text = warn ?? "";

        VramList.ItemsSource = s.VramUsers.Select(p => new Row(p.Name, $"{p.VramGb:0.0} GB", $"PID {p.Pid}")).ToList();
        CpuList.ItemsSource = s.TopCpu.Where(p => p.CpuPercent >= 0.1).Select(p => new Row(p.Name, $"{p.CpuPercent:0.0}%", $"PID {p.Pid} · {p.RamGb:0.00} GB RAM")).ToList();
        RamList.ItemsSource = s.TopRam.Select(p => new Row(p.Name, $"{p.RamGb:0.00} GB", $"PID {p.Pid} · {p.CpuPercent:0.0}% CPU")).ToList();
    }

    private static string Up(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";

    private static string OsDisplay()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName") as string ?? "Windows";
            var build = Environment.OSVersion.Version.Build;
            if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");   // ProductName still says 10 on 11
            var disp = k?.GetValue("DisplayVersion") as string;
            return $"{name}{(disp is null ? "" : " " + disp)} (build {build})";
        }
        catch { return RuntimeInformation.OSDescription; }
    }

    private static async Task<string?> DriverAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=driver_version --format=csv,noheader")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var v = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            return v.Length > 0 ? v.Split('\n')[0].Trim() : null;
        }
        catch { return null; }
    }
}
