using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Maison.Services;

/// <summary>A newer maison on GitHub: its version, release page and the exe to download.</summary>
public sealed record UpdateInfo(string Version, string PageUrl, string? DownloadUrl, string? Notes);

/// <summary>
/// Asks GitHub for maison's latest release and compares its version with this one. Only the public releases API is
/// contacted, with a generic user agent; nothing about this PC is sent.
/// </summary>
public static class UpdateCheck
{
    public const string Repo = "GreggsNBacon/qwen-maison";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";
    private static string ApiUrl => Environment.GetEnvironmentVariable("MAISON_UPDATE_URL") is { Length: > 0 } u
        ? u : $"https://api.github.com/repos/{Repo}/releases/latest";

    /// <summary>The newer release, or null if this is the latest. Throws when GitHub can't be reached.</summary>
    public static async Task<UpdateInfo?> CheckAsync(string? currentVersion = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("maison-update-check");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var r = await http.GetFromJsonAsync<JsonElement>(ApiUrl, ct);
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!IsNewer(tag, currentVersion ?? AppInfo.Version)) return null;
        string? exe = null;
        if (r.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("maison", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    exe = a.GetProperty("browser_download_url").GetString();
            }
        var page = r.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        var notes = r.TryGetProperty("body", out var b) ? b.GetString() : null;
        return new UpdateInfo(Clean(tag), page, exe, notes);
    }

    /// <summary>"v0.0.6" vs "0.0.5" → true. Anything unparsable is not newer.</summary>
    public static bool IsNewer(string tag, string current) =>
        Version.TryParse(Clean(tag), out var latest) && Version.TryParse(Clean(current), out var mine) && latest > mine;

    private static string Clean(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        var cut = v.IndexOfAny(['-', '+', ' ']);
        return cut >= 0 ? v[..cut] : v;
    }
}
