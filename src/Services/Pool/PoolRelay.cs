using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Maison.Models;

namespace Maison.Services.Pool;

/// <summary>
/// On the PC using the AI pool: a local endpoint (127.0.0.1 only) that Claude Code, Qwen Code and maison itself talk
/// to as if it were Ollama. Each request goes to an AI PC in the pool:
///  · a session sticks to one AI PC while that PC is free (its model stays loaded, so replies start fast);
///  · if that PC is busy with someone else's work, or offline, the request goes to a free PC that has the model;
///  · if every PC is busy, it waits its turn on the least busy one.
/// Sessions get their own address (/s/&lt;id&gt;) so the relay knows which requests belong together.
/// </summary>
public static class PoolRelay
{
    private static WebApplication? _app;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Local = new() { BaseAddress = new Uri(OllamaClient.LocalUrl), Timeout = Timeout.InfiniteTimeSpan };
    private static readonly ConcurrentDictionary<string, Route> Sessions = new();

    public sealed class Route
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public required string Title { get; init; }
        public string? MemberId { get; set; }
        public string? MemberName { get; set; }
        public DateTime LastUsed { get; set; }
        public int Active;
        public int Moves { get; set; }
    }

    public static bool IsRunning => _app is not null;
    public static string Url => $"http://127.0.0.1:{PoolPorts.Relay}";
    public static string SessionUrl(string id) => $"{Url}/s/{id}";
    public static IReadOnlyCollection<Route> Routes => Sessions.Values.ToList();

    /// <summary>A new session gets its own address on the relay.</summary>
    public static string Register(string kind, string title)
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        Sessions[id] = new Route { Id = id, Kind = kind, Title = title, LastUsed = DateTime.Now };
        return id;
    }

    public static async Task StartAsync()
    {
        if (_app is not null) return;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 256L * 1024 * 1024;
            k.Listen(IPAddress.Loopback, PoolPorts.Relay);
        });
        var app = builder.Build();

        app.MapGet("/api/version", () =>
        {
            var v = Online().Select(m => m.Info!.OllamaVersion).FirstOrDefault(x => x is not null);
            return v is null ? Results.Json(new { error = "No AI PC in your pool is reachable." }, statusCode: 503) : Results.Json(new { version = v });
        });
        app.MapGet("/api/tags", () => Results.Json(new
        {
            models = Online().SelectMany(m => m.Info!.Models).GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { name = g.Key, model = g.Key, size = (long)(g.First().SizeGb * 1073741824.0), modified_at = DateTime.UtcNow })
                .ToList(),
        }));
        app.MapGet("/api/ps", () => Results.Json(new
        {
            models = Online().SelectMany(m => m.Info!.Loaded).Select(l => new
            {
                name = l.Name, model = l.Name, size = (long)(l.SizeGb * 1073741824.0), size_vram = (long)(l.SizeGb * 1073741824.0), context_length = l.Context,
            }).ToList(),
        }));
        app.MapPost("/api/create", CreateEverywhereAsync);
        app.MapPost("/api/pull", () => Results.Json(new { error = "In the AI pool, install models on a particular PC from the Pool page." }, statusCode: 400));
        app.MapDelete("/api/delete", () => Results.Json(new { error = "In the AI pool, remove models on a particular PC from the Pool page." }, statusCode: 400));
        app.MapFallback("{**path}", ctx =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            var m = Regex.Match(path, "^/s/([0-9a-f]{10})(/.*)?$");
            return m.Success ? RouteAsync(ctx, m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : "/") : RouteAsync(ctx, null, path);
        });

        await app.StartAsync();
        _app = app;
        AiPool.StartPolling();
        await AiPool.RefreshAsync();
    }

    public static async Task StopAsync()
    {
        if (_app is not { } a) return;
        _app = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await a.StopAsync(cts.Token); } catch { }
        await a.DisposeAsync();
    }

    private static IEnumerable<MemberState> Online() =>
        AiPool.Members.Where(m => m.Member.Enabled && m.Online && m.Info is { OllamaRunning: true });

    // ------------------------------------------------------------------ routing

    private static readonly Regex Variant = new(@"^(?<base>.+)-ctx(?<k>\d+)k$", RegexOptions.Compiled);

    private static bool Serves(MemberState m, string model) =>
        model.Length == 0 || m.Has(model) || (Variant.Match(model) is { Success: true } v && m.Has(v.Groups["base"].Value));

    /// <summary>The order to try AI PCs in for one request.</summary>
    private static List<MemberState> Choose(Route? route, string model)
    {
        var candidates = Online().Where(m => Serves(m, model)).ToList();
        var order = new List<MemberState>();
        if (route?.MemberId is { } sticky && candidates.FirstOrDefault(m => m.Member.Id == sticky) is { } s && !s.BusyForUs)
            order.Add(s);
        order.AddRange(candidates.Where(m => !order.Contains(m) && !m.BusyForUs)
            .OrderByDescending(m => m.HasLoaded(model))
            .ThenBy(m => m.OurActive)
            .ThenByDescending(m => m.Info?.Gpu?.TotalGb ?? 0));
        // Everyone busy: queue on the PC with the least going on (the session's own PC first).
        order.AddRange(candidates.Where(m => !order.Contains(m))
            .OrderByDescending(m => m.Member.Id == route?.MemberId)
            .ThenBy(m => m.Info?.Active ?? 0));
        return order;
    }

    private static readonly HashSet<string> SkipRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Authorization", "x-api-key", "Content-Length", "Content-Type", "Connection", "Transfer-Encoding", "Expect" };
    private static readonly HashSet<string> SkipResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Transfer-Encoding", "Connection", "Keep-Alive" };

    private static async Task RouteAsync(HttpContext ctx, string? sessionId, string path)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        var body = ms.ToArray();
        var model = PoolHost.ModelOf(body);
        Route? route = sessionId is not null && Sessions.TryGetValue(sessionId, out var r) ? r : null;

        var order = Choose(route, model);
        if (order.Count == 0)
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = model.Length > 0
                    ? $"No AI PC in your pool that's online has {model}. Install it on one from the Pool page."
                    : "No AI PC in your pool is online.",
            });
            return;
        }

        foreach (var member in order)
        {
            HttpResponseMessage? resp = null;
            Interlocked.Increment(ref member.OurActive);
            if (route is not null) Interlocked.Increment(ref route.Active);
            try
            {
                if (model.Length > 0 && !member.Has(model) && Variant.Match(model) is { Success: true } v)
                    await CreateVariantOnAsync(member, model, v.Groups["base"].Value, int.Parse(v.Groups["k"].Value) * 1024);

                using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), path + ctx.Request.QueryString);
                if (body.Length > 0)
                {
                    req.Content = new ByteArrayContent(body);
                    if (ctx.Request.ContentType is { } ct) req.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
                }
                foreach (var (k, val) in ctx.Request.Headers)
                    if (!SkipRequestHeaders.Contains(k)) req.Headers.TryAddWithoutValidation(k, val.ToArray());
                if (route is not null)
                {
                    req.Headers.TryAddWithoutValidation("X-Maison-Agent", route.Kind);
                    req.Headers.TryAddWithoutValidation("X-Maison-Session", route.Id);
                }

                try
                {
                    resp = await (member.IsThisPc ? Local : AiPool.For(member.Member))
                        .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                }
                catch (HttpRequestException)
                {
                    member.Online = false;   // gone: try the next PC with the same request
                    continue;
                }
                if (resp.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.Unauthorized && member != order[^1])
                {
                    resp.Dispose();
                    continue;
                }

                if (route is not null)
                {
                    if (route.MemberId is not null && route.MemberId != member.Member.Id) route.Moves++;
                    route.MemberId = member.Member.Id;
                    route.MemberName = member.Member.Name;
                    route.LastUsed = DateTime.Now;
                }
                ctx.Response.StatusCode = (int)resp.StatusCode;
                foreach (var (k, val) in resp.Headers.Concat(resp.Content.Headers))
                    if (!SkipResponseHeaders.Contains(k)) ctx.Response.Headers[k] = val.ToArray();
                await using var s = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted);
                var buf = new byte[16384];
                int n;
                while ((n = await s.ReadAsync(buf, ctx.RequestAborted)) > 0)
                {
                    await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
                return;
            }
            catch (OperationCanceledException) { return; }
            finally
            {
                resp?.Dispose();
                Interlocked.Decrement(ref member.OurActive);
                if (route is not null) { Interlocked.Decrement(ref route.Active); route.LastUsed = DateTime.Now; }
            }
        }
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new { error = "Every AI PC in your pool that has this model failed to answer." });
        }
    }

    // ------------------------------------------------------------------ context-size variants

    /// <summary>maison's context-size variants (model-ctx64k) are created on every AI PC that has the base model.</summary>
    private static async Task<IResult> CreateEverywhereAsync(HttpContext ctx)
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var name = doc.RootElement.TryGetProperty("model", out var n) ? n.GetString() ?? "" : "";
        var from = doc.RootElement.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
        int ctxSize = doc.RootElement.TryGetProperty("parameters", out var p) && p.TryGetProperty("num_ctx", out var c) ? c.GetInt32() : 0;
        if (name.Length == 0 || from.Length == 0 || ctxSize == 0) return Results.Json(new { error = "maison's pool only creates context-size variants." }, statusCode: 400);
        var targets = Online().Where(m => m.Has(from)).ToList();
        if (targets.Count == 0) return Results.Json(new { error = $"No AI PC in your pool has {from}." }, statusCode: 404);
        var results = await Task.WhenAll(targets.Select(async m => { try { await CreateVariantOnAsync(m, name, from, ctxSize); return true; } catch { return false; } }));
        _ = AiPool.RefreshAsync();
        return results.Any(x => x) ? Results.Json(new { status = "success" }) : Results.Json(new { error = "Couldn't create it on any AI PC." }, statusCode: 502);
    }

    private static async Task CreateVariantOnAsync(MemberState m, string name, string from, int ctxSize)
    {
        if (m.Has(name)) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var http = m.IsThisPc ? Local : AiPool.For(m.Member);
        using var resp = await http.PostAsJsonAsync("/api/create", new { model = name, from, parameters = new { num_ctx = ctxSize }, stream = false }, cts.Token);
        resp.EnsureSuccessStatusCode();
        m.Info = m.Info! with { Models = [.. m.Info.Models, new PoolModel(name, m.Info.Models.FirstOrDefault(x => MemberState.Same(x.Name, from))?.SizeGb ?? 0)] };
    }
}
