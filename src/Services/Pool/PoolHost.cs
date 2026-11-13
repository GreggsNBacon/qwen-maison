using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Maison.Models;

namespace Maison.Services.Pool;

/// <summary>
/// Runs on an AI PC ("Share this PC's AI"): maison's pool service in front of this PC's Ollama.
/// Ollama itself stays on 127.0.0.1. Paired PCs reach it through this service over HTTPS with their own key; it
/// tracks which agents are using the PC, and lets paired PCs install models or set the PC up remotely.
/// Answers discovery broadcasts so other PCs find it without typing an address.
/// </summary>
public static class PoolHost
{
    private static WebApplication? _app;
    private static UdpClient? _udp;
    private static CancellationTokenSource? _stop;
    private static readonly HttpClient Ollama = new() { BaseAddress = new Uri(OllamaClient.LocalUrl), Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static int _active;
    private static readonly ConcurrentDictionary<string, AgentState> AgentsSeen = new();
    private static readonly object TaskGate = new();
    private static PoolTask? _task;
    private static readonly ConcurrentDictionary<string, (int Fails, DateTime Until)> PairFails = new();
    private static DateTime _lastSave;

    private sealed class AgentState
    {
        public string Client = "", Kind = "", Model = "";
        public DateTime LastSeen;
        public int Active;
    }

    public static bool IsRunning => _app is not null;
    public static string Name => Environment.MachineName;
    public static PoolHello Hello => new(AppConfig.Current.MachineId, Name, AppInfo.Version, PoolPorts.Service);

    public static string PairCode
    {
        get
        {
            var c = AppConfig.Current;
            if (c.PoolPairCode.Length != 6) { c.PoolPairCode = NewCode(); c.Save(); }
            return c.PoolPairCode;
        }
    }

    /// <summary>A fresh pairing code. PCs already paired keep working.</summary>
    public static string RenewPairCode()
    {
        AppConfig.Current.PoolPairCode = NewCode();
        AppConfig.Current.Save();
        return AppConfig.Current.PoolPairCode;
    }

    private static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static async Task StartAsync()
    {
        if (_app is not null) return;
        _ = PairCode;
        var cert = PortalCert.GetOrCreate();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 256L * 1024 * 1024;   // long agent conversations
            k.ListenAnyIP(PoolPorts.Service, l => l.UseHttps(cert));
        });
        var app = builder.Build();

        // Home network only (LAN, VPNs like Tailscale, this PC).
        app.Use(async (ctx, next) =>
        {
            if (!PortalNet.IsPrivate(ctx.Connection.RemoteIpAddress))
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsync("maison: the AI pool only accepts PCs on your home network.");
                return;
            }
            await next();
        });

        app.MapGet("/maison/hello", () => Results.Json(Hello, Json));
        app.MapPost("/maison/pair", (Func<HttpContext, Task<IResult>>)PairAsync);
        app.MapGet("/maison/info", async (HttpContext ctx) => Authed(ctx) is null ? Unauthorized() : Results.Json(await InfoAsync(), Json));
        app.MapPost("/maison/pull", async (HttpContext ctx) =>
        {
            if (Authed(ctx) is null) return Unauthorized();
            var r = await ctx.Request.ReadFromJsonAsync<ModelRequest>(Json);
            if (string.IsNullOrWhiteSpace(r?.Model)) return Results.BadRequest(new { error = "Which model?" });
            return StartTask($"Downloading {r.Model}", (log, progress) => PullAsync(r.Model.Trim(), log, progress));
        });
        app.MapPost("/maison/delete", async (HttpContext ctx) =>
        {
            if (Authed(ctx) is null) return Unauthorized();
            var r = await ctx.Request.ReadFromJsonAsync<ModelRequest>(Json);
            if (string.IsNullOrWhiteSpace(r?.Model)) return Results.BadRequest(new { error = "Which model?" });
            using var req = new HttpRequestMessage(HttpMethod.Delete, "/api/delete") { Content = JsonContent.Create(new { model = r.Model }) };
            using var resp = await Ollama.SendAsync(req);
            return resp.IsSuccessStatusCode ? Results.Ok() : Results.Json(new { error = await resp.Content.ReadAsStringAsync() }, statusCode: (int)resp.StatusCode);
        });
        app.MapPost("/maison/setup", (HttpContext ctx) =>
            Authed(ctx) is null ? Unauthorized() : StartTask("Setting up this PC", SetupAsync));
        app.MapPost("/maison/unpair", (HttpContext ctx) =>
        {
            if (Authed(ctx) is not { } c) return Unauthorized();
            AppConfig.Current.PoolClients.Remove(c);
            AppConfig.Current.Save();
            return Results.Ok();
        });
        app.MapFallback("{**path}", ProxyAsync);

        await app.StartAsync();
        _app = app;
        _stop = new CancellationTokenSource();
        StartDiscovery(_stop.Token);
    }

    public static async Task StopAsync()
    {
        _stop?.Cancel();
        try { _udp?.Dispose(); } catch { }
        _udp = null;
        if (_app is { } a)
        {
            _app = null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await a.StopAsync(cts.Token); } catch { }
            await a.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ discovery

    private static void StartDiscovery(CancellationToken ct)
    {
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, PoolPorts.Discovery));
            _udp = udp;
            _ = Task.Run(async () =>
            {
                var reply = JsonSerializer.SerializeToUtf8Bytes(Hello, Json);
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var r = await udp.ReceiveAsync(ct);
                        if (!PortalNet.IsPrivate(r.RemoteEndPoint.Address)) continue;
                        if (Encoding.ASCII.GetString(r.Buffer).Trim() == PoolPorts.DiscoverMessage)
                            await udp.SendAsync(reply, r.RemoteEndPoint, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch { /* malformed packet: ignore */ }
                }
            }, ct);
        }
        catch { /* port busy: PCs can still find this one by scanning */ }
    }

    // ------------------------------------------------------------------ pairing and keys

    private static async Task<IResult> PairAsync(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (PairFails.TryGetValue(ip, out var f) && f.Until > DateTime.Now)
            return Results.Json(new { error = "Too many wrong codes. Try again in a few minutes." }, statusCode: 429);
        var r = await ctx.Request.ReadFromJsonAsync<PairRequest>(Json);
        var ok = r is not null && r.Code.Length == 6 &&
                 CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(r.Code), Encoding.ASCII.GetBytes(PairCode));
        if (!ok)
        {
            var fails = (PairFails.TryGetValue(ip, out var p) ? p.Fails : 0) + 1;
            PairFails[ip] = (fails, fails >= 5 ? DateTime.Now.AddMinutes(10) : DateTime.MinValue);
            return Results.Json(new { error = "That code isn't right. It's shown on the AI PC in Settings → AI on another PC." }, statusCode: 403);
        }
        PairFails.TryRemove(ip, out _);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var cfg = AppConfig.Current;
        cfg.PoolClients.RemoveAll(c => c.Id.Length > 0 && c.Id == r!.ClientId);
        cfg.PoolClients.Add(new PoolClient
        {
            Id = r!.ClientId, Name = string.IsNullOrWhiteSpace(r.Client) ? ip : r.Client.Trim()[..Math.Min(64, r.Client.Trim().Length)],
            TokenHash = Hash(token), Added = DateTime.Now, LastSeen = DateTime.Now,
        });
        cfg.Save();
        return Results.Json(new PairReply(token, cfg.MachineId, Name), Json);
    }

    public static void Revoke(PoolClient c)
    {
        AppConfig.Current.PoolClients.Remove(c);
        AppConfig.Current.Save();
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static PoolClient? Authed(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : ctx.Request.Headers["x-api-key"].ToString();
        if (token.Length < 20) return null;
        var h = Hash(token);
        var c = AppConfig.Current.PoolClients.FirstOrDefault(x => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(x.TokenHash), Encoding.ASCII.GetBytes(h)));
        if (c is not null)
        {
            c.LastSeen = DateTime.Now;
            if ((DateTime.Now - _lastSave).TotalMinutes > 1) { _lastSave = DateTime.Now; AppConfig.Current.Save(); }
        }
        return c;
    }

    private static IResult Unauthorized() =>
        Results.Json(new { error = "maison: this PC isn't paired with this AI PC (or was removed). Add it again on the Pool page." }, statusCode: 401);

    // ------------------------------------------------------------------ the Ollama proxy

    private static readonly HashSet<string> SkipRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Authorization", "x-api-key", "Content-Length", "Content-Type", "Connection", "Transfer-Encoding", "Expect" };
    private static readonly HashSet<string> SkipResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Transfer-Encoding", "Connection", "Keep-Alive" };

    private static async Task ProxyAsync(HttpContext ctx)
    {
        var client = Authed(ctx);
        if (client is null)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "maison: this PC isn't paired with this AI PC. Add it on the Pool page." });
            return;
        }

        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        var body = ms.ToArray();
        var model = ModelOf(body);
        var agentHeader = ctx.Request.Headers["X-Maison-Agent"].ToString();
        var session = ctx.Request.Headers["X-Maison-Session"].ToString();
        bool isWork = ctx.Request.Method == "POST" && model.Length > 0 && !ctx.Request.Path.StartsWithSegments("/api/show") &&
                      !ctx.Request.Path.StartsWithSegments("/api/create");
        AgentState? agent = null;
        if (isWork)
        {
            agent = AgentsSeen.GetOrAdd($"{client.Id}|{session}|{agentHeader}", _ => new AgentState());
            lock (agent)
            {
                agent.Client = client.Name; agent.Kind = agentHeader.Length > 0 ? agentHeader : "app"; agent.Model = model;
                agent.LastSeen = DateTime.Now; agent.Active++;
            }
            Interlocked.Increment(ref _active);
        }
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), ctx.Request.Path + ctx.Request.QueryString);
            if (body.Length > 0)
            {
                req.Content = new ByteArrayContent(body);
                if (ctx.Request.ContentType is { } ct) req.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
            }
            foreach (var (k, v) in ctx.Request.Headers)
                if (!SkipRequestHeaders.Contains(k) && !k.StartsWith("X-Maison-", StringComparison.OrdinalIgnoreCase))
                    req.Headers.TryAddWithoutValidation(k, v.ToArray());

            HttpResponseMessage resp;
            try { resp = await Ollama.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted); }
            catch (HttpRequestException)
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsJsonAsync(new { error = $"maison: Ollama isn't running on {Name}." });
                return;
            }
            using (resp)
            {
                ctx.Response.StatusCode = (int)resp.StatusCode;
                foreach (var (k, v) in resp.Headers.Concat(resp.Content.Headers))
                    if (!SkipResponseHeaders.Contains(k)) ctx.Response.Headers[k] = v.ToArray();
                await using var s = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted);
                var buf = new byte[16384];
                int n;
                while ((n = await s.ReadAsync(buf, ctx.RequestAborted)) > 0)
                {
                    await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);   // stream tokens as they come
                }
            }
        }
        catch (OperationCanceledException) { /* the agent stopped the request */ }
        finally
        {
            if (agent is not null)
            {
                lock (agent) { agent.Active--; agent.LastSeen = DateTime.Now; }
                Interlocked.Decrement(ref _active);
            }
        }
    }

    /// <summary>The "model" field of a JSON request body, or "".</summary>
    public static string ModelOf(byte[] body)
    {
        if (body.Length == 0 || body[0] != (byte)'{') return "";
        try
        {
            var reader = new Utf8JsonReader(body);
            int depth = 0;
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) depth--;
                else if (depth == 1 && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("model"))
                {
                    reader.Read();
                    return reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
                }
            }
        }
        catch { }
        return "";
    }

    // ------------------------------------------------------------------ info

    public static async Task<PoolInfo> InfoAsync()
    {
        var s = StatsService.Latest;
        var gpu = s?.Gpu is { } g ? new PoolGpu(g.Name, g.TotalGb, g.UsedGb, g.LoadPercent) : null;
        var version = await OllamaClient.GetLocalVersionAsync(2);
        var models = new List<PoolModel>();
        var loaded = new List<PoolLoaded>();
        if (version is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var tags = await Ollama.GetFromJsonAsync<JsonElement>("/api/tags", cts.Token);
                foreach (var m in tags.GetProperty("models").EnumerateArray())
                    models.Add(new PoolModel(m.GetProperty("name").GetString()!, Math.Round(m.GetProperty("size").GetInt64() / 1073741824.0, 1)));
                var ps = await Ollama.GetFromJsonAsync<JsonElement>("/api/ps", cts.Token);
                foreach (var m in ps.GetProperty("models").EnumerateArray())
                    loaded.Add(new PoolLoaded(m.GetProperty("name").GetString()!, Math.Round(m.GetProperty("size").GetInt64() / 1073741824.0, 1),
                        m.TryGetProperty("context_length", out var c) ? c.GetInt32() : 0));
            }
            catch { /* busy: send what we have */ }
        }

        var agents = AgentsSeen.Values
            .Where(a => (DateTime.Now - a.LastSeen).TotalMinutes < 15)
            .Select(a => { lock (a) return new PoolAgent(a.Client, a.Kind, a.Model, a.LastSeen, a.Active > 0); })
            .ToList();
        // maison's own sessions on this AI PC use the same GPU.
        foreach (var t in TerminalHub.Sessions.Where(t => t.IsRunning && t.Kind is "claude" or "qwen" or "chat"))
            agents.Add(new PoolAgent("this PC", t.Kind, t.Spec.Title.Split('·').Last().Trim(), t.LastOutput, !t.IsIdle));

        // Busy: serving a pool request, or the GPU is working for something else (e.g. a session on this PC).
        var ollamaGpu = s?.OllamaGpuPercent ?? 0;
        bool busy = _active > 0 || ollamaGpu > 30;
        var rec = ModelCatalog.Recommend(gpu?.TotalGb ?? 0, SystemProbe.RamGb);
        PoolTask? task;
        lock (TaskGate) task = _task;
        return new PoolInfo(AppConfig.Current.MachineId, Name, AppInfo.Version, gpu, SystemProbe.RamGb,
            SystemProbe.OllamaInstalled, version is not null, version, models, loaded, _active, busy,
            agents.OrderByDescending(a => a.Working).ThenByDescending(a => a.LastSeen).ToList(), task, rec.Tag, rec.SizeGb);
    }

    // ------------------------------------------------------------------ remote jobs

    private static IResult StartTask(string title, Func<Action<string>, Action<double>, Task> work)
    {
        lock (TaskGate)
        {
            if (_task is { Running: true } t) return Results.Json(new { error = $"Busy: {t.Title}." }, statusCode: 409);
            _task = new PoolTask(title, "Starting…", 0, true, null);
        }
        _ = Task.Run(async () =>
        {
            void Log(string status) { lock (TaskGate) _task = _task! with { Status = status }; }
            void Progress(double p) { lock (TaskGate) _task = _task! with { Percent = Math.Round(p, 1) }; }
            try
            {
                await work(Log, Progress);
                lock (TaskGate) _task = _task! with { Running = false, Percent = 100, Status = "Done" };
            }
            catch (Exception ex)
            {
                lock (TaskGate) _task = _task! with { Running = false, Error = ex.Message, Status = "Failed: " + ex.Message };
            }
        });
        return Results.Accepted();
    }

    private static async Task PullAsync(string model, Action<string> log, Action<double> progress)
    {
        if (await OllamaClient.GetLocalVersionAsync(2) is null && !await SystemProbe.StartOllamaAsync())
            throw new InvalidOperationException($"Ollama isn't running on {Name}.");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/pull") { Content = JsonContent.Create(new { model, stream = true }) };
        using var resp = await Ollama.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await resp.Content.ReadAsStringAsync());
        var layers = new Dictionary<string, (long Done, long Total)>();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var err)) throw new InvalidOperationException(err.GetString());
            var status = r.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
            if (r.TryGetProperty("digest", out var dg) && r.TryGetProperty("total", out var tot))
            {
                long done = r.TryGetProperty("completed", out var c) ? c.GetInt64() : 0;
                var key = dg.GetString() ?? "";
                layers[key] = (Math.Max(layers.GetValueOrDefault(key).Done, done), tot.GetInt64());
            }
            long d = layers.Values.Sum(v => v.Done), t = layers.Values.Sum(v => v.Total);
            if (t > 0) { progress(100.0 * d / t); log($"{model}: {d / 1073741824.0:N1} of {t / 1073741824.0:N1} GB"); }
            else log($"{model}: {status}");
            if (status == "success") return;
        }
    }

    /// <summary>Remote "Set up": Ollama if missing, started, and the recommended model if there's none.</summary>
    private static async Task SetupAsync(Action<string> log, Action<double> progress)
    {
        if (!SystemProbe.OllamaInstalled || !SystemProbe.RuntimeOk)
        {
            log("Installing Ollama…");
            await Installer.InstallOllamaAsync(log, new Progress<double>(p => progress(p * 0.3)), CancellationToken.None);
        }
        log("Starting Ollama…");
        if (!await SystemProbe.StartOllamaAsync()) throw new InvalidOperationException("Ollama installed but didn't start.");
        var info = await InfoAsync();
        if (info.Models.Count == 0)
            await PullAsync(info.RecommendedModel, log, p => progress(30 + p * 0.7));
        log("Ready.");
    }
}
