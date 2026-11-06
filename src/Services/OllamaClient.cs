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
    public const string BaseUrl = "http://127.0.0.1:11434";

    private static readonly HttpClient Http = new() { BaseAddress = new Uri(BaseUrl), Timeout = Timeout.InfiniteTimeSpan };

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
