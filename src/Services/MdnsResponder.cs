using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Maison.Services;

/// <summary>
/// Answers multicast-DNS questions for one name (maison.local) with this PC's home-network addresses, so phones and other
/// PCs can open https://maison.local without knowing the IP. Windows has an mDNS client but no responder for custom names.
/// Only IPv4 A records, only private addresses; it never answers for any other name.
/// </summary>
public static class MdnsResponder
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;
    private static Socket? _sock;
    private static CancellationTokenSource? _cts;
    private static string _name = "";

    public static bool IsRunning => _sock is not null;
    /// <summary>Why it couldn't start, or empty.</summary>
    public static string Problem { get; private set; } = "";

    public static void Start(string host)
    {
        Stop();
        Problem = "";
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);   // Windows' own mDNS client shares the port
            s.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            foreach (var (ip, _) in Interfaces())
                try { s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, ip)); } catch { /* that adapter has no multicast */ }
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            _sock = s;
            _name = host.ToLowerInvariant();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => LoopAsync(s, ct));
            foreach (var (ip, _) in Interfaces()) Announce(s, ip);
        }
        catch (Exception ex)
        {
            Problem = "Could not start the maison.local name service: " + ex.Message;
            Stop();
        }
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _sock?.Dispose(); } catch { }
        _sock = null;
        _cts = null;
    }

    /// <summary>This PC's usable IPv4 addresses on private networks, with their subnet prefix length.</summary>
    private static List<(IPAddress Ip, int Prefix)> Interfaces() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && PortalNet.IsPrivate(a.Address) && !IPAddress.IsLoopback(a.Address))
            .Select(a => (a.Address, a.PrefixLength))
            .ToList();

    private static async Task LoopAsync(Socket s, CancellationToken ct)
    {
        var buf = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await s.ReceiveFromAsync(buf, SocketFlags.None, any, ct);
                if (r.RemoteEndPoint is IPEndPoint from) Handle(s, buf, r.ReceivedBytes, from);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch { /* a bad packet or a network change: keep listening */ }
        }
    }

    private static void Handle(Socket s, byte[] p, int len, IPEndPoint from)
    {
        if (len < 12 || (p[2] & 0x80) != 0) return;   // too short, or it's someone else's answer
        int questions = (p[4] << 8) | p[5];
        int pos = 12;
        for (int q = 0; q < questions; q++)
        {
            var name = ReadName(p, len, ref pos);
            if (name is null || pos + 4 > len) return;
            int type = (p[pos] << 8) | p[pos + 1];
            bool unicast = (p[pos + 2] & 0x80) != 0;
            pos += 4;
            if (!name.Equals(_name, StringComparison.OrdinalIgnoreCase) || (type != 1 && type != 255)) continue;

            // Answer with the address on the same network as the asker (or all of ours if we can't tell).
            var mine = Interfaces();
            var local = mine.Where(m => SameSubnet(m.Ip, from.Address, m.Prefix)).Select(m => m.Ip).ToList();
            if (local.Count == 0) local = mine.Select(m => m.Ip).ToList();
            if (local.Count == 0) return;
            var reply = BuildAnswer(local);
            if (unicast || from.Port != MdnsPort) s.SendTo(reply, from);
            else
            {
                try { s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local[0].GetAddressBytes()); } catch { }
                s.SendTo(reply, new IPEndPoint(Group, MdnsPort));
            }
            return;
        }
    }

    private static void Announce(Socket s, IPAddress ip)
    {
        try
        {
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
            s.SendTo(BuildAnswer([ip]), new IPEndPoint(Group, MdnsPort));
        }
        catch { /* announcing is a courtesy */ }
    }

    private static byte[] BuildAnswer(List<IPAddress> ips)
    {
        var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        U16(0); U16(0x8400); U16(0); U16(ips.Count); U16(0); U16(0);   // id, flags (response + authoritative), counts
        foreach (var ip in ips)
        {
            foreach (var label in _name.Split('.')) { var b = Encoding.UTF8.GetBytes(label); ms.WriteByte((byte)b.Length); ms.Write(b); }
            ms.WriteByte(0);
            U16(1);                       // A
            U16(0x8001);                  // IN, cache-flush
            U16(0); U16(120);             // TTL 120 s
            U16(4); ms.Write(ip.GetAddressBytes());
        }
        return ms.ToArray();
    }

    private static string? ReadName(byte[] p, int len, ref int pos)
    {
        var sb = new StringBuilder();
        while (pos < len)
        {
            int n = p[pos++];
            if (n == 0) return sb.ToString();
            if ((n & 0xC0) != 0) return null;   // compression pointers aren't used in questions we care about
            if (pos + n > len) return null;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(p, pos, n));
            pos += n;
        }
        return null;
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, int prefix)
    {
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        if (x.Length != 4 || y.Length != 4) return false;
        int full = prefix / 8, rest = prefix % 8;
        for (int i = 0; i < full && i < 4; i++) if (x[i] != y[i]) return false;
        if (rest > 0 && full < 4) { int mask = 0xFF << (8 - rest); if ((x[full] & mask) != (y[full] & mask)) return false; }
        return true;
    }
}
