using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Maison.Models;

namespace Maison.Services.Pool;

/// <summary>A PC found on the network that runs maison with "Share this PC's AI" on.</summary>
public sealed record FoundPc(string Id, string Name, string Address, int Port, string Version);

/// <summary>What the pool knows about one AI PC right now.</summary>
public sealed class MemberState
{
    public required PoolMember Member { get; init; }
    public bool IsThisPc { get; init; }
    public bool Online { get; set; }
    public string? Problem { get; set; }
    public PoolInfo? Info { get; set; }
    public DateTime Checked { get; set; }
    /// <summary>Requests this PC's relay has in flight to it.</summary>
    public int OurActive;

    public bool Has(string model) => Info?.Models.Any(m => Same(m.Name, model)) == true;
    public bool HasLoaded(string model) => Info?.Loaded.Any(m => Same(m.Name, model)) == true;
    /// <summary>Busy with work that isn't ours (another PC's agent, or something on that PC itself).</summary>
    public bool BusyForUs => Info is { } i && (i.Active > OurActive || (i.Active == 0 && i.Busy));

    public static bool Same(string a, string b) =>
        string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    private static string Norm(string n) => n.Contains(':') ? n : n + ":latest";
}

/// <summary>
/// The PC-using-the-pool side: finds AI PCs, pairs with them, keeps their status fresh, and talks to them over HTTPS
/// with each one's pinned certificate and key.
/// </summary>
public static class AiPool
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, MemberState> States = new();
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();
    private static CancellationTokenSource? _poll;

    public static event Action? Changed;

    public static string ThisPcId => "this-pc";

    /// <summary>Members in the pool (paired AI PCs, plus this PC when included), with their latest status.</summary>
    public static IReadOnlyList<MemberState> Members
    {
        get
        {
            var cfg = AppConfig.Current;
            var list = new List<MemberState>();
            if (cfg.PoolIncludeThisPc && SystemProbe.OllamaInstalled)
                list.Add(States.GetOrAdd(ThisPcId, _ => new MemberState
                {
                    Member = new PoolMember { Id = ThisPcId, Name = Environment.MachineName + " (this PC)" }, IsThisPc = true,
                }));
            foreach (var m in cfg.PoolMembers)
                list.Add(States.AddOrUpdate(m.Id, _ => new MemberState { Member = m }, (_, s) => s.Member == m ? s : new MemberState { Member = m }));
            return list;
        }
    }

    // ------------------------------------------------------------------ HTTP to an AI PC

    /// <summary>HTTPS client for an address. With a thumbprint, only that certificate is accepted (pinned at pairing).</summary>
    private static HttpClient NewClient(string address, int port, string? thumbprint, Action<string>? seenThumbprint = null, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    var tp = Convert.ToHexString(cert.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256));
                    seenThumbprint?.Invoke(tp);
                    return thumbprint is null || string.Equals(tp, thumbprint, StringComparison.OrdinalIgnoreCase);
                },
            },
        };
        var host = address.Contains(':') && !address.StartsWith('[') ? $"[{address}]" : address;
        return new HttpClient(handler) { BaseAddress = new Uri($"https://{host}:{port}"), Timeout = timeout ?? Timeout.InfiniteTimeSpan };
    }

    /// <summary>The authenticated client for a paired AI PC.</summary>
    public static HttpClient For(PoolMember m)
    {
        var key = $"{m.Id}|{m.Address}|{m.Port}|{m.Thumbprint}|{m.Token}";
        return Clients.GetOrAdd(key, _ =>
        {
            var c = NewClient(m.Address, m.Port, m.Thumbprint);
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", m.Token);
            c.DefaultRequestHeaders.Add("X-Maison-Client", Environment.MachineName);
            return c;
        });
    }

    // ------------------------------------------------------------------ discovery

    /// <summary>
    /// Finds AI PCs on the home network: a broadcast every maison AI PC answers, plus a quick scan of this PC's
    /// network for the pool port (for networks that drop broadcasts). Takes about 2 seconds.
    /// </summary>
    public static async Task<List<FoundPc>> DiscoverAsync()
    {
        var found = new ConcurrentDictionary<string, FoundPc>();
        var selfId = AppConfig.Current.MachineId;
        void Add(PoolHello h, string address)
        {
            if (h.Id == selfId || h.Id.Length == 0) return;
            found.TryAdd(h.Id, new FoundPc(h.Id, h.Name, address, h.Port, h.Version));
        }
        await Task.WhenAll(BroadcastAsync(Add), ScanAsync(Add));
        // Keep paired PCs' addresses current (DHCP can move them).
        bool changed = false;
        foreach (var m in AppConfig.Current.PoolMembers)
            if (found.TryGetValue(m.Id, out var f) && (m.Address != f.Address || m.Port != f.Port || m.Name != f.Name))
            {
                m.Address = f.Address; m.Port = f.Port; m.Name = f.Name; changed = true;
            }
        if (changed) AppConfig.Current.Save();
        return found.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task BroadcastAsync(Action<PoolHello, string> add)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var msg = Encoding.ASCII.GetBytes(PoolPorts.DiscoverMessage);
            var targets = BroadcastAddresses().Append(IPAddress.Broadcast).Append(IPAddress.Loopback).Distinct();
            foreach (var t in targets)
                try { await udp.SendAsync(msg, new IPEndPoint(t, PoolPorts.Discovery)); } catch { }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var r = await udp.ReceiveAsync(cts.Token);
                    var h = JsonSerializer.Deserialize<PoolHello>(r.Buffer, Json);
                    if (h is not null) add(h, r.RemoteEndPoint.Address.ToString());
                }
                catch (OperationCanceledException) { break; }
                catch { /* not ours */ }
            }
        }
        catch { /* no network */ }
    }

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var u in ni.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(u.Address) || u.IPv4Mask is null) continue;
                var ip = u.Address.GetAddressBytes();
                var mask = u.IPv4Mask.GetAddressBytes();
                yield return new IPAddress(ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray());
            }
    }

    /// <summary>Tries the pool port on every address of this PC's home networks (up to /22 each).</summary>
    private static async Task ScanAsync(Action<PoolHello, string> add)
    {
        var hosts = new List<IPAddress> { IPAddress.Loopback };
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var u in ni.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(u.Address) || u.IPv4Mask is null) continue;
                if (!PortalNet.IsPrivate(u.Address)) continue;
                uint ip = Be(u.Address), mask = Be(u.IPv4Mask);
                uint net = ip & mask, size = ~mask;
                if (size > 1024) { mask = 0xFFFFFF00; net = ip & mask; size = 255; }   // big networks: just our /24
                for (uint h = 1; h < size; h++) { var a = net | h; if (a != ip) hosts.Add(FromBe(a)); }
            }
        using var gate = new SemaphoreSlim(128);
        var tasks = hosts.Distinct().Select(async a =>
        {
            await gate.WaitAsync();
            try
            {
                using var tcp = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await tcp.ConnectAsync(a, PoolPorts.Service, cts.Token);
            }
            catch { return; }
            finally { gate.Release(); }
            try
            {
                using var http = NewClient(a.ToString(), PoolPorts.Service, null, timeout: TimeSpan.FromSeconds(3));
                if (await http.GetFromJsonAsync<PoolHello>("/maison/hello", Json) is { } h) add(h, a.ToString());
            }
            catch { /* something else on that port */ }
        });
        await Task.WhenAll(tasks);
    }

    private static uint Be(IPAddress a) { var b = a.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
    private static IPAddress FromBe(uint v) => new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    /// <summary>Asks an address directly (for AI PCs on another network, e.g. over Tailscale).</summary>
    public static async Task<FoundPc?> LookupAsync(string address)
    {
        var a = address.Trim();
        int port = PoolPorts.Service;
        if (a.LastIndexOf(':') is var i and > 0 && a.Count(ch => ch == ':') == 1 && int.TryParse(a[(i + 1)..], out var p)) { port = p; a = a[..i]; }
        try
        {
            using var http = NewClient(a, port, null, timeout: TimeSpan.FromSeconds(5));
            var h = await http.GetFromJsonAsync<PoolHello>("/maison/hello", Json);
            return h is null ? null : new FoundPc(h.Id, h.Name, a, port, h.Version);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ pairing

    /// <summary>Pairs with an AI PC using the code it shows. Pins its certificate and stores its key.</summary>
    public static async Task<(bool Ok, string Message)> PairAsync(FoundPc pc, string code)
    {
        string? thumb = null;
        try
        {
            using var http = NewClient(pc.Address, pc.Port, null, tp => thumb = tp, TimeSpan.FromSeconds(10));
            var cfg = AppConfig.Current;
            using var resp = await http.PostAsJsonAsync("/maison/pair", new PairRequest(code.Trim(), Environment.MachineName, cfg.MachineId), Json);
            if (!resp.IsSuccessStatusCode) return (false, await ErrorOf(resp));
            var r = await resp.Content.ReadFromJsonAsync<PairReply>(Json);
            if (r is null || thumb is null) return (false, "The AI PC gave an unexpected answer.");
            cfg.PoolMembers.RemoveAll(m => m.Id == r.Id);
            cfg.PoolMembers.Add(new PoolMember { Id = r.Id, Name = r.Name, Address = pc.Address, Port = pc.Port, Token = r.Token, Thumbprint = thumb });
            cfg.Save();
            _ = RefreshAsync();
            return (true, $"{r.Name} is in your AI pool.");
        }
        catch (Exception ex) { return (false, $"Couldn't reach {pc.Name}: {ex.Message}"); }
    }

    public static async Task RemoveAsync(PoolMember m)
    {
        try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4)); await For(m).PostAsync("/maison/unpair", null, cts.Token); } catch { }
        AppConfig.Current.PoolMembers.Remove(m);
        AppConfig.Current.Save();
        States.TryRemove(m.Id, out _);
        Changed?.Invoke();
    }

    public static async Task<string> ErrorOf(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        try { return JsonDocument.Parse(text).RootElement.GetProperty("error").GetString() ?? text; }
        catch { return $"{(int)resp.StatusCode} {resp.ReasonPhrase}"; }
    }

    // ------------------------------------------------------------------ remote management

    public static Task<(bool Ok, string Message)> InstallModelAsync(PoolMember m, string model) => PostAsync(m, "/maison/pull", new ModelRequest(model));
    public static Task<(bool Ok, string Message)> DeleteModelAsync(PoolMember m, string model) => PostAsync(m, "/maison/delete", new ModelRequest(model));
    public static Task<(bool Ok, string Message)> SetupAsync(PoolMember m) => PostAsync(m, "/maison/setup", new { });

    private static async Task<(bool, string)> PostAsync(PoolMember m, string path, object body)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var resp = await For(m).PostAsJsonAsync(path, body, Json, cts.Token);
            _ = RefreshAsync();
            return resp.IsSuccessStatusCode ? (true, "Started.") : (false, await ErrorOf(resp));
        }
        catch (Exception ex) { return (false, $"Couldn't reach {m.Name}: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ status polling

    /// <summary>Keeps every member's status fresh (every 2 s while the pool is in use or the pool page is open).</summary>
    public static void StartPolling()
    {
        if (_poll is not null) return;
        _poll = new CancellationTokenSource();
        var ct = _poll.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshAsync();
                try { await Task.Delay(2000, ct); } catch { break; }
            }
        }, ct);
    }

    public static void StopPolling() { _poll?.Cancel(); _poll = null; }

    public static async Task RefreshAsync()
    {
        await Task.WhenAll(Members.Select(RefreshOneAsync));
        Changed?.Invoke();
    }

    private static async Task RefreshOneAsync(MemberState s)
    {
        try
        {
            s.Info = s.IsThisPc ? await PoolHost.InfoAsync() : await GetInfoAsync(s.Member);
            s.Online = s.IsThisPc ? s.Info.OllamaRunning : true;
            s.Problem = s.Info.OllamaRunning ? null : s.Info.OllamaInstalled ? "Ollama isn't running there" : "Ollama isn't installed there";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            s.Online = false; s.Problem = "No longer paired: remove it and add it again";
        }
        catch (Exception ex)
        {
            s.Online = false;
            s.Problem = ex is TaskCanceledException or HttpRequestException ? "Offline (or maison isn't running there)" : ex.Message;
        }
        s.Checked = DateTime.Now;
    }

    private static async Task<PoolInfo> GetInfoAsync(PoolMember m)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var resp = await For(m).GetAsync("/maison/info", cts.Token);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new HttpRequestException("unpaired", null, HttpStatusCode.Unauthorized);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PoolInfo>(Json, cts.Token))!;
    }
}
