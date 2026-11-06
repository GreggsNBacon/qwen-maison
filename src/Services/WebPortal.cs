using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Maison.Models;

namespace Maison.Services;

/// <summary>
/// The phone web portal: HTTPS on the chosen port, one password, devices remembered by a secure cookie.
/// Shows the Dashboard basics and mirrors the app's terminals live (same sessions, not copies).
/// </summary>
public static class WebPortal
{
    private const string Cookie = "maison_device";
    private static WebApplication? _app;
    private static System.Security.Cryptography.X509Certificates.X509Certificate2? _cert;

    public static bool IsRunning => _app is not null;
    public static int Port { get; private set; }
    public static string Fingerprint => _cert is null ? "" : PortalCert.Fingerprint(_cert);

    public static IReadOnlyList<string> Urls =>
        PortalNet.LanAddresses().Select(ip => $"https://{ip}:{Port}").Append($"https://localhost:{Port}").ToList();

    public static async Task StartAsync()
    {
        if (_app is not null) return;
        var cfg = AppConfig.Current;
        if (!PortalAuth.HasPassword) throw new InvalidOperationException("Set a portal password first.");
        _cert = PortalCert.GetOrCreate();
        Port = cfg.PortalPort;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 64 * 1024;
            k.ListenAnyIP(Port, l => l.UseHttps(_cert));
        });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        // Network filter + security headers on every request.
        app.Use(async (ctx, next) =>
        {
            var remote = ctx.Connection.RemoteIpAddress;
            if (!AppConfig.Current.PortalAllowInternet && !PortalNet.IsPrivate(remote))
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsync("maison: connections from outside the local network are turned off.");
                return;
            }
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Cache-Control"] = "no-store";
            h["Strict-Transport-Security"] = "max-age=31536000";
            h["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' wss:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            await next();
        });

        app.MapGet("/login", () => Results.Content(Page("login.html"), "text/html; charset=utf-8"));
        app.MapGet("/brand.png", () => Results.Bytes(BrandPng(), "image/png"));

        app.MapPost("/api/login", async (HttpContext ctx) =>
        {
            if (!SameOrigin(ctx)) return Results.StatusCode(403);
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            if (PortalAuth.LockedOut(ip) is { } wait)
                return Results.Json(new { ok = false, error = $"Too many attempts. Try again in {Math.Ceiling(wait.TotalMinutes)} min." }, statusCode: 429);
            // A login is tiny; refuse anything bigger before reading it.
            if (ctx.Request.ContentLength is null or > 4096) return Results.StatusCode(413);
            LoginRequest? req;
            try { req = await JsonSerializer.DeserializeAsync<LoginRequest>(ctx.Request.Body, JsonOpts); }
            catch { return Results.BadRequest(); }
            if (req is null || string.IsNullOrEmpty(req.Password) || !PortalAuth.VerifyPassword(req.Password))
            {
                PortalAuth.Failed(ip);
                await Task.Delay(700);
                return Results.Json(new { ok = false, error = "Wrong password." }, statusCode: 401);
            }
            PortalAuth.Succeeded(ip);
            var name = string.IsNullOrWhiteSpace(req.Device) ? DeviceName(ctx) : new string(req.Device.Trim().Where(ch => !char.IsControl(ch)).Take(40).ToArray());
            var token = PortalAuth.NewDevice(name, ip);
            ctx.Response.Cookies.Append(Cookie, token, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/",
                MaxAge = TimeSpan.FromDays(400), IsEssential = true,
            });
            Ui(() => MainWindow.Instance?.Log($"Web portal: new device signed in ({name}, {ip})"));
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/logout", (HttpContext ctx) =>
        {
            if (!SameOrigin(ctx)) return Results.StatusCode(403);
            if (Authed(ctx) is { } d) PortalAuth.Revoke(d);
            ctx.Response.Cookies.Delete(Cookie);
            return Results.Json(new { ok = true });
        });

        app.MapGet("/", (HttpContext ctx) => Authed(ctx) is null ? Results.Redirect("/login") : Results.Content(Page("index.html"), "text/html; charset=utf-8"));

        app.MapGet("/api/state", async (HttpContext ctx) =>
        {
            if (Authed(ctx) is null) return Results.Unauthorized();
            return Results.Json(await StateAsync(), JsonOpts);
        });

        app.MapPost("/api/launch/{kind}", async (HttpContext ctx, string kind) =>
        {
            if (Authed(ctx) is null) return Results.Unauthorized();
            if (!SameOrigin(ctx) || !ctx.Request.Headers.ContainsKey("X-Maison")) return Results.StatusCode(403);
            if (kind is not ("claude" or "qwen" or "chat" or "shell")) return Results.BadRequest();
            var s = await UiAsync(async () => MainWindow.Instance is { } w ? await w.LaunchKindAsync(kind, Launcher.TargetApp) : null);
            return s is null ? Results.Json(new { ok = false, error = "Could not start — check the app's Dashboard." }) : Results.Json(new { ok = true, id = s.Id });
        });

        app.MapPost("/api/sessions/{id}/close", (HttpContext ctx, string id) =>
        {
            if (Authed(ctx) is null) return Results.Unauthorized();
            if (!SameOrigin(ctx) || !ctx.Request.Headers.ContainsKey("X-Maison")) return Results.StatusCode(403);
            if (TerminalHub.Find(id) is not { } s) return Results.NotFound();
            Ui(() => TerminalHub.Remove(s));
            return Results.Json(new { ok = true });
        });

        app.MapGet("/term/{id}", (HttpContext ctx, string id) =>
        {
            if (Authed(ctx) is null) return Results.Redirect("/login");
            if (TerminalHub.Find(id) is not { } s) return Results.Redirect("/");
            return Results.Content(TermPage(s), "text/html; charset=utf-8");
        });

        app.Map("/ws/term/{id}", async (HttpContext ctx, string id) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            if (Authed(ctx) is null || !SameOrigin(ctx)) { ctx.Response.StatusCode = 403; return; }
            if (TerminalHub.Find(id) is not { } s) { ctx.Response.StatusCode = 404; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await PumpAsync(ws, s, ctx.RequestAborted);
        });

        await app.StartAsync();
        _app = app;
    }

    public static async Task StopAsync()
    {
        if (_app is null) return;
        var a = _app;
        _app = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await a.StopAsync(cts.Token);
            await a.DisposeAsync();
        }
        catch { /* already stopping */ }
    }

    // ------------------------------------------------------------------ helpers

    private sealed record LoginRequest(string? Password, string? Device);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static PortalDevice? Authed(HttpContext ctx) => PortalAuth.Device(ctx.Request.Cookies[Cookie]);

    /// <summary>Requests that change something must come from our own page (blocks cross-site requests).</summary>
    private static bool SameOrigin(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return false;
        return Uri.TryCreate(origin, UriKind.Absolute, out var o)
               && o.Scheme == "https" && string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string DeviceName(HttpContext ctx)
    {
        var ua = ctx.Request.Headers.UserAgent.ToString();
        string os = ua.Contains("iPhone") ? "iPhone" : ua.Contains("iPad") ? "iPad" : ua.Contains("Android") ? "Android" : ua.Contains("Windows") ? "Windows" : ua.Contains("Mac") ? "Mac" : "Browser";
        string br = ua.Contains("Edg/") ? "Edge" : ua.Contains("CriOS") || ua.Contains("Chrome/") ? "Chrome" : ua.Contains("Firefox") || ua.Contains("FxiOS") ? "Firefox" : ua.Contains("Safari") ? "Safari" : "";
        return (os + " " + br).Trim();
    }

    private static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    private static async Task<T?> UiAsync<T>(Func<Task<T?>> f)
    {
        if (Application.Current is null) return default;
        return await Application.Current.Dispatcher.InvokeAsync(f).Task.Unwrap();
    }

    private static async Task<object> StateAsync()
    {
        var w = MainWindow.Instance;
        var snapshot = await UiAsync(() => Task.FromResult<object?>(w is null ? null : new
        {
            status = w.CurrentStatusSummary,
            model = w.CurrentMainModel,
            context = w.SelectedContext,
            permissions = w.SelectedPermissions,
            folder = w.WorkingFolder,
            health = w.StatusItems.Select(i => new { level = i.Level.ToString().ToLowerInvariant(), title = i.Title, detail = i.Detail }),
            loaded = w.LoadedModels.Select(m => new { name = m.Name, gb = m.SizeGb, gpu = m.GpuPercent, ctx = m.Context }),
        }));
        var st = StatsService.Latest;
        return new
        {
            app = new { name = "maison", version = AppInfo.Version, machine = Environment.MachineName },
            ui = snapshot,
            stats = st is null ? null : new
            {
                cpu = Math.Round(st.CpuPercent), cpuName = st.CpuName, ramUsed = Math.Round(st.RamUsedGb, 1), ramTotal = Math.Round(st.RamTotalGb),
                gpu = st.Gpu is null ? null : new
                {
                    name = st.Gpu.Name, load = st.Gpu.LoadPercent, temp = st.Gpu.TempC, power = st.Gpu.PowerW,
                    vramUsed = Math.Round(st.Gpu.UsedGb, 1), vramTotal = Math.Round(st.Gpu.TotalGb),
                },
                ollamaCpu = Math.Round(st.OllamaCpuPercent), ollamaRam = Math.Round(st.OllamaRamGb, 1),
                warning = HealthAdvice.VramWarning(st),
            },
            sessions = TerminalHub.Sessions.Select(s => new
            {
                id = s.Id, title = s.Spec.Title, kind = s.Kind, running = s.IsRunning, idle = s.IsIdle,
                started = s.Started, folder = s.Spec.WorkDir,
            }),
        };
    }

    private static async Task PumpAsync(WebSocket ws, TermSession s, CancellationToken ct)
    {
        var outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
        using var link = new TermLink(s, json => outbox.Writer.TryWrite(json));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var sender = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in outbox.Reader.ReadAllAsync(cts.Token))
                    await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, cts.Token);
            }
            catch { /* closed */ }
        });

        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (sb.Length > 1_000_000) break;   // runaway message
                if (!r.EndOfMessage) continue;
                var text = sb.ToString();
                sb.Clear();
                try { link.Receive(text); } catch { /* malformed */ }
            }
        }
        catch { /* disconnected */ }
        finally
        {
            cts.Cancel();
            outbox.Writer.TryComplete();
            try { await sender; } catch { }
            // Answer the client's close (CloseReceived) or start one ourselves (Open).
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                try
                {
                    using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", t.Token);
                }
                catch { }
        }
    }

    private static string TermPage(TermSession s)
    {
        var title = WebUtility.HtmlEncode(s.Spec.Title);
        const string head = """
            #bar { position: absolute; top: 0; left: 0; right: 0; height: 44px; display: flex; align-items: center; gap: 10px; padding: 0 12px;
                   background: #1f1d2b; border-bottom: 1px solid #2d2a40; font: 14px 'Segoe UI', system-ui, sans-serif; color: #ecebf3; z-index: 9; }
            #bar a { color: #b9a7ff; text-decoration: none; font-weight: 600; }
            #bar .t { flex: 1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
            #keys { position: absolute; left: 0; right: 0; bottom: 0; height: 46px; display: flex; gap: 6px; padding: 6px 8px; overflow-x: auto;
                    background: #1f1d2b; border-top: 1px solid #2d2a40; z-index: 9; }
            #keys button { flex: 0 0 auto; min-width: 46px; border: 1px solid #3a3555; background: #2a2740; color: #ecebf3; border-radius: 8px;
                           font: 13px 'Segoe UI', system-ui, sans-serif; padding: 0 10px; }
            #keys button:active { background: #7c5cff; }
            #wrap { top: 50px !important; bottom: 52px !important; left: 6px !important; right: 4px !important; }
            """;
        var body = $$"""
            <div id="bar"><a href="/">‹ maison</a><span class="t">{{title}}</span></div>
            <div id="keys">
              <button data-k="\u001b">Esc</button><button data-k="\t">Tab</button><button data-k="\u0003">Ctrl+C</button>
              <button data-k="\u001b[A">↑</button><button data-k="\u001b[B">↓</button><button data-k="\u001b[D">←</button><button data-k="\u001b[C">→</button>
              <button data-k="\r">Enter</button><button data-k="\u001b[Z">⇧Tab</button><button id="pasteBtn">Paste</button>
            </div>
            <script>
              document.querySelectorAll('#keys button[data-k]').forEach(b => b.addEventListener('click', e => {
                e.preventDefault();
                const k = JSON.parse('"' + b.dataset.k + '"');
                window.dispatchEvent(new CustomEvent('maison-key', { detail: k }));
              }));
              document.getElementById('pasteBtn').addEventListener('click', async () => {
                try { const t = await navigator.clipboard.readText(); window.dispatchEvent(new CustomEvent('maison-paste', { detail: t })); } catch (e) {}
              });
            </script>
            """;
        // The key bar talks to the terminal script (send/claim/pasteText live in its scope).
        const string script = """
            window.addEventListener('maison-key', e => { claim(); send({ t: 'i', d: e.detail }); });
            window.addEventListener('maison-paste', e => pasteText(e.detail));
            """;
        return TerminalPage.Build("ws", $"/ws/term/{s.Id}", head, body, script);
    }

    private static string Page(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("portal/" + name)
                      ?? throw new FileNotFoundException("Missing embedded resource portal/" + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static byte[]? _brand;
    private static byte[] BrandPng()
    {
        if (_brand is not null) return _brand;
        using var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/brand/maison-256.png"))!.Stream;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return _brand = ms.ToArray();
    }
}
