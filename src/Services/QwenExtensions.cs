using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Maison.Services;

public sealed record MarketplaceInfo(string Repo, string Title, string Description, string Format);
public sealed record MarketPlugin(string Name, string Description, string Category, string Marketplace);

/// <summary>
/// Qwen Code extensions. Qwen Code installs its own extensions, Gemini CLI extensions (from a GitHub repo) and
/// Claude Code marketplace plugins (converted on install), so Claude-format marketplaces work as sources.
/// </summary>
public static class QwenExtensions
{
    public static readonly MarketplaceInfo[] Defaults =
    [
        new("anthropics/claude-plugins-official", "Claude plugins (official)", "Anthropic's official plugin directory: dev tools, integrations, workflows.", "claude"),
        new("anthropics/skills", "Agent Skills", "Anthropic's skills: documents (Word, Excel, PowerPoint, PDF), examples and more.", "claude"),
        new("anthropics/claude-code", "Claude Code demo plugins", "Example plugins from the Claude Code repo: code review, commits, feature dev, SDK helpers.", "claude"),
    ];

    public const string GeminiGallery = "https://geminicli.com/extensions/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static QwenExtensions() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("maison-launcher");

    /// <summary>Runs a qwen subcommand hidden and returns its combined output.</summary>
    public static async Task<(int Code, string Output)> RunAsync(params string[] args)
    {
        var qwen = SystemProbe.FindOnPath("qwen") ?? throw new InvalidOperationException("Qwen Code isn't installed. Install it on the Setup page.");
        var sb = new StringBuilder();
        var all = new List<string> { "/d", "/c", qwen };
        all.AddRange(args);
        int code = await Installer.RunAsync("cmd.exe", all, line => { lock (sb) sb.AppendLine(line); }, CancellationToken.None);
        return (code, sb.ToString().Trim());
    }

    public static Task<(int, string)> ListInstalledAsync() => RunAsync("extensions", "list");
    public static Task<(int, string)> ListSourcesAsync() => RunAsync("extensions", "sources", "list");
    public static Task<(int, string)> AddSourceAsync(string source) => RunAsync("extensions", "sources", "add", source);

    /// <summary>Reads a Claude-format marketplace (.claude-plugin/marketplace.json) from GitHub.</summary>
    public static async Task<List<MarketPlugin>> BrowseAsync(string repo)
    {
        var url = $"https://raw.githubusercontent.com/{repo}/HEAD/.claude-plugin/marketplace.json";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var list = new List<MarketPlugin>();
        if (!doc.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in plugins.EnumerateArray())
        {
            string S(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var name = S("name");
            if (name.Length == 0) continue;
            list.Add(new MarketPlugin(name, S("description"), S("category"), repo));
        }
        return list.OrderBy(p => p.Category).ThenBy(p => p.Name).ToList();
    }

    /// <summary>The install source string Qwen Code understands for a marketplace plugin.</summary>
    public static string InstallSource(MarketPlugin p) => $"{p.Marketplace}:{p.Name}";
}
