using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Maison.Models;

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

public sealed class PortalDevice
{
    public string TokenHash { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime LastSeen { get; set; }
}

/// <summary>
/// Everything that goes into the Claude Code / Qwen Code / Chat start commands, editable on Settings → Start commands.
/// Defaults are what maison used before, plus telling each agent the real context window.
/// </summary>
public sealed class LaunchOptions
{
    // Shared
    /// <summary>Tell the agent how big Ollama's context window really is, so it compacts before it fills (instead of erroring).</summary>
    public bool TellContextWindow { get; set; } = true;
    /// <summary>Max tokens per reply. 0 = automatic: a quarter of the context window, 4K–32K.</summary>
    public int MaxOutputTokens { get; set; }

    // Claude Code
    /// <summary>Compact at this % of the window. 0 = Claude Code's default.</summary>
    public int ClaudeCompactPercent { get; set; }
    public bool ClaudeDisableThinking { get; set; }
    public bool ClaudeNoNonessentialTraffic { get; set; } = true;
    public bool ClaudeNoPromptCaching { get; set; } = true;
    public bool ClaudeContinue { get; set; }
    public string ClaudeExtraArgs { get; set; } = "";
    public string ClaudeExtraEnv { get; set; } = "";

    // Qwen Code
    /// <summary>Compact at this fraction of the window (Qwen Code's context.autoCompactThreshold, default 0.85).</summary>
    public double QwenCompactThreshold { get; set; } = 0.85;
    /// <summary>Per-request timeout. Local models on long prompts can take minutes (Qwen Code's default is 2 min).</summary>
    public int QwenTimeoutSeconds { get; set; } = 600;
    public bool QwenContinue { get; set; }
    public string QwenExtraArgs { get; set; } = "";
    public string QwenExtraEnv { get; set; } = "";

    // Chat (ollama run)
    public bool ChatVerbose { get; set; }
    /// <summary>"default", "on" or "off" (ollama run --think).</summary>
    public string ChatThink { get; set; } = "default";
    public string ChatExtraArgs { get; set; } = "";
}

/// <summary>An AI PC this PC has paired with (client side).</summary>
public sealed class PoolMember
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Last address it was seen at (refreshed by discovery).</summary>
    public string Address { get; set; } = "";
    public int Port { get; set; } = 11435;
    /// <summary>The key it gave us at pairing.</summary>
    public string Token { get; set; } = "";
    /// <summary>Its HTTPS certificate's SHA-256, pinned at pairing.</summary>
    public string Thumbprint { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>A PC paired with this AI PC (host side).</summary>
public sealed class PoolClient
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTime Added { get; set; }
    public DateTime LastSeen { get; set; }
}

public sealed class AppConfig
{
    public LaunchOptions Launch { get; set; } = new();
    public string MainModel { get; set; } = "";
    public string FastModel { get; set; } = "";
    public string Context { get; set; } = "64K";
    public string WorkDir { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string OpenIn { get; set; } = "This app";
    /// <summary>Legacy (≤1.3): replaced by <see cref="Permissions"/>.</summary>
    public bool SkipPermissions { get; set; }
    /// <summary>Permission mode for launched agents: ask | edits | plan | skip. Never Claude Code's "auto" (its safety check needs Anthropic's servers).</summary>
    public string Permissions { get; set; } = "";

    // Appearance
    public string Accent { get; set; } = "Purple";
    public string TerminalTheme { get; set; } = "maison";
    public int TerminalFontSize { get; set; } = 14;

    // Idle notifications
    public bool IdleNotify { get; set; } = true;
    public int IdleSeconds { get; set; } = 8;
    public bool IdleSound { get; set; } = true;

    // Model usage
    public int StaleDays { get; set; } = 14;
    public Dictionary<string, DateTime> ModelLastUsed { get; set; } = [];
    public DateTime UsageTrackingSince { get; set; } = DateTime.Now;

    /// <summary>Look for a newer maison on GitHub at start-up and once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    // AI pool
    /// <summary>Stable id for this PC in AI pools (so a PC is recognised when its address changes).</summary>
    public string MachineId { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Use the AI pool (paired AI PCs, and optionally this PC) instead of only this PC's Ollama.</summary>
    public bool UseRemoteAi { get; set; }
    /// <summary>Also use this PC's own Ollama as part of the pool.</summary>
    public bool PoolIncludeThisPc { get; set; } = true;
    /// <summary>AI PCs this PC has paired with.</summary>
    public List<PoolMember> PoolMembers { get; set; } = [];
    /// <summary>Legacy (unreleased builds): a single remote address.</summary>
    public string RemoteAiAddress { get; set; } = "";

    /// <summary>This PC is an AI PC: maison's pool service shares its Ollama with paired PCs at home.</summary>
    public bool ShareAi { get; set; }
    /// <summary>The code other PCs enter once to pair with this AI PC.</summary>
    public string PoolPairCode { get; set; } = "";
    /// <summary>PCs paired with this AI PC (only a hash of each key is kept).</summary>
    public List<PoolClient> PoolClients { get; set; } = [];

    // Web portal
    public bool PortalEnabled { get; set; }
    public int PortalPort { get; set; } = Random.Shared.Next(20000, 60000);
    public bool PortalAllowInternet { get; set; }
    public string PortalPasswordHash { get; set; } = "";
    public string PortalCertThumbprint { get; set; } = "";
    public List<PortalDevice> PortalDevices { get; set; } = [];

    // MAISON_CONFIG_DIR lets the self-test use a throwaway config instead of the user's.
    private static readonly string Dir = Environment.GetEnvironmentVariable("MAISON_CONFIG_DIR") is { Length: > 0 } d
        ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maison");
    private static readonly string FilePath = Path.Combine(Dir, "config.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object SaveLock = new();

    public static string Folder => Dir;

    /// <summary>The one shared config instance (the web portal and the UI both use it).</summary>
    public static AppConfig Current { get; } = Load();

    /// <summary>"simple" (one Start button, a tick and a terminal) or "advanced" (the full app).</summary>
    public string UiMode { get; set; } = "";

    private static AppConfig Load()
    {
        // Only maison's own settings count: a fresh install (no maison config yet) starts in the simple view.
        bool existed = File.Exists(FilePath);
        var c = LoadFile();
        if (c.Permissions.Length == 0) c.Permissions = c.SkipPermissions ? "skip" : "ask";
        // New users start in the simple view; people upgrading keep the full app they already know.
        if (c.UiMode is not ("simple" or "advanced")) c.UiMode = existed ? "advanced" : "simple";
        return c;
    }

    private static AppConfig LoadFile()
    {
        try
        {
            // One-time migration from the app's previous name.
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QwenLauncher", "config.json");
            if (!File.Exists(FilePath) && File.Exists(legacy))
            {
                Directory.CreateDirectory(Dir);
                File.Copy(legacy, FilePath);
            }
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig();
        }
        catch { /* corrupt config: fall back to defaults */ }
        return new AppConfig();
    }

    public void Save()
    {
        lock (SaveLock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { /* non-fatal */ }
        }
    }
}
