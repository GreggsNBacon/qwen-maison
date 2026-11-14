using System.IO;
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

    // ------------------------------------------------------------------ what is installed

    /// <summary>One installed extension, as read from <c>qwen extensions list</c>.</summary>
    public sealed class ExtensionInfo
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Description { get; set; } = "";
        public string Source { get; set; } = "";
        public string Type { get; set; } = "";
        public string ReleaseTag { get; set; } = "";
        public List<string> Skills { get; } = [];
        public List<string> Commands { get; } = [];
        public List<string> Mcp { get; } = [];
        public List<string> Agents { get; } = [];
        /// <summary>null = couldn't tell; "" = up to date; otherwise the newer version.</summary>
        public string? NewerVersion { get; set; }
    }

    private static readonly System.Text.RegularExpressions.Regex Ansi = new(@"\x1B\[[0-9;?]*[A-Za-z]");

    private static readonly string[] ListKeys =
        ["Description", "Path", "Source", "Origin", "Release tag", "Enabled (User)", "Enabled (Workspace)", "Context files", "Commands", "Skills", "MCP servers", "Agents"];

    /// <summary>
    /// Parses the text <c>qwen extensions list</c> prints: a "✓ name (version)" line, then "Key: value" lines and lists
    /// under Skills / Commands / MCP servers / Agents. Indentation is ignored (the captured lines arrive trimmed), so
    /// only the known keys end a list.
    /// </summary>
    public static List<ExtensionInfo> ParseList(string output)
    {
        var list = new List<ExtensionInfo>();
        ExtensionInfo? cur = null;
        string? section = null;
        foreach (var raw in Ansi.Replace(output, "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var h = System.Text.RegularExpressions.Regex.Match(line, @"^\S\s+(\S+)\s+\(([^)]*)\)$");
            if (h.Success)
            {
                cur = new ExtensionInfo { Name = h.Groups[1].Value, Version = h.Groups[2].Value };
                list.Add(cur);
                section = null;
                continue;
            }
            if (cur is null) continue;
            var key = ListKeys.FirstOrDefault(k => line.StartsWith(k + ":", StringComparison.Ordinal));
            if (key is null)
            {
                switch (section)
                {
                    case "Skills": cur.Skills.Add(line); break;
                    case "Commands": cur.Commands.Add(line); break;
                    case "MCP servers": cur.Mcp.Add(line); break;
                    case "Agents": cur.Agents.Add(line); break;
                }
                continue;
            }
            var val = line[(key.Length + 1)..].Trim();
            section = val.Length == 0 ? key : null;
            switch (key)
            {
                case "Description": cur.Description = val; break;
                case "Release tag": cur.ReleaseTag = val; break;
                case "Enabled (User)": cur.Enabled = !val.Equals("false", StringComparison.OrdinalIgnoreCase); break;
                case "Source":
                    var m = System.Text.RegularExpressions.Regex.Match(val, @"^(.*?)\s*\(Type:\s*([^)]*)\)\s*$");
                    cur.Source = m.Success ? m.Groups[1].Value : val;
                    cur.Type = m.Success ? m.Groups[2].Value : "";
                    break;
            }
        }
        return list;
    }

    public static async Task<List<ExtensionInfo>> InstalledAsync()
    {
        var (_, text) = await ListInstalledAsync();
        return ParseList(text);
    }

    private static string? GitHubRepo(string source)
    {
        var m = System.Text.RegularExpressions.Regex.Match(source, @"github\.com/([\w.-]+/[\w.-]+?)(?:\.git)?/?$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool IsNewer(string latest, string installed)
    {
        static bool Parse(string s, out Version v) => Version.TryParse(s.Trim().TrimStart('v', 'V'), out v!);
        if (Parse(latest, out var a) && Parse(installed, out var b)) return a > b;
        return !string.Equals(latest.Trim().TrimStart('v'), installed.Trim().TrimStart('v'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Best-effort check for a newer version: the latest GitHub release for release-type extensions, or the version a
    /// Claude-format marketplace lists for the plugin. Leaves <see cref="ExtensionInfo.NewerVersion"/> null when it can't tell.
    /// </summary>
    public static async Task CheckUpdateAsync(ExtensionInfo e)
    {
        try
        {
            var repo = GitHubRepo(e.Source);
            if (repo is null) return;
            if (e.Type.Contains("release", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest"));
                var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                if (tag.Length == 0) return;
                e.NewerVersion = IsNewer(tag, e.ReleaseTag.Length > 0 ? e.ReleaseTag : e.Version) ? tag : "";
                return;
            }
            using var mk = JsonDocument.Parse(await Http.GetStringAsync($"https://raw.githubusercontent.com/{repo}/HEAD/.claude-plugin/marketplace.json"));
            if (!mk.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array) return;
            foreach (var p in plugins.EnumerateArray())
            {
                if (!p.TryGetProperty("name", out var n) || n.GetString() != e.Name) continue;
                if (p.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } ver)
                    e.NewerVersion = IsNewer(ver, e.Version) ? ver : "";
                return;
            }
        }
        catch { /* offline, rate limited or not a marketplace: leave unknown */ }
    }

    // ------------------------------------------------------------------ marketplace sources Qwen Code has saved

    private static string SourcesFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".qwen", "extensions", "marketplaces.json");

    /// <summary>The marketplaces already added to Qwen Code (it keeps them in ~/.qwen/extensions/marketplaces.json), so maison shows them again next time.</summary>
    public static List<MarketplaceInfo> SavedSources()
    {
        var result = new List<MarketplaceInfo>();
        try
        {
            if (!File.Exists(SourcesFile)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(SourcesFile));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var src = s.TryGetProperty("source", out var v) ? v.GetString() ?? "" : "";
                var repo = GitHubRepo(src.Contains("github.com") ? src : "github.com/" + src.Trim('/'));
                if (repo is null) continue;
                var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? repo : repo;
                result.Add(new MarketplaceInfo(repo, name == repo ? repo : $"{name} ({repo})", "Added to Qwen Code.", "claude"));
            }
        }
        catch { /* unreadable file: show only the defaults */ }
        return result;
    }
}
