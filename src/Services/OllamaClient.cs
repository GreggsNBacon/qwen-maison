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

    /// <summary>Where Ollama is: this PC, or another PC on the network that shares its AI (Settings → AI on another PC).</summary>
    public static string BaseUrl { get; private set; } = LocalUrl;
    /// <summary>True when the models run on another PC (nothing to install or start here).</summary>
    public static bool IsRemote { get; private set; }
    /// <summary>"gpu-pc:11434" when remote, "this PC" when local.</summary>
    public static string Where => IsRemote ? new Uri(BaseUrl).Authority : "this PC";

    private static HttpClient Http = NewClient(LocalUrl);
    private static HttpClient NewClient(string url) => new() { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Points everything (health, models, launches) at this PC or at another PC's shared Ollama.</summary>
    public static void Use(bool remote, string address)
    {
        var url = remote && NormalizeAddress(address) is { } u ? u : LocalUrl;
        IsRemote = url != LocalUrl;
        if (url == BaseUrl) return;
        BaseUrl = url;
        Http = NewClient(url);
    }

    /// <summary>"gpu-pc", "192.168.1.20:11434" or "http://gpu-pc:11434/" → "http://gpu-pc:11434"; null if unusable.</summary>
    public static string? NormalizeAddress(string? address)
    {
        var a = (address ?? "").Trim().TrimEnd('/');
        if (a.Length == 0) return null;
        if (!a.Contains("://")) a = "http://" + a;
        if (!Uri.TryCreate(a, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0) return null;
        var port = u.IsDefaultPort && !a.Contains($":{u.Port}") ? DefaultPort : u.Port;
        return $"{u.Scheme}://{u.Host}:{port}";
    }

    /// <summary>Checks an address without switching to it: Ollama's version and model count, or why it failed.</summary>
    public static async Task<(bool Ok, string Message)> ProbeAsync(string address)
    {
        if (NormalizeAddress(address) is not { } url) return (false, "That doesn't look like an address. Use the PC's name or IP, e.g. gpu-pc or 192.168.1.20.");
        try
        {
            using var http = NewClient(url);
            using var cts = After(5);
            var v = await http.GetFromJsonAsync<JsonElement>("/api/version", cts.Token);
            var tags = await http.GetFromJsonAsync<JsonElement>("/api/tags", cts.Token);
            int n = tags.GetProperty("models").GetArrayLength();
            return (true, $"Connected: Ollama v{v.GetProperty("version").GetString()} with {n} model{(n == 1 ? "" : "s")} at {new Uri(url).Authority}.");
        }
        catch (TaskCanceledException) { return (false, $"No answer from {new Uri(url).Authority}. Is that PC on, with \"Share this PC's AI\" turned on in maison?"); }
        catch (HttpRequestException ex) { return (false, $"Can't connect to {new Uri(url).Authority}: {ex.Message} Check the name, that sharing is on there, and that both PCs are on the same network."); }
        catch (Exception ex) { return (false, $"{new Uri(url).Authority} answered, but not like Ollama: {ex.Message}"); }
    }

    private static CancellationTokenSource After(double seconds) => new(TimeSpan.FromSeconds(seconds));

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

    public static async Task<List<ModelInfo>> GetModelsAsync()
    {
        using var cts = After(5);
        var doc = await Http.GetFromJsonAsync<JsonElement>("/api/tags", cts.Token);
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
    public static async Task PullAsync(string model, IProgress<PullProgress> progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/pull") { Content = JsonContent.Create(new { model, stream = true }) };
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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

    public static async Task DeleteAsync(string model)
    {
        using var cts = After(30);
        using var req = new HttpRequestMessage(HttpMethod.Delete, "/api/delete") { Content = JsonContent.Create(new { model }) };
        using var resp = await Http.SendAsync(req, cts.Token);
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
