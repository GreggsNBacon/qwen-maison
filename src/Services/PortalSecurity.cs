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
    public static X509Certificate2 GetOrCreate()
    {
        var c = AppConfig.Current;
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (c.PortalCertThumbprint.Length > 0)
        {
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, c.PortalCertThumbprint, validOnly: false);
            if (found.Count > 0 && found[0].HasPrivateKey && found[0].NotAfter > DateTime.Now.AddDays(30)) return found[0];
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=maison on {Environment.MachineName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
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
