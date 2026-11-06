using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using QwenLauncher.Models;

namespace QwenLauncher.Services;

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
        return doc.GetProperty("models").EnumerateArray()
            .Select(m => new ModelInfo(m.GetProperty("name").GetString()!, m.GetProperty("size").GetInt64()))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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

    public static async Task<SpeedResult> SpeedTestAsync(string model)
    {
        using var cts = After(900);
        var sw = Stopwatch.StartNew();
        var body = new { model, prompt = "Explain how a hash map works in about 120 words.", stream = false, think = false };
        using var resp = await Http.PostAsJsonAsync("/api/generate", body, cts.Token);
        resp.EnsureSuccessStatusCode();
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
        resp.EnsureSuccessStatusCode();
    }
}
