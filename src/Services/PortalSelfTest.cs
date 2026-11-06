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
        PortalAuth.SetPassword(password);
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

        async Task<HttpResponseMessage> Login(string pw, string? org)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/login")
            { Content = new StringContent(JsonSerializer.Serialize(new { password = pw, device = "selftest" }), Encoding.UTF8, "application/json") };
            if (org is not null) req.Headers.Add("Origin", org);
            return await http.SendAsync(req);
        }

        r.Add($"crossSiteLoginBlocked={(int)(await Login(password, "https://evil.example")).StatusCode == 403}");
        r.Add($"wrongPassword401={(int)(await Login("wrong-password", origin)).StatusCode == 401}");
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

        // Lockout: 5 wrong passwords from one address → the right one is refused too.
        using (var h2 = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri(baseUrl) })
        {
            int code = 0;
            for (int i = 0; i < 6; i++)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/api/login") { Content = new StringContent("{\"password\":\"nope\"}", Encoding.UTF8, "application/json") };
                req.Headers.Add("Origin", origin);
                code = (int)(await h2.SendAsync(req)).StatusCode;
            }
            var right = new HttpRequestMessage(HttpMethod.Post, "/api/login") { Content = new StringContent(JsonSerializer.Serialize(new { password }), Encoding.UTF8, "application/json") };
            right.Headers.Add("Origin", origin);
            var rc = (int)(await h2.SendAsync(right)).StatusCode;
            r.Add($"lockoutAfter5={code == 429} rightPasswordWhileLocked={rc}");
        }

        await WebPortal.StopAsync();
        return "PORTAL: " + string.Join(" ", r);
    }
}
