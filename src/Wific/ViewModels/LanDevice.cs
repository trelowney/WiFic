// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Wific.Net;

namespace Wific.ViewModels;

public sealed record LanPort(int Port, string Name, string? Url);

public sealed record LanDetail(string Label, string Value);

/// <summary>One device found on the local network.</summary>
public sealed class LanDevice : ObservableObject
{
    public static readonly (int Port, string Name)[] CommonPorts =
    {
        (21, "FTP"), (22, "SSH"), (23, "Telnet"), (53, "DNS"), (80, "HTTP"), (139, "NetBIOS"), (443, "HTTPS"),
        (445, "SMB"), (515, "LPD"), (554, "RTSP"), (631, "IPP"), (1883, "MQTT"), (3389, "RDP"), (5000, "Web"),
        (5001, "Web (TLS)"), (5900, "VNC"), (8008, "Cast"), (8009, "Cast"), (8080, "Web"), (8443, "Web (TLS)"),
        (9100, "Printer"), (62078, "Apple sync"),
    };

    public LanDevice(IPAddress ip)
    {
        Ip = ip;
        IpSort = LanProbes.ToUInt(ip);
        FirstSeen = DateTime.Now;
    }

    public IPAddress Ip { get; }
    public string IpText => Ip.ToString();
    public uint IpSort { get; }
    public DateTime FirstSeen { get; }

    // Raw facts, filled in by the probes.
    public byte[]? Mac { get; set; }
    public string Vendor { get; set; } = "";
    public bool IsGateway { get; set; }
    public bool IsSelf { get; set; }
    public bool IsNew { get; set; }
    public int? PingMs { get; set; }
    public string? DnsName { get; set; }
    public string? NetBiosName { get; set; }
    public string? Workgroup { get; set; }
    public string? MdnsName { get; set; }
    public SmbInfo? Smb { get; set; }
    public UpnpInfo? Upnp { get; set; }
    public string? HttpTitle { get; set; }
    public string? HttpServer { get; set; }
    public SortedSet<int> OpenPorts { get; } = new();
    public bool PortsChecked { get; set; }

    private bool _probing = true;
    public bool IsProbing
    {
        get => _probing;
        set => Set(ref _probing, value);
    }

    public bool IsRandomMac => Mac is { Length: 6 } m && (m[0] & 0x02) != 0;

    public string MacText => Mac is { Length: 6 } m
        ? string.Join(":", m.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))
        : "–";

    public string VendorText => IsRandomMac ? "Private (random) MAC" : Vendor.Length > 0 ? Vendor : Mac == null ? "–" : "Unknown";

    public string PingText => PingMs is int ms ? (ms < 1 ? "<1 ms" : $"{ms} ms") : "–";
    public int PingSort => PingMs ?? int.MaxValue;

    private static string ShortHost(string name)
    {
        // Strip the local DNS suffix (router.lan, nas.home, pc.fritz.box …), but keep IP-like names whole.
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && !char.IsDigit(name[0]) ? name[..dot] : name;
    }

    /// <summary>The most readable name from all sources.</summary>
    public string Name
    {
        get
        {
            if (Upnp?.FriendlyName is { Length: > 0 } upnp) return upnp;
            if (NetBiosName is { Length: > 0 } nb) return nb;
            if (Smb?.Name is { Length: > 0 } smb) return smb;
            if (MdnsName is { Length: > 0 } mdns) return mdns;
            if (DnsName is { Length: > 0 } dns) return ShortHost(dns);
            if (HttpTitle is { Length: > 0 } title) return title;
            if (IsSelf) return Environment.MachineName;
            return "";
        }
    }

    public string NameText => Name.Length > 0 ? Name : IsProbing ? "…" : "(no name)";

    /// <summary>Model, operating system or web page title: the second most useful line about the device.</summary>
    public string InfoText
    {
        get
        {
            var parts = new List<string>();
            if (Upnp != null)
            {
                string model = string.Join(" ", new[] { Upnp.ModelName, Upnp.ModelNumber }
                    .Where(s => !string.IsNullOrEmpty(s)).Distinct());
                if (model.Length > 0) parts.Add(Upnp.Manufacturer is { Length: > 0 } m && !model.StartsWith(m, StringComparison.OrdinalIgnoreCase) ? $"{m} {model}" : model);
            }
            if (Smb?.Os is { Length: > 0 } os) parts.Add(os);
            else if (Smb != null && parts.Count == 0) parts.Add("File server (SMB)");
            string? group = Smb?.Domain ?? Workgroup;
            if (group is { Length: > 0 } && !group.Equals("WORKGROUP", StringComparison.OrdinalIgnoreCase)
                && !group.Equals(Name, StringComparison.OrdinalIgnoreCase))
                parts.Add(group);
            if (HttpTitle is { Length: > 0 } title && title != Name && !parts.Contains(title)) parts.Add(title);
            else if (parts.Count == 0 && HttpServer is { Length: > 0 } server) parts.Add(server);
            return string.Join(" · ", parts);
        }
    }

    public IReadOnlyList<LanPort> Services => OpenPorts.Select(p =>
    {
        string name = CommonPorts.FirstOrDefault(c => c.Port == p).Name ?? p.ToString(CultureInfo.InvariantCulture);
        string? url = p switch
        {
            80 => $"http://{Ip}/",
            443 => $"https://{Ip}/",
            5000 or 8008 or 8080 => $"http://{Ip}:{p}/",
            5001 or 8443 => $"https://{Ip}:{p}/",
            _ => null,
        };
        return new LanPort(p, name, url);
    }).ToList();

    public string ServicesText
    {
        get
        {
            if (OpenPorts.Count > 0)
            {
                var names = Services.Select(s => s.Name).Distinct().ToList();
                if (names.Contains("SMB")) names.Remove("NetBIOS");
                return string.Join(" · ", names);
            }
            return PortsChecked ? "none" : IsProbing ? "…" : "–";
        }
    }

    public IReadOnlyList<LanPort> WebLinks => Services.Where(s => s.Url != null).ToList();

    // ---- Type ----

    public string TypeText => Classify();

    private string Classify()
    {
        if (IsSelf) return "This PC";
        if (IsGateway) return "Router / gateway";
        string text = string.Join(" ", new[]
        {
            Name, Upnp?.Manufacturer, Upnp?.ModelName, Upnp?.ModelNumber, HttpTitle, HttpServer, MdnsName, DnsName, Vendor,
        }.Where(s => !string.IsNullOrEmpty(s))).ToLowerInvariant();
        bool Has(params string[] words) => words.Any(w => text.Contains(w, StringComparison.Ordinal));
        bool Port(params int[] ports) => ports.Any(OpenPorts.Contains);

        if (Port(9100, 631, 515) || Has("printer", "laserjet", "officejet", "deskjet", "epson", "brother", "kyocera", "xerox", "ricoh", "lexmark"))
            return "Printer";
        if (Port(554) || Has("camera", "ipcam", "hikvision", "dahua", "reolink", "nvr", "doorbell"))
            return "Camera / NVR";
        if (Port(62078) || Has("iphone", "ipad")) return "iPhone / iPad";
        if (Port(8008, 8009) || Has("chromecast", "google tv", "android tv", "nvidia shield")) return "Chromecast / TV";
        if (Has("[tv]", " tv", "bravia", "webos", "tizen", "roku", "fire tv", "television")) return "TV / media player";
        if (Has("synology", "qnap", "diskstation", "truenas", "unraid", "asustor") || Regex.IsMatch(text, @"\bnas\b") || (Port(5000, 5001) && Port(445)))
            return "NAS";
        if (Has("playstation", "xbox", "nintendo")) return "Game console";
        if (Has("sonos", "denon", "heos", "bose", "yamaha", "speaker")) return "Speaker / audio";
        if (Has("raspberry")) return "Raspberry Pi";
        if (Has("hue", "philips lighting", "signify", "espressif", "tuya", "shelly", "allterco", "itead", "sonoff", "tasmota"))
            return "Smart home / IoT";
        if (Port(3389) || Smb?.Os is { Length: > 0 }) return "Windows PC";
        if (Has("apple")) return "Apple device";
        if (Port(445, 139) || NetBiosName != null) return "Computer";
        if (Port(22)) return "Linux / server";
        if (IsRandomMac) return "Phone / laptop";
        if (Upnp != null) return "UPnP device";
        return IsProbing ? "…" : "Unknown";
    }

    // ---- Issues ----

    public IReadOnlyList<Issue> Issues
    {
        get
        {
            var list = new List<Issue>();
            if (IsSelf) return list;
            if (OpenPorts.Contains(23)) list.Add(new Issue(Severity.Critical, "Telnet is open: logins travel unencrypted. Turn it off in the device settings."));
            if (OpenPorts.Contains(21)) list.Add(new Issue(Severity.Warning, "FTP is open: files and passwords travel unencrypted."));
            if (OpenPorts.Contains(5900)) list.Add(new Issue(Severity.Warning, "VNC remote control is open: make sure it has a strong password."));
            if (OpenPorts.Contains(3389)) list.Add(new Issue(Severity.Info, "Remote Desktop is enabled on this device."));
            if (OpenPorts.Contains(1883)) list.Add(new Issue(Severity.Info, "MQTT broker without TLS (port 1883)."));
            if (IsNew) list.Add(new Issue(Severity.Info, "New device: not seen in the previous scan."));
            return list;
        }
    }

    public bool HasIssues => Issues.Count > 0;
    public Severity IssueSeverity => Issues.Count == 0 ? Severity.None : Issues.Max(i => i.Severity);
    public string IssueSymbol => IssueSeverity == Severity.Info ? "i" : "!";
    public string IssueTooltip => string.Join("\n", Issues.Select(i => "• " + i.Text));

    // ---- Details ----

    public IReadOnlyList<LanDetail> Details
    {
        get
        {
            var list = new List<LanDetail>();
            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) list.Add(new LanDetail(label, value));
            }
            Add("IP address", IpText + (IsGateway ? "  (default gateway)" : IsSelf ? "  (this PC)" : ""));
            Add("MAC address", Mac == null ? null : MacText);
            Add("Vendor", IsRandomMac ? "Randomized MAC: phones and laptops hide their real address on Wi-Fi" : Vendor);
            Add("Type", TypeText);
            Add("Response time", PingMs == null ? "no reply to ping (found in the ARP cache)" : PingText);
            Add("DNS name", DnsName);
            Add("NetBIOS name", NetBiosName is null ? null : Workgroup is null ? NetBiosName : $"{NetBiosName}  ·  workgroup {Workgroup}");
            Add("mDNS name", MdnsName is null ? null : MdnsName + ".local");
            if (Smb != null)
                Add("SMB", string.Join("  ·  ", new[] { Smb.DnsName ?? Smb.Name, Smb.Domain, Smb.Os }.Where(s => !string.IsNullOrEmpty(s))));
            if (Upnp != null)
                Add("UPnP", string.Join("  ·  ", new[] { Upnp.FriendlyName, Upnp.Manufacturer, Upnp.ModelName, Upnp.ModelNumber }.Where(s => !string.IsNullOrEmpty(s))));
            Add("Web page", HttpTitle);
            Add("Web server", HttpServer);
            Add("Open ports", OpenPorts.Count == 0 ? (PortsChecked ? "none of the common ports" : "not checked")
                : string.Join(", ", Services.Select(s => $"{s.Port} {s.Name}")));
            Add("First seen", FirstSeen.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            return list;
        }
    }

    /// <summary>Tells the UI that the computed texts changed after a probe finished.</summary>
    public void Refresh()
    {
        foreach (string name in new[]
                 {
                     nameof(Name), nameof(NameText), nameof(InfoText), nameof(TypeText), nameof(VendorText), nameof(MacText),
                     nameof(PingText), nameof(PingSort), nameof(ServicesText), nameof(Services), nameof(WebLinks), nameof(Issues),
                     nameof(HasIssues), nameof(IssueSeverity), nameof(IssueSymbol), nameof(IssueTooltip), nameof(Details),
                 })
            OnPropertyChanged(name);
    }
}
