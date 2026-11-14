using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Maison.Models;

namespace Maison.Services;

/// <summary>Password hashing, remembered-device tokens and login throttling for the web portal.</summary>
public static class PortalAuth
{
    private const int Iterations = 600_000;
    private static readonly Dictionary<string, (int Fails, DateTime Until)> PerIp = [];
    private static readonly List<DateTime> RecentFails = [];
    private static readonly object Gate = new();

    public static bool HasPassword => AppConfig.Current.PortalPasswordHash.Length > 0;
    public static bool HasUsername => AppConfig.Current.PortalUsername.Length > 0;
    /// <summary>The portal only starts once both a username and a password exist.</summary>
    public static bool HasCredentials => HasUsername && HasPassword;
    public static bool TotpEnabled => AppConfig.Current.PortalTotpSecret.Length > 0;

    public static string NormaliseUsername(string? u) => (u ?? "").Trim().ToLowerInvariant();

    public static void SetUsername(string username)
    {
        var c = AppConfig.Current;
        c.PortalUsername = NormaliseUsername(username);
        c.PortalDevices.Clear();
        c.Save();
    }

    /// <summary>
    /// Checks everything the login form sent. Always does the slow password hash and never says which part was wrong,
    /// so a stranger can't learn whether a username exists or whether two-step is on.
    /// </summary>
    public static bool Login(string? username, string? password, string? code)
    {
        var c = AppConfig.Current;
        var userBytes = System.Text.Encoding.UTF8.GetBytes(NormaliseUsername(username));
        var wantBytes = System.Text.Encoding.UTF8.GetBytes(c.PortalUsername);
        bool userOk = c.PortalUsername.Length > 0 && CryptographicOperations.FixedTimeEquals(userBytes, wantBytes);
        bool pwOk = VerifyPassword(password ?? "");
        if (!userOk || !pwOk) return false;
        if (!TotpEnabled) return true;
        lock (Gate)
        {
            var secret = Totp.Unprotect(c.PortalTotpSecret);
            if (secret is null || !Totp.Verify(secret, code, c.PortalTotpLastStep, out var step)) return false;
            c.PortalTotpLastStep = step;   // a code works once
            c.Save();
            return true;
        }
    }

    /// <summary>Turns two-step sign-in on once the user has proved their authenticator shows the right code.</summary>
    public static bool EnableTotp(byte[] secret, string? code)
    {
        if (!Totp.Verify(secret, code, 0, out var step)) return false;
        var c = AppConfig.Current;
        c.PortalTotpSecret = Totp.Protect(secret);
        c.PortalTotpLastStep = step;
        c.PortalDevices.Clear();   // devices signed in without a code have to sign in again
        c.Save();
        return true;
    }

    public static void DisableTotp()
    {
        var c = AppConfig.Current;
        c.PortalTotpSecret = "";
        c.PortalTotpLastStep = 0;
        c.Save();
    }

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password)
    {
        var parts = AppConfig.Current.PortalPasswordHash.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out var iter)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Changing the password signs every remembered device out.</summary>
    public static void SetPassword(string password)
    {
        var c = AppConfig.Current;
        c.PortalPasswordHash = HashPassword(password);
        c.PortalDevices.Clear();
        c.Save();
    }

    private static string Sha(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>Creates a remembered device and returns its secret token (only the hash is stored).</summary>
    public static string NewDevice(string name, string address)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var c = AppConfig.Current;
        lock (c.PortalDevices)
        {
            c.PortalDevices.Add(new PortalDevice
            {
                TokenHash = Sha(token), Name = Trim(name, 80), Address = address, Created = DateTime.Now, LastSeen = DateTime.Now,
            });
        }
        c.Save();
        return token;
    }

    public static PortalDevice? Device(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        var h = Sha(token);
        var c = AppConfig.Current;
        PortalDevice? d;
        lock (c.PortalDevices) d = c.PortalDevices.FirstOrDefault(x => CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(x.TokenHash), System.Text.Encoding.ASCII.GetBytes(h)));
        if (d is not null && (DateTime.Now - d.LastSeen).TotalMinutes > 5) { d.LastSeen = DateTime.Now; c.Save(); }
        return d;
    }

    public static void Revoke(PortalDevice d)
    {
        var c = AppConfig.Current;
        lock (c.PortalDevices) c.PortalDevices.Remove(d);
        c.Save();
    }

    /// <summary>Null when this address may try a password now; otherwise how long to wait.</summary>
    public static TimeSpan? LockedOut(string ip)
    {
        lock (Gate)
        {
            var now = DateTime.Now;
            RecentFails.RemoveAll(t => (now - t).TotalHours > 1);
            if (RecentFails.Count >= 30) return TimeSpan.FromMinutes(15);   // global cap across all addresses
            if (PerIp.TryGetValue(ip, out var e) && e.Until > now) return e.Until - now;
            return null;
        }
    }

    public static void Failed(string ip)
    {
        lock (Gate)
        {
            var now = DateTime.Now;
            RecentFails.Add(now);
            var e = PerIp.GetValueOrDefault(ip);
            e.Fails++;
            if (e.Fails >= 5) { e.Until = now.AddMinutes(15); e.Fails = 0; }
            PerIp[ip] = e;
        }
    }

    public static void Succeeded(string ip) { lock (Gate) PerIp.Remove(ip); }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Trim(string s, int n) => s.Length > n ? s[..n] : s;
}

/// <summary>Time-based one-time codes (RFC 6238: HMAC-SHA1, 30 seconds, 6 digits) for Google Authenticator, Microsoft Authenticator, Aegis, etc.</summary>
public static class Totp
{
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new System.Text.StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static string Uri(byte[] secret, string account) =>
        $"otpauth://totp/maison:{System.Uri.EscapeDataString(account)}?secret={Base32(secret)}&issuer=maison&algorithm=SHA1&digits=6&period=30";

    public static string Code(byte[] secret, long step)
    {
        var msg = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        var h = HMACSHA1.HashData(secret, msg);
        int o = h[^1] & 0x0F;
        int bin = ((h[o] & 0x7F) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    public static long NowStep(DateTimeOffset? now = null) => (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;

    /// <summary>Accepts this window and one either side (phone clocks drift) but never a window at or before <paramref name="lastStep"/>.</summary>
    public static bool Verify(byte[] secret, string? code, long lastStep, out long matchedStep)
    {
        matchedStep = 0;
        var digits = new string((code ?? "").Where(char.IsDigit).ToArray());   // phones show "123 456"
        if (digits.Length != 6) return false;
        var given = System.Text.Encoding.ASCII.GetBytes(digits);
        bool found = false;
        long now = NowStep();
        for (long s = now - 1; s <= now + 1; s++)
        {
            bool same = CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Code(secret, s)), given);
            if (same && s > lastStep && !found) { found = true; matchedStep = s; }
        }
        return found;
    }

    public static string Protect(byte[] secret) =>
        Convert.ToBase64String(ProtectedData.Protect(secret, "maison-portal-totp"u8.ToArray(), DataProtectionScope.CurrentUser));

    public static string ProtectText(string text) => Protect(System.Text.Encoding.UTF8.GetBytes(text));
    public static string? UnprotectText(string stored) => Unprotect(stored) is { } b ? System.Text.Encoding.UTF8.GetString(b) : null;

    public static byte[]? Unprotect(string stored)
    {
        try { return ProtectedData.Unprotect(Convert.FromBase64String(stored), "maison-portal-totp"u8.ToArray(), DataProtectionScope.CurrentUser); }
        catch { return null; }
    }
}

/// <summary>
/// Windows Firewall for the portal. Windows' own "allow maison?" prompt makes a rule per exe file and often only for
/// Public networks, so a portal that worked once stops when the network is Private, the exe moves, or it updates.
/// maison's own rule is by port, for the right networks, and is kept in step with the portal settings.
/// </summary>
public static class PortalFirewall
{
    public const string RuleName = "maison web portal";

    public sealed record Status(bool RuleOk, List<string> PublicNetworks, string Summary)
    {
        public bool Healthy => RuleOk && PublicNetworks.Count == 0;
    }

    /// <summary>Reads (without admin) whether a rule lets phones reach <paramref name="port"/> on the networks this PC is on.</summary>
    public static async Task<Status> CheckAsync(int port, bool allowInternet, bool friendly = false)
    {
        const string script = """
            $out = @()
            foreach ($x in @(Get-NetFirewallRule -DisplayName 'maison web portal*' -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' -and $_.Action -eq 'Allow' })) {
              $p = ($x | Get-NetFirewallPortFilter).LocalPort -join ','
              $a = ($x | Get-NetFirewallAddressFilter).RemoteAddress -join ','
              $out += "RULE|$($x.DisplayName)|$p|$($x.Profile)|$a"
            }
            foreach ($n in @(Get-NetConnectionProfile)) { $out += "NET|$($n.Name)|$($n.NetworkCategory)" }
            $out
            """;
        var text = await AiSharing.RunPowerShellAsync(script);
        bool ruleOk = false;
        var pub = new List<string>();
        var categories = new List<string>();
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = raw.Trim().Split('|');
            if (f is ["RULE", var rname, var p, var prof, var addr])
            {
                if (rname.Contains("mDNS", StringComparison.OrdinalIgnoreCase)) continue;   // left by maison 0.0.8/0.0.9; no longer needed
                bool profileOk = prof.Contains("Any", StringComparison.OrdinalIgnoreCase) || prof.Contains("Private", StringComparison.OrdinalIgnoreCase)
                                 || prof.Contains("Domain", StringComparison.OrdinalIgnoreCase);
                bool addrOk = !allowInternet || addr.Contains("Any", StringComparison.OrdinalIgnoreCase);
                var ports = p.Split(',').Select(x => x.Trim()).ToList();
                bool portsOk = ports.Contains(port.ToString()) && (!friendly || (ports.Contains("443") && ports.Contains("80")));
                if (portsOk && profileOk && addrOk) ruleOk = true;
            }
            else if (f is ["NET", var name, var cat])
            {
                categories.Add(cat);
                if (cat.Equals("Public", StringComparison.OrdinalIgnoreCase)) pub.Add(name);
            }
        }
        string summary = !ruleOk ? friendly ? $"Windows Firewall isn't set up for the portal (ports {port}, 80 and 443), so phones are blocked."
                      : $"Windows Firewall has no rule for port {port}, so phones are blocked."
            : pub.Count > 0 && !allowInternet ? $"Your network ({pub[0]}) is set to Public in Windows, which blocks phones."
            : "Windows Firewall lets your phone through.";
        return new Status(ruleOk, allowInternet ? [] : pub, summary);
    }

    /// <summary>One admin prompt: (re)creates the rule for this port. Returns null on success, else why not.</summary>
    public static async Task<string?> EnsureAsync(int port, bool allowInternet, bool makePrivate, bool friendly = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"Get-NetFirewallRule -DisplayName '{RuleName}*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
        var scope = allowInternet ? "-Profile Any -RemoteAddress Any" : "-Profile Private,Domain -RemoteAddress LocalSubnet,100.64.0.0/10";
        sb.AppendLine($"New-NetFirewallRule -DisplayName '{RuleName}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {(friendly ? $"{port},443,80" : port.ToString())} {scope} " +
                      "-Description 'Lets your phone reach the maison web portal. Managed in maison Settings.' | Out-Null");
        if (makePrivate)
            sb.AppendLine("Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public' | ForEach-Object { Set-NetConnectionProfile -InterfaceIndex $_.InterfaceIndex -NetworkCategory Private }");
        return await AiSharing.RunElevatedPowerShellAsync(sb.ToString());
    }

    public static async Task<string?> RemoveAsync() =>
        await AiSharing.RunElevatedPowerShellAsync($"Get-NetFirewallRule -DisplayName '{RuleName}*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
}

public static class PortalNet
{
    /// <summary>Loopback, RFC1918, CGNAT/Tailscale (100.64/10), link-local and IPv6 ULA/link-local.</summary>
    public static bool IsPrivate(IPAddress? ip)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 169 && b[1] == 254);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
    }

    /// <summary>The address a phone on the same Wi-Fi should use: the one on the adapter that has the router (default gateway).</summary>
    public static IPAddress? PreferredAddress() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a) && !IPAddress.IsLoopback(a) && !IsLinkLocal(a))
        ?? LanAddresses().FirstOrDefault(a => !IsLinkLocal(a));

    /// <summary>169.254.x.x: what an adapter gives itself when it has no real network. Never useful to a phone.</summary>
    public static bool IsLinkLocal(IPAddress a) => a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [169, 254, ..];

    public static List<IPAddress> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a) && !IPAddress.IsLoopback(a))
            .Distinct()
            .ToList();
}

/// <summary>Self-signed HTTPS certificate for the portal, kept in the user's certificate store (private key never on disk as a file).</summary>
public static class PortalCert
{
    /// <summary>True when the portal uses the user's own certificate instead of maison's self-signed one.</summary>
    public static bool IsCustom => AppConfig.Current.PortalCertPfxPath.Length > 0;

    public static X509Certificate2 GetOrCreate()
    {
        var c = AppConfig.Current;
        if (IsCustom)
        {
            // A trusted certificate (e.g. from Let's Encrypt) lets phones install the portal as an app with no warnings.
            if (!File.Exists(c.PortalCertPfxPath)) throw new InvalidOperationException($"The certificate file {c.PortalCertPfxPath} was not found.");
            var pw = c.PortalCertPfxPassword.Length == 0 ? "" : Totp.UnprotectText(c.PortalCertPfxPassword) ?? "";
            try { return X509CertificateLoader.LoadPkcs12FromFile(c.PortalCertPfxPath, pw, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.EphemeralKeySet); }
            catch (Exception ex) { throw new InvalidOperationException("Could not open the certificate (wrong password?): " + ex.Message); }
        }
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (c.PortalCertThumbprint.Length > 0)
        {
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, c.PortalCertThumbprint, validOnly: false);
            if (found.Count > 0 && found[0].HasPrivateKey && found[0].NotAfter > DateTime.Now.AddDays(30)) return found[0];
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=maison", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("maison");   // the short address (new certificates only; existing ones are kept so paired PCs don't break)
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in PortalNet.LanAddresses()) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var temp = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(3));
        var pfxPass = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var cert = X509CertificateLoader.LoadPkcs12(temp.Export(X509ContentType.Pfx, pfxPass), pfxPass,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        cert.FriendlyName = "maison web portal";
        store.Add(cert);
        c.PortalCertThumbprint = cert.Thumbprint;
        c.Save();
        return cert;
    }

    /// <summary>SHA-256 fingerprint, so the user can check the browser's certificate warning is really this PC.</summary>
    public static string Fingerprint(X509Certificate2 cert) =>
        string.Join(":", cert.GetCertHash(HashAlgorithmName.SHA256).Take(8).Select(b => b.ToString("X2"))) + "…";
}

/// <summary>
/// This network's public address, for the QR code and address list when outside access is on. Asks api.ipify.org
/// (it only sees this connection's address, which any website you visit sees too) and is only ever called when
/// "Allow connections from outside" is on. Cached for ten minutes.
/// </summary>
public static class PublicIp
{
    private static string? _ip;
    private static DateTime _at;
    public static string? Cached => _ip;

    public static async Task<string?> GetAsync(bool force = false)
    {
        if (!force && _ip is not null && (DateTime.Now - _at).TotalMinutes < 10) return _ip;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var text = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork) { _ip = ip.ToString(); _at = DateTime.Now; }
        }
        catch { /* offline: keep whatever we had */ }
        return _ip;
    }
}

/// <summary>
/// What has reached the portal, so Settings can tell "the phone never got here" (network problem) from
/// "it connected but its browser refused the certificate" from "it loaded the page".
/// </summary>
public static class PortalDiag
{
    public sealed record Seen(string Address, DateTime At, string What);
    private static readonly object Gate = new();
    private static Seen? _connection, _page, _plainHttp;

    public static Seen? LastConnection { get { lock (Gate) return _connection; } }
    public static Seen? LastPage { get { lock (Gate) return _page; } }
    public static Seen? LastPlainHttp { get { lock (Gate) return _plainHttp; } }

    private static bool IsThisPc(IPAddress? ip)
    {
        if (ip is null) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip) || PortalNet.LanAddresses().Contains(ip);
    }

    public static void Connection(IPAddress? ip, bool plainHttp)
    {
        if (IsThisPc(ip)) return;
        var s = new Seen(Clean(ip), DateTime.Now, plainHttp ? "plain http" : "tls");
        lock (Gate) { _connection = s; if (plainHttp) _plainHttp = s; }
    }

    public static void Page(IPAddress? ip, string path)
    {
        if (IsThisPc(ip)) return;
        lock (Gate) _page = new Seen(Clean(ip), DateTime.Now, path);
    }

    private static string Clean(IPAddress? ip) => ip is null ? "?" : ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
}
