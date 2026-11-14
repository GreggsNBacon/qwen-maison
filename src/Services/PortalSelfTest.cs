using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Maison.Models;

namespace Maison.Services;

/// <summary>Self-test for the web portal: talks to it over real HTTPS like a phone would.</summary>
public static class PortalSelfTest
{
    public static async Task<string> RunAsync(string password, string dir)
    {
        var r = new List<string>();
        const string user = "selftest-user";
        PortalAuth.SetPassword(password);
        PortalAuth.SetUsername(user);
        AppConfig.Current.PortalPort = 47231;
        await WebPortal.StartAsync();
        var baseUrl = "https://localhost:47231";
        var origin = baseUrl;

        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            CookieContainer = cookies, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == AppConfig.Current.PortalCertThumbprint,
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };

        var home = await http.GetAsync("/");
        r.Add($"unauthedHomeRedirects={(int)home.StatusCode == 302 && home.Headers.Location?.ToString() == "/login"}");
        r.Add($"unauthedStateIs401={(int)(await http.GetAsync("/api/state")).StatusCode == 401}");
        var login = await http.GetAsync("/login");
        r.Add($"loginPage={(int)login.StatusCode} csp={login.Headers.Contains("Content-Security-Policy")}");
        await File.WriteAllTextAsync(Path.Combine(dir, "portal-login.html"), await login.Content.ReadAsStringAsync());

        async Task<HttpResponseMessage> Login(string pw, string? org, string un = user, string? code = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/login")
            { Content = new StringContent(JsonSerializer.Serialize(new { username = un, password = pw, code, device = "selftest" }), Encoding.UTF8, "application/json") };
            if (org is not null) req.Headers.Add("Origin", org);
            return await http.SendAsync(req);
        }

        r.Add($"crossSiteLoginBlocked={(int)(await Login(password, "https://evil.example")).StatusCode == 403}");
        r.Add($"wrongPassword401={(int)(await Login("wrong-password", origin)).StatusCode == 401}");
        r.Add($"wrongUsername401={(int)(await Login(password, origin, "someone-else")).StatusCode == 401}");
        r.Add($"usernameCaseInsensitive={(int)(await Login(password, origin, user.ToUpperInvariant())).StatusCode == 200}");
        AppConfig.Current.PortalDevices.Clear();
        var ok = await Login(password, origin);
        var setCookie = ok.Headers.TryGetValues("Set-Cookie", out var sc) ? string.Join(";", sc) : "";
        r.Add($"rightPassword={(int)ok.StatusCode} cookieHttpOnly={setCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase)} secure={setCookie.Contains("secure", StringComparison.OrdinalIgnoreCase)} samesiteStrict={setCookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)}");
        r.Add($"devicesStored={AppConfig.Current.PortalDevices.Count} tokenStoredHashed={!setCookie.Contains(AppConfig.Current.PortalDevices.FirstOrDefault()?.TokenHash ?? "x")}");

        var state = await http.GetAsync("/api/state");
        var stateJson = await state.Content.ReadAsStringAsync();
        r.Add($"stateAuthed={(int)state.StatusCode} hasStats={stateJson.Contains("\"cpu\"")}");
        await File.WriteAllTextAsync(Path.Combine(dir, "portal-state.json"), stateJson);

        // Launch a shell through the portal (no X-Maison header → refused; with it → ok).
        var bad = new HttpRequestMessage(HttpMethod.Post, "/api/launch/shell");
        bad.Headers.Add("Origin", origin);
        r.Add($"launchWithoutHeaderBlocked={(int)(await http.SendAsync(bad)).StatusCode == 403}");
        var launch = new HttpRequestMessage(HttpMethod.Post, "/api/launch/shell");
        launch.Headers.Add("Origin", origin);
        launch.Headers.Add("X-Maison", "1");
        var lj = JsonDocument.Parse(await (await http.SendAsync(launch)).Content.ReadAsStringAsync()).RootElement;
        var id = lj.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        r.Add($"launchedShell={id is not null}");

        if (id is not null)
        {
            await Task.Delay(3000);   // the app tab starts the process
            var termPage = await http.GetAsync($"/term/{id}");
            await File.WriteAllTextAsync(Path.Combine(dir, "portal-term.html"), await termPage.Content.ReadAsStringAsync());

            using var ws = new ClientWebSocket();
            ws.Options.Cookies = cookies;
            ws.Options.SetRequestHeader("Origin", origin);
            ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == AppConfig.Current.PortalCertThumbprint;
            await ws.ConnectAsync(new Uri($"wss://localhost:47231/ws/term/{id}"), CancellationToken.None);
            async Task Send(object o) => await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)), WebSocketMessageType.Text, true, CancellationToken.None);
            var received = new StringBuilder();
            var recvTask = Task.Run(async () =>
            {
                var buf = new byte[1 << 16];
                try
                {
                    while (ws.State == WebSocketState.Open)
                    {
                        var res = await ws.ReceiveAsync(buf, CancellationToken.None);
                        if (res.MessageType == WebSocketMessageType.Close) break;
                        lock (received) received.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                    }
                }
                catch { }
            });
            await Send(new { t = "ready", cols = 80, rows = 24 });
            await Task.Delay(1500);
            await Send(new { t = "i", d = "Write-Output ('phone-' + (8*8))\r" });
            await Task.Delay(3000);
            string got;
            lock (received) got = received.ToString();
            r.Add($"wsGotHistory={got.Contains("\"t\":\"reset\"")} wsSawPhoneInputOutput={got.Contains("phone-64")}");
            var appSees = TerminalHub.Find(id)?.History.Contains("phone-64") == true;
            r.Add($"appSessionSeesPhoneInput={appSees}");
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); r.Add("wsClosedCleanly=True"); }
            catch (Exception ex) { r.Add("wsClosedCleanly=False(" + ex.GetType().Name + ")"); }

            // A WebSocket without the cookie must be refused.
            using var anon = new ClientWebSocket();
            anon.Options.SetRequestHeader("Origin", origin);
            anon.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            bool anonRefused;
            try { await anon.ConnectAsync(new Uri($"wss://localhost:47231/ws/term/{id}"), CancellationToken.None); anonRefused = false; }
            catch { anonRefused = true; }
            r.Add($"wsWithoutCookieRefused={anonRefused}");
        }

        // Two-step sign-in: needs a fresh correct code, and a code only works once.
        var secret = Totp.NewSecret();
        r.Add($"totpRfcVector={Totp.Code("12345678901234567890"u8.ToArray(), 59 / 30) == "287082"}");
        var good = Totp.Code(secret, Totp.NowStep());
        var wrongCode = (int.Parse(good) + 1).ToString("D6");
        r.Add($"totpWrongEnableRefused={!PortalAuth.EnableTotp(secret, wrongCode)}");
        r.Add($"totpEnabled={PortalAuth.EnableTotp(secret, good)} secretEncrypted={!AppConfig.Current.PortalTotpSecret.Contains(Totp.Base32(secret))}");
        r.Add($"loginPageShowsCode={!(await (await http.GetAsync("/login")).Content.ReadAsStringAsync()).Contains("codebox\" hidden")}");
        r.Add($"totpMissingCode401={(int)(await Login(password, origin)).StatusCode == 401}");
        r.Add($"totpReplay401={(int)(await Login(password, origin, code: good)).StatusCode == 401}");
        AppConfig.Current.PortalTotpLastStep = Totp.NowStep() - 5;   // pretend time moved on
        r.Add($"totpFreshCodeOk={(int)(await Login(password, origin, code: Totp.Code(secret, Totp.NowStep()))).StatusCode == 200}");
        PortalAuth.DisableTotp();
        r.Add($"totpOffLoginOk={(int)(await Login(password, origin)).StatusCode == 200}");

        // Install-as-app files are public and correct.
        using (var h3 = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl) })
        {
            var mf = await h3.GetAsync("/manifest.webmanifest");
            var mfJson = JsonDocument.Parse(await mf.Content.ReadAsStringAsync()).RootElement;
            bool maskable = mfJson.GetProperty("icons").EnumerateArray().Any(i => i.GetProperty("purpose").GetString() == "maskable");
            r.Add($"manifest={(int)mf.StatusCode} display={mfJson.GetProperty("display").GetString()} maskableIcon={maskable}");
            var sw = await h3.GetAsync("/sw.js");
            r.Add($"serviceWorker={(int)sw.StatusCode} fetchHandler={(await sw.Content.ReadAsStringAsync()).Contains("'fetch'")}");
            var icon = await h3.GetAsync("/pwa/icon-512.png");
            var iconBytes = await icon.Content.ReadAsByteArrayAsync();
            r.Add($"icon512={(int)icon.StatusCode} png={iconBytes.Length > 8 && iconBytes[1] == (byte)'P'}");
            r.Add($"lockup={(int)(await h3.GetAsync("/pwa/lockup.png")).StatusCode} appleTouch={(int)(await h3.GetAsync("/apple-touch-icon.png")).StatusCode} unknownPwaFile404={(int)(await h3.GetAsync("/pwa/secret.png")).StatusCode == 404}");
            r.Add($"brand={(int)(await h3.GetAsync("/brand.png")).StatusCode}");
        }

        // A phone that types ip:port without https:// sends plain http to the TLS port: it must be redirected, not dropped.
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync("127.0.0.1", 47231);
            var ns = tcp.GetStream();
            await ns.WriteAsync(Encoding.ASCII.GetBytes("GET /term/abc?x=1 HTTP/1.1\r\nHost: 192.168.2.54:47231\r\n\r\n"));
            var rb = new byte[1024];
            int n = await ns.ReadAsync(rb);
            var txt = Encoding.ASCII.GetString(rb, 0, n);
            r.Add($"plainHttpRedirected={txt.StartsWith("HTTP/1.1 307") && txt.Contains("Location: https://192.168.2.54:47231/term/abc?x=1")}");
            using var tcp2 = new System.Net.Sockets.TcpClient();
            await tcp2.ConnectAsync("127.0.0.1", 47231);
            await tcp2.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: evil host/x\r\n\r\n"));
            n = await tcp2.GetStream().ReadAsync(rb);
            r.Add($"plainHttpBadHostRefused={Encoding.ASCII.GetString(rb, 0, n).StartsWith("HTTP/1.1 400")}");
        }
        catch (Exception ex) { r.Add("plainHttpRedirected=ERR(" + ex.GetType().Name + ")"); }
        r.Add($"phoneUrl={(WebPortal.PhoneUrl ?? "none").StartsWith("https://")} noLinkLocalInUrls={!WebPortal.Urls.Any(u => u.Contains("169.254."))} lanUrlsHaveNoLocalhost={!WebPortal.LanUrls.Any(u => u.Contains("localhost"))}");

        // Standard ports: 443 serves the portal and port 80 only redirects (skipped when another program holds them).
        r.Add($"friendlyActive={WebPortal.FriendlyActive} problem=[{WebPortal.FriendlyProblem}]");
        if (WebPortal.FriendlyActive)
        {
            using var h4 = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, AllowAutoRedirect = false });
            try { r.Add($"https443Login={(int)(await h4.GetAsync("https://localhost/login")).StatusCode}"); } catch (Exception ex) { r.Add("https443Login=ERR(" + ex.GetType().Name + ")"); }
            try
            {
                var redir = await h4.GetAsync("http://localhost/term/abc");
                r.Add($"http80Redirects={(int)redir.StatusCode == 302 && redir.Headers.Location?.ToString() == "https://localhost/term/abc"}");
            }
            catch (Exception ex) { r.Add("http80Redirects=ERR(" + ex.GetType().Name + ")"); }
        }

        // Lockout: 5 wrong passwords from one address → the right one is refused too.
        using (var h2 = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri(baseUrl) })
        {
            int code = 0;
            for (int i = 0; i < 6; i++)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/api/login") { Content = new StringContent("{\"username\":\"x\",\"password\":\"nope\"}", Encoding.UTF8, "application/json") };
                req.Headers.Add("Origin", origin);
                code = (int)(await h2.SendAsync(req)).StatusCode;
            }
            var right = new HttpRequestMessage(HttpMethod.Post, "/api/login") { Content = new StringContent(JsonSerializer.Serialize(new { username = user, password }), Encoding.UTF8, "application/json") };
            right.Headers.Add("Origin", origin);
            var rc = (int)(await h2.SendAsync(right)).StatusCode;
            r.Add($"lockoutAfter5={code == 429} rightPasswordWhileLocked={rc}");
        }

        await WebPortal.StopAsync();
        return "PORTAL: " + string.Join(" ", r);
    }
}
