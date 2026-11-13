using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Maison.Models;

namespace Maison.Services;

/// <summary>Thin client for the local Ollama REST API.</summary>
public static class OllamaClient
{
    public const string LocalUrl = "http://127.0.0.1:11434";
    public const int DefaultPort = 11434;

    /// <summary>Where Ollama requests go: this PC's Ollama, or the AI pool relay (which picks an AI PC per request).</summary>
    public static string BaseUrl { get; private set; } = LocalUrl;
    /// <summary>True when the AI pool is in use (models run on the pool's PCs; nothing to install or start here).</summary>
    public static bool IsRemote { get; private set; }
    public static string Where => IsRemote ? "your AI pool" : "this PC";

    private static HttpClient Http = NewClient(LocalUrl);
    /// <summary>Always this PC's own Ollama: the Models page lists, installs and removes models here, pool or not.</summary>
    private static readonly HttpClient LocalHttp = NewClient(LocalUrl);
    private static HttpClient Pick(bool local) => local ? LocalHttp : Http;
    private static HttpClient NewClient(string url) => new() { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };

    private static CancellationTokenSource After(double seconds) => new(TimeSpan.FromSeconds(seconds));

    /// <summary>Points health, models and launches at the AI pool relay, or back at this PC.</summary>
    public static void UsePool(bool pool)
    {
        var url = pool ? Pool.PoolRelay.Url : LocalUrl;
        IsRemote = pool;
        if (url == BaseUrl) return;
        BaseUrl = url;
        Http = NewClient(url);
    }
    /// <summary>This PC's own Ollama, whatever the pool setting.</summary>
    public static async Task<string?> GetLocalVersionAsync(double timeoutSeconds = 3)
    {
        try
        {
            using var http = NewClient(LocalUrl);
            using var cts = After(timeoutSeconds);
            var doc = await http.GetFromJsonAsync<JsonElement>("/api/version", cts.Token);
            return doc.GetProperty("version").GetString();
        }
        catch { return null; }
    }

    public static async Task<string?> GetVersionAsync(double timeoutSeconds = 3)
    {
        try
        {
            using var cts = After(timeoutSeconds);
            var doc = await Http.GetFromJsonAsync<JsonElement>("/api/version", cts.Token);
            return doc.GetProperty("version").GetString();
        }
        catch { return null; }
    }

    /// <param name="local">This PC's own Ollama, even when the AI pool is in use.</param>
    public static async Task<List<ModelInfo>> GetModelsAsync(bool local = false)
    {
        using var cts = After(5);
        var doc = await Pick(local).GetFromJsonAsync<JsonElement>("/api/tags", cts.Token);
        var list = new List<ModelInfo>();
        foreach (var m in doc.GetProperty("models").EnumerateArray())
        {
            var d = m.TryGetProperty("details", out var det) ? det : default;
            list.Add(new ModelInfo
            {
                Name = m.GetProperty("name").GetString()!,
                Size = m.GetProperty("size").GetInt64(),
                Modified = m.TryGetProperty("modified_at", out var mod) && mod.TryGetDateTime(out var dt) ? dt : default,
                ParameterSize = Str(d, "parameter_size"),
                Quantization = Str(d, "quantization_level"),
                ParentModel = Str(d, "parent_model"),
                Capabilities = m.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
                    ? caps.EnumerateArray().Select(c => c.GetString() ?? "").ToArray()
                    : [],
            });
        }
        return list.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static async Task<List<LoadedModel>> GetLoadedAsync()
    {
        using var cts = After(5);
        var doc = await Http.GetFromJsonAsync<JsonElement>("/api/ps", cts.Token);
        return doc.GetProperty("models").EnumerateArray()
            .Select(m => new LoadedModel(
                m.GetProperty("name").GetString()!,
                m.GetProperty("size").GetInt64(),
                m.TryGetProperty("size_vram", out var v) ? v.GetInt64() : 0,
                m.TryGetProperty("context_length", out var c) ? c.GetInt32() : 0))
            .ToList();
    }

    /// <summary>
    /// The context window Ollama will actually use for <paramref name="model"/> when no size is forced:
    /// the loaded model's own size if it's running, else the server's VRAM-based default from its log, else 4096.
    /// On another PC there's no log to read, so the model is loaded first and Ollama asked directly.
    /// </summary>
    public static async Task<int> EffectiveContextAsync(string model)
    {
        if (await LoadedContextAsync(model) is { } c) return c;
        if (IsRemote)
        {
            try
            {
                using var cts = After(300);   // loading a big model over there can take a while
                using var resp = await Http.PostAsJsonAsync("/api/generate", new { model, prompt = "", keep_alive = "10m" }, cts.Token);
            }
            catch { /* fall through to the default */ }
            if (await LoadedContextAsync(model) is { } c2) return c2;
            return 32768;
        }
        return ServerDefaultContext();
    }

    private static async Task<int?> LoadedContextAsync(string model)
    {
        try
        {
            var loaded = await GetLoadedAsync();
            if (loaded.FirstOrDefault(m => m.Name == model || m.Name == model + ":latest") is { Context: > 0 } m) return m.Context;
        }
        catch { /* server busy */ }
        return null;
    }

    /// <summary>Ollama's default num_ctx (it logs "default_num_ctx=32768" at start, sized to the GPU).</summary>
    public static int ServerDefaultContext()
    {
        foreach (var t in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            if (int.TryParse(Environment.GetEnvironmentVariable("OLLAMA_CONTEXT_LENGTH", t), out var env) && env > 0) return env;
        try
        {
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ollama", "server.log");
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            int found = 0;
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                var i = line.IndexOf("default_num_ctx=", StringComparison.Ordinal);
                if (i < 0) continue;
                var digits = new string(line.Skip(i + 16).TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var n) && n > 0) found = n;   // keep the newest
            }
            if (found > 0) return found;
        }
        catch { /* no log yet */ }
        return 4096;
    }

    /// <summary>Creates a settings-only model variant (no weight copy) with a fixed context window.</summary>
    public static async Task CreateVariantAsync(string name, string from, int numCtx)
    {
        using var cts = After(120);
        var body = new { model = name, from, parameters = new { num_ctx = numCtx }, stream = false };
        using var resp = await Http.PostAsJsonAsync("/api/create", body, cts.Token);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Downloads a model, reporting combined progress across all layers. Cancelling stops the download;
    /// Ollama keeps the partial layers and resumes next time.
    /// </summary>
    public static async Task PullAsync(string model, IProgress<PullProgress> progress, CancellationToken ct, bool local = false)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/pull") { Content = JsonContent.Create(new { model, stream = true }) };
        using var resp = await Pick(local).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(await ErrorText(resp));

        var layers = new Dictionary<string, (long Done, long Total)>();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var err)) throw new InvalidOperationException(err.GetString());
            var status = r.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            if (r.TryGetProperty("digest", out var dg) && r.TryGetProperty("total", out var tot))
            {
                long done = r.TryGetProperty("completed", out var c) ? c.GetInt64() : 0;
                var key = dg.GetString() ?? "";
                var prev = layers.GetValueOrDefault(key);
                layers[key] = (Math.Max(prev.Done, done), tot.GetInt64());
            }
            progress.Report(new PullProgress(status, layers.Values.Sum(v => v.Done), layers.Values.Sum(v => v.Total)));
            if (status == "success") return;
        }
    }

    private static async Task<string> ErrorText(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        try { return JsonDocument.Parse(text).RootElement.GetProperty("error").GetString() ?? text; }
        catch { return $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {text}"; }
    }

    public static async Task<SpeedResult> SpeedTestAsync(string model)
    {
        using var cts = After(900);
        var sw = Stopwatch.StartNew();
        var body = new { model, prompt = "Explain how a hash map works in about 120 words.", stream = false, think = false };
        using var resp = await Http.PostAsJsonAsync("/api/generate", body, cts.Token);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await ErrorText(resp));
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cts.Token);
        int tokens = doc.GetProperty("eval_count").GetInt32();
        double evalSec = doc.GetProperty("eval_duration").GetInt64() / 1e9;
        double loadSec = doc.TryGetProperty("load_duration", out var l) ? l.GetInt64() / 1e9 : 0;
        return new SpeedResult(model, tokens, evalSec > 0 ? Math.Round(tokens / evalSec, 1) : 0, loadSec, sw.Elapsed.TotalSeconds);
    }

    public static async Task UnloadAsync(string model)
    {
        using var cts = After(30);
        using var resp = await Http.PostAsJsonAsync("/api/generate", new { model, keep_alive = 0 }, cts.Token);
        resp.EnsureSuccessStatusCode();
    }

    public static async Task DeleteAsync(string model, bool local = false)
    {
        using var cts = After(30);
        using var req = new HttpRequestMessage(HttpMethod.Delete, "/api/delete") { Content = JsonContent.Create(new { model }) };
        using var resp = await Pick(local).SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await ErrorText(resp));
    }

    /// <summary>Where Ollama stores model files (OLLAMA_MODELS or ~/.ollama/models).</summary>
    public static string ModelsDir
    {
        get
        {
            foreach (var t in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
                if (Environment.GetEnvironmentVariable("OLLAMA_MODELS", t) is { Length: > 0 } v) return v;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
        }
    }
}
