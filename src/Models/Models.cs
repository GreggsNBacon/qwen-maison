using System.IO;
using System.Text.Json;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace QwenLauncher.Models;

public sealed record ModelInfo(string Name, long Size)
{
    public double SizeGb => Math.Round(Size / 1073741824.0, 1);
}

public sealed record LoadedModel(string Name, long Size, long SizeVram, int Context)
{
    public double SizeGb => Math.Round(Size / 1073741824.0, 1);
    public int GpuPercent => Size > 0 ? (int)Math.Round(100.0 * SizeVram / Size) : 0;
}

public sealed record GpuInfo(string Name, double UsedGb, double TotalGb, int LoadPercent);

public sealed record SpeedResult(string Model, int Tokens, double TokensPerSecond, double LoadSeconds, double TotalSeconds);

public enum Health { Ok, Warn, Bad, Info }

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
        Health.Ok => Frozen(0x4C, 0xC3, 0x8A),
        Health.Warn => Frozen(0xE5, 0xB4, 0x54),
        Health.Bad => Frozen(0xE5, 0x53, 0x4B),
        _ => Frozen(0x6C, 0xA0, 0xDC),
    };

    public bool HasDetail => Detail.Length > 0;

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}

public sealed class AppConfig
{
    public string MainModel { get; set; } = "qwen3.8:27b";
    public string FastModel { get; set; } = "";
    public string Context { get; set; } = "64K";
    public string WorkDir { get; set; } = @"B:\Claude";
    public bool UseTerminal { get; set; } = true;
    public bool SkipPermissions { get; set; }

    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QwenLauncher");
    private static readonly string FilePath = Path.Combine(Dir, "config.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

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
