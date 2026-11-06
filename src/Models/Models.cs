using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace QwenLauncher.Models;

public sealed class ModelInfo
{
    public required string Name { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string ParameterSize { get; init; } = "";
    public string Quantization { get; init; } = "";
    public string ParentModel { get; init; } = "";
    public string[] Capabilities { get; init; } = [];
    public double SizeGb => Math.Round(Size / 1073741824.0, 1);
}

public sealed record LoadedModel(string Name, long Size, long SizeVram, int Context)
{
    public double SizeGb => Math.Round(Size / 1073741824.0, 1);
    public int GpuPercent => Size > 0 ? (int)Math.Round(100.0 * SizeVram / Size) : 0;
}

public sealed record GpuInfo(string Name, double UsedGb, double TotalGb, int LoadPercent,
    double? TempC, double? PowerW, double? PowerLimitW, int? FanPercent, int? ClockMhz);

public sealed record SpeedResult(string Model, int Tokens, double TokensPerSecond, double LoadSeconds, double TotalSeconds);

public sealed record PullProgress(string Status, long Completed, long Total);

public enum Health { Ok, Warn, Bad, Info }

internal static class Palette
{
    public static readonly SolidColorBrush Green = Frozen(0x4C, 0xC3, 0x8A);
    public static readonly SolidColorBrush Amber = Frozen(0xE5, 0xB4, 0x54);
    public static readonly SolidColorBrush Red = Frozen(0xE5, 0x53, 0x4B);
    public static readonly SolidColorBrush Blue = Frozen(0x6C, 0xA0, 0xDC);
    public static readonly SolidColorBrush Grey = Frozen(0x8A, 0x8A, 0x8A);

    public static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}

public sealed class StatusItem
{
    public required Health Level { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";

    public SymbolRegular Symbol => Level switch
    {
        Health.Ok => SymbolRegular.CheckmarkCircle24,
        Health.Warn => SymbolRegular.Warning24,
        Health.Bad => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.Info24,
    };

    public Brush Brush => Level switch
    {
        Health.Ok => Palette.Green,
        Health.Warn => Palette.Amber,
        Health.Bad => Palette.Red,
        _ => Palette.Blue,
    };

    public bool HasDetail => Detail.Length > 0;
}

/// <summary>Base for small bindable view items.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A model download in progress (Models page).</summary>
public sealed class DownloadItem : Observable
{
    private double _percent;
    private string _detail = "Starting…";
    private bool _indeterminate = true;

    public required string Model { get; init; }
    public CancellationTokenSource Cancel { get; } = new();
    public double Percent { get => _percent; set => Set(ref _percent, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public bool Indeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }
}

public sealed class AppConfig
{
    public string MainModel { get; set; } = "";
    public string FastModel { get; set; } = "";
    public string Context { get; set; } = "64K";
    public string WorkDir { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string OpenIn { get; set; } = "This app";
    public bool SkipPermissions { get; set; }

    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QwenLauncher");
    private static readonly string FilePath = Path.Combine(Dir, "config.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder => Dir;

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig();
        }
        catch { /* corrupt config: fall back to defaults */ }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* non-fatal */ }
    }
}
