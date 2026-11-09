// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Wific.Net;

/// <summary>The local IPv4 network wific is connected to.</summary>
public sealed record LanNetwork(
    string AdapterName, bool IsWireless, IPAddress Local, int Prefix, IPAddress? Gateway, byte[] LocalMac,
    IReadOnlyList<IPAddress> Hosts, bool Limited)
{
    public string CidrText => $"{Network(Local, Prefix)}/{Prefix}";

    public static IPAddress Network(IPAddress ip, int prefix)
    {
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return LanProbes.FromUInt(LanProbes.ToUInt(ip) & mask);
    }
}

public sealed record SmbInfo(string? Name, string? DnsName, string? Domain, string? Os);

public sealed record UpnpInfo(string? FriendlyName, string? Manufacturer, string? ModelName, string? ModelNumber);

public sealed record SnmpInfo(string? Name, string? Description, string? Location);

/// <summary>Small, self-contained network probes used by the network device scanner.</summary>
public static class LanProbes
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    })
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    private static readonly Regex TitleRegex = new("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public static uint ToUInt(IPAddress ip) => BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());

    public static IPAddress FromUInt(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }


    // ---- Targets ----

    public const int MaxTargets = 65536;

    /// <summary>
    /// Parses "192.168.1.0/24", "10.0.0.5-10.0.0.40", "10.0.0.5-40" or single addresses, separated by commas.
    /// Returns null and an error message when the text is not valid or covers more than 65,536 addresses.
    /// </summary>
    public static List<IPAddress>? ParseTargets(string text, out string? error)
    {
        error = null;
        var seen = new HashSet<uint>();
        var result = new List<IPAddress>();
        foreach (string raw in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string item = raw.Trim();
            uint first, last;
            int slash = item.IndexOf('/', StringComparison.Ordinal);
            int dash = item.IndexOf('-', StringComparison.Ordinal);
            if (slash > 0)
            {
                if (!TryIpv4(item[..slash], out uint ip)
                    || !int.TryParse(item[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int prefix) || prefix > 32)
                {
                    error = $"“{item}” is not a valid network (example: 192.168.1.0/24).";
                    return null;
                }
                if (prefix < 16)
                {
                    error = $"“{item}” is too large. The largest network wific scans at once is a /16 (65,536 addresses).";
                    return null;
                }
                uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
                first = ip & mask;
                last = first | ~mask;
                if (prefix <= 30)
                {
                    first++;
                    last--;
                }
            }
            else if (dash > 0)
            {
                string from = item[..dash], to = item[(dash + 1)..];
                if (!TryIpv4(from, out first))
                {
                    error = $"“{item}” is not a valid range (example: 192.168.1.10-192.168.1.50).";
                    return null;
                }
                if (TryIpv4(to, out uint end)) last = end;
                else if (byte.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out byte octet)) last = (first & 0xFFFFFF00) | octet;
                else
                {
                    error = $"“{item}” is not a valid range (example: 192.168.1.10-50).";
                    return null;
                }
                if (last < first)
                {
                    error = $"“{item}”: the range ends before it starts.";
                    return null;
                }
            }
            else if (TryIpv4(item, out first))
            {
                last = first;
            }
            else
            {
                error = $"“{item}” is not an IPv4 address, range or network.";
                return null;
            }

            if ((ulong)last - first + 1 + (ulong)result.Count > MaxTargets)
            {
                error = "That is more than 65,536 addresses. Please scan a smaller range.";
                return null;
            }
            for (ulong a = first; a <= last; a++)
                if (seen.Add((uint)a)) result.Add(FromUInt((uint)a));
        }
        if (result.Count == 0) error = "Enter an address, a range or a network to scan.";
        return result.Count == 0 ? null : result;
    }

    private static bool TryIpv4(string text, out uint value)
    {
        value = 0;
        var parts = text.Trim().Split('.');
        if (parts.Length != 4) return false;
        foreach (string p in parts)
        {
            if (!byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out byte b)) return false;
            value = (value << 8) | b;
        }
        return true;
    }
    // ---- Network ----

    /// <summary>The adapter with a default gateway (Wi-Fi preferred) and its IPv4 subnet; networks larger than /22 are limited to the local /24.</summary>
    public static LanNetwork? GetCurrentNetwork()
    {
        LanNetwork? best = null;
        int bestScore = -1;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = ni.GetIPProperties();
            var unicast = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork
                                                                     && !IPAddress.IsLoopback(u.Address));
            if (unicast == null) continue;
            var gateway = props.GatewayAddresses.Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            bool wireless = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            int score = (gateway != null ? 4 : 0) + (wireless ? 2 : 0);
            if (score <= bestScore) continue;

            int prefix = unicast.PrefixLength;
            bool limited = false;
            if (prefix < 22)
            {
                prefix = 24;
                limited = true;
            }
            uint net = ToUInt(LanNetwork.Network(unicast.Address, prefix));
            uint count = prefix >= 31 ? 0 : (1u << (32 - prefix)) - 2;
            var hosts = new List<IPAddress>((int)count);
            for (uint i = 1; i <= count; i++) hosts.Add(FromUInt(net + i));

            best = new LanNetwork(ni.Name, wireless, unicast.Address, prefix, gateway,
                ni.GetPhysicalAddress().GetAddressBytes(), hosts, limited);
            bestScore = score;
        }
        return best;
    }

    // ---- Discovery ----

    public static async Task<int?> PingAsync(IPAddress ip, int timeoutMs)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(ip, timeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? (int)Math.Max(0, reply.RoundtripTime) : null;
        }
        catch (PingException)
        {
            return null;
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order);

    /// <summary>IPv4 neighbours Windows has resolved (ARP cache). A host that ignores ping is usually still here.</summary>
    public static Dictionary<uint, byte[]> ReadArpTable()
    {
        var result = new Dictionary<uint, byte[]>();
        int size = 0;
        _ = GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0) return result;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0) return result;
            int count = Marshal.ReadInt32(buffer);
            for (int i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * 24;
                int macLength = Marshal.ReadInt32(row, 4);
                int type = Marshal.ReadInt32(row, 20);
                if (macLength != 6 || type == 2) continue; // 2 = invalid
                var mac = new byte[6];
                Marshal.Copy(row + 8, mac, 0, 6);
                if (mac.All(b => b == 0) || mac.All(b => b == 0xFF)) continue;
                var ip = new byte[4];
                Marshal.Copy(row + 16, ip, 0, 4);
                result[BinaryPrimitives.ReadUInt32BigEndian(ip)] = mac;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    public static async Task<bool> IsPortOpenAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // ---- Names ----

    public static async Task<string?> ReverseDnsAsync(IPAddress ip)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(lookup, Task.Delay(1500)).ConfigureAwait(false) != lookup) return null;
            string name = (await lookup.ConfigureAwait(false)).HostName;
            return string.IsNullOrWhiteSpace(name) || name == ip.ToString() ? null : name;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> UdpQueryAsync(IPAddress ip, int port, byte[] query, int timeoutMs)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await udp.SendAsync(query, new IPEndPoint(ip, port), cts.Token).ConfigureAwait(false);
            while (true)
            {
                var reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (reply.RemoteEndPoint.Address.Equals(ip)) return reply.Buffer;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>NetBIOS node status (UDP 137): computer name and workgroup of Windows PCs, Samba servers and NAS boxes.</summary>
    public static async Task<(string? Name, string? Group)> NetBiosAsync(IPAddress ip)
    {
        var q = new byte[50];
        q[0] = 0x57;
        q[1] = 0x46;
        q[5] = 1;
        q[12] = 0x20;
        q[13] = (byte)'C';
        q[14] = (byte)'K';
        for (int i = 15; i < 45; i++) q[i] = (byte)'A';
        q[47] = 0x21;
        q[49] = 1;
        var d = await UdpQueryAsync(ip, 137, q, 900).ConfigureAwait(false);
        if (d == null || d.Length < 57) return (null, null);
        try
        {
            int p = 12;
            ReadDnsName(d, ref p);
            p += 10; // type, class, ttl, rdlength
            int count = d[p++];
            string? name = null, group = null;
            for (int i = 0; i < count && p + 18 <= d.Length; i++, p += 18)
            {
                string n = Encoding.ASCII.GetString(d, p, 15).Trim();
                byte suffix = d[p + 15];
                bool isGroup = (d[p + 16] & 0x80) != 0;
                if (suffix != 0x00 || n.Length == 0) continue;
                if (isGroup) group ??= n;
                else name ??= n;
            }
            return (name, group);
        }
        catch (IndexOutOfRangeException)
        {
            return (null, null);
        }
    }

    /// <summary>Asks the host itself over mDNS (unicast to port 5353) for its .local name. Apple devices, printers, Linux and most smart devices answer.</summary>
    public static async Task<string?> MdnsNameAsync(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        var q = new List<byte> { 0x12, 0x34, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (string label in new[] { Num(b[3]), Num(b[2]), Num(b[1]), Num(b[0]), "in-addr", "arpa" })
        {
            q.Add((byte)label.Length);
            q.AddRange(Encoding.ASCII.GetBytes(label));
        }
        q.AddRange(new byte[] { 0, 0, 12, 0, 1 });
        var d = await UdpQueryAsync(ip, 5353, q.ToArray(), 900).ConfigureAwait(false);
        if (d == null || d.Length < 12) return null;
        try
        {
            int qd = (d[4] << 8) | d[5], an = (d[6] << 8) | d[7];
            int p = 12;
            for (int i = 0; i < qd; i++)
            {
                ReadDnsName(d, ref p);
                p += 4;
            }
            for (int i = 0; i < an; i++)
            {
                ReadDnsName(d, ref p);
                int type = (d[p] << 8) | d[p + 1];
                int length = (d[p + 8] << 8) | d[p + 9];
                p += 10;
                if (type == 12)
                {
                    int r = p;
                    string name = ReadDnsName(d, ref r);
                    if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
                    return name.Length > 0 ? name : null;
                }
                p += length;
            }
        }
        catch (IndexOutOfRangeException)
        {
        }
        return null;
    }

    private static string Num(byte b) => b.ToString(CultureInfo.InvariantCulture);

    private static string ReadDnsName(byte[] d, ref int p)
    {
        var parts = new List<string>();
        int? resume = null;
        int jumps = 0;
        while (true)
        {
            int length = d[p];
            if (length == 0)
            {
                p++;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                resume ??= p + 2;
                p = ((length & 0x3F) << 8) | d[p + 1];
                if (++jumps > 16) break;
                continue;
            }
            parts.Add(Encoding.UTF8.GetString(d, p + 1, length));
            p += length + 1;
        }
        if (resume is int r) p = r;
        return string.Join(".", parts);
    }

    // ---- SMB ----

    /// <summary>
    /// Starts an SMB2 session (NTLM negotiate only, no credentials). The server's NTLM challenge contains its
    /// NetBIOS name, DNS name, domain and Windows version.
    /// </summary>
    public static async Task<SmbInfo?> SmbAsync(IPAddress ip)
    {
        using var cts = new CancellationTokenSource(3000);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(ip, 445, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();

            var negotiate = new byte[36 + 8];
            negotiate[0] = 36;
            negotiate[2] = 4; // dialect count
            negotiate[4] = 1; // signing enabled
            Guid.NewGuid().ToByteArray().CopyTo(negotiate, 12);
            ushort[] dialects = { 0x0202, 0x0210, 0x0300, 0x0302 };
            for (int i = 0; i < dialects.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(negotiate.AsSpan(36 + i * 2), dialects[i]);
            await SendSmbAsync(stream, 0, 0, negotiate, cts.Token).ConfigureAwait(false);
            var response = await ReadSmbAsync(stream, cts.Token).ConfigureAwait(false);
            if (response == null || BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(8)) != 0) return null;

            byte[] ntlm =
            {
                (byte)'N', (byte)'T', (byte)'L', (byte)'M', (byte)'S', (byte)'S', (byte)'P', 0,
                1, 0, 0, 0, 0x07, 0x82, 0x08, 0xE2,
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                10, 0, 0x61, 0x4A, 0, 0, 0, 15,
            };
            var setup = new byte[24 + ntlm.Length];
            setup[0] = 25;
            setup[3] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(12), 64 + 24);
            BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(14), (ushort)ntlm.Length);
            ntlm.CopyTo(setup, 24);
            await SendSmbAsync(stream, 1, 1, setup, cts.Token).ConfigureAwait(false);
            response = await ReadSmbAsync(stream, cts.Token).ConfigureAwait(false);
            return response == null ? null : ParseChallenge(response);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task SendSmbAsync(Stream stream, ushort command, ulong messageId, byte[] body, CancellationToken ct)
    {
        var packet = new byte[4 + 64 + body.Length];
        int length = 64 + body.Length;
        packet[1] = (byte)(length >> 16);
        packet[2] = (byte)(length >> 8);
        packet[3] = (byte)length;
        var h = packet.AsSpan(4);
        h[0] = 0xFE;
        h[1] = (byte)'S';
        h[2] = (byte)'M';
        h[3] = (byte)'B';
        h[4] = 64;
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], command);
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(h[24..], messageId);
        body.CopyTo(packet, 68);
        await stream.WriteAsync(packet, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadSmbAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = (header[1] << 16) | (header[2] << 8) | header[3];
        if (length < 64 || length > 1 << 16) return null;
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        return data[0] == 0xFE && data[1] == (byte)'S' ? data : null;
    }

    private static SmbInfo? ParseChallenge(byte[] d)
    {
        var signature = "NTLMSSP\0"u8;
        int n = d.AsSpan().IndexOf(signature);
        if (n < 0 || n + 56 > d.Length || d[n + 8] != 2) return null;
        int infoLength = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(n + 40));
        int infoOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(n + 44));
        int major = d[n + 48], minor = d[n + 49];
        int build = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(n + 50));

        string? name = null, dnsName = null, domain = null;
        int p = n + infoOffset, end = Math.Min(d.Length, p + infoLength);
        while (p + 4 <= end)
        {
            int id = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p));
            int length = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 2));
            if (id == 0 || p + 4 + length > end) break;
            string value = Encoding.Unicode.GetString(d, p + 4, length);
            switch (id)
            {
                case 1: name = value; break;
                case 2: domain = value; break;
                case 3: dnsName = value; break;
            }
            p += 4 + length;
        }
        string? os = major >= 5 && build > 0 ? $"Windows {major}.{minor}.{build}" : null;
        return new SmbInfo(name, dnsName, domain, os);
    }

    // ---- SNMP ----

    /// <summary>
    /// SNMP v2c GET of sysDescr, sysName and sysLocation with the community "public" (UDP 161). Printers, switches,
    /// NAS boxes and routers often answer; sysDescr usually contains the model and firmware.
    /// </summary>
    public static async Task<SnmpInfo?> SnmpAsync(IPAddress ip)
    {
        int[] objects = { 1, 5, 6 }; // 1.3.6.1.2.1.1.x.0: sysDescr, sysName, sysLocation
        var varbinds = new List<byte>();
        foreach (int o in objects)
        {
            byte[] oid = { 0x2B, 6, 1, 2, 1, 1, (byte)o, 0 };
            varbinds.AddRange(Tlv(0x30, Concat(Tlv(0x06, oid), new byte[] { 0x05, 0x00 })));
        }
        var pdu = Tlv(0xA0, Concat(new byte[] { 0x02, 0x01, 0x2A, 0x02, 0x01, 0x00, 0x02, 0x01, 0x00 }, Tlv(0x30, varbinds.ToArray())));
        var message = Tlv(0x30, Concat(new byte[] { 0x02, 0x01, 0x01 }, Tlv(0x04, "public"u8.ToArray()), pdu));

        var d = await UdpQueryAsync(ip, 161, message, 1200).ConfigureAwait(false);
        if (d == null) return null;
        try
        {
            var values = new Dictionary<int, string>();
            int p = 0;
            Enter(d, ref p, 0x30);          // message
            Skip(d, ref p);                 // version
            Skip(d, ref p);                 // community
            Enter(d, ref p, 0xA2);          // GetResponse
            Skip(d, ref p);                 // request id
            if (ReadInt(d, ref p) != 0) return null; // error status
            Skip(d, ref p);                 // error index
            int end = Enter(d, ref p, 0x30);
            while (p < end)
            {
                int vbEnd = Enter(d, ref p, 0x30);
                if (d[p] != 0x06) break;
                int oidLength = d[p + 1];
                int last = d[p + 2 + oidLength - 2];
                p += 2 + oidLength;
                if (d[p] == 0x04)
                {
                    int length = ReadLength(d, p + 1, out int header);
                    values[last] = Encoding.UTF8.GetString(d, p + 1 + header, length).Replace("\0", "", StringComparison.Ordinal).Trim();
                }
                p = vbEnd;
            }
            string? Get(int key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
            return values.Count == 0 ? null : new SnmpInfo(Get(5), Get(1), Get(6));
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException)
        {
            return null;
        }
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();

    private static byte[] Tlv(byte tag, byte[] value)
    {
        var length = value.Length < 128
            ? new[] { (byte)value.Length }
            : value.Length < 256 ? new byte[] { 0x81, (byte)value.Length } : new byte[] { 0x82, (byte)(value.Length >> 8), (byte)value.Length };
        return Concat(new[] { tag }, length, value);
    }

    private static int ReadLength(byte[] d, int p, out int header)
    {
        int first = d[p];
        if (first < 0x80)
        {
            header = 1;
            return first;
        }
        int bytes = first & 0x7F;
        if (bytes is < 1 or > 3) throw new InvalidDataException("Bad BER length.");
        int length = 0;
        for (int i = 1; i <= bytes; i++) length = (length << 8) | d[p + i];
        header = 1 + bytes;
        return length;
    }

    /// <summary>Steps into a constructed element and returns where it ends.</summary>
    private static int Enter(byte[] d, ref int p, byte tag)
    {
        if (d[p] != tag) throw new InvalidDataException("Unexpected BER tag.");
        int length = ReadLength(d, p + 1, out int header);
        p += 1 + header;
        return p + length;
    }

    private static void Skip(byte[] d, ref int p)
    {
        int length = ReadLength(d, p + 1, out int header);
        p += 1 + header + length;
    }

    private static int ReadInt(byte[] d, ref int p)
    {
        int length = ReadLength(d, p + 1, out int header);
        int value = 0;
        for (int i = 0; i < length; i++) value = (value << 8) | d[p + 1 + header + i];
        p += 1 + header + length;
        return value;
    }

    // ---- UPnP / SSDP ----

    /// <summary>Sends an SSDP M-SEARCH and collects the description URL of every UPnP device that answers.</summary>
    public static async Task<Dictionary<uint, string>> SsdpDiscoverAsync(IPAddress local, int listenMs, CancellationToken ct)
    {
        var found = new Dictionary<uint, string>();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            var request = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n");
            var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            await udp.SendAsync(request, target, ct).ConfigureAwait(false);
            await udp.SendAsync(request, target, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(listenMs);
            while (true)
            {
                var reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                uint key = ToUInt(reply.RemoteEndPoint.Address);
                if (found.ContainsKey(key)) continue;
                foreach (string line in Encoding.ASCII.GetString(reply.Buffer).Split("\r\n"))
                {
                    if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                    {
                        found[key] = line[9..].Trim();
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        return found;
    }

    public static async Task<UpnpInfo?> UpnpDescriptionAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp) return null;
        try
        {
            string xml = await Http.GetStringAsync(uri).ConfigureAwait(false);
            var device = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
            if (device == null) return null;
            string? Field(string name) => device.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
            return new UpnpInfo(Field("friendlyName"), Field("manufacturer"), Field("modelName"), Field("modelNumber"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    // ---- HTTP ----

    /// <summary>Title of the web page and the Server header, which often name the device model.</summary>
    public static async Task<(string? Title, string? Server)> HttpInfoAsync(IPAddress ip, int port)
    {
        try
        {
            using var response = await Http.GetAsync(new Uri($"http://{ip}:{port}/"), HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            string? server = response.Headers.Server.Count > 0 ? response.Headers.Server.ToString() : null;
            string? title = null;
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var buffer = new byte[65536];
                int total = 0, read;
                while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total)).ConfigureAwait(false)) > 0)
                    total += read;
                var match = TitleRegex.Match(Encoding.UTF8.GetString(buffer, 0, total));
                if (match.Success)
                {
                    title = Regex.Replace(WebUtility.HtmlDecode(match.Groups[1].Value), @"\s+", " ").Trim();
                    if (title.Length > 80) title = title[..80] + "…";
                    if (title.Length == 0) title = null;
                }
            }
            return (title, server);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return (null, null);
        }
    }
}
