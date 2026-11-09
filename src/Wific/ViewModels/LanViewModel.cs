// WiFic - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using Wific.Net;
using Wific.Wifi;

namespace Wific.ViewModels;

/// <summary>Finds and identifies the devices on the network this PC is connected to.</summary>
public sealed class LanViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Func<OuiDatabase> _oui;
    private readonly HashSet<uint> _known = new();
    private CancellationTokenSource? _cts;
    private int _scans;

    public LanViewModel(AppSettings settings, Func<OuiDatabase> oui)
    {
        _settings = settings;
        _oui = oui;
        View = CollectionViewSource.GetDefaultView(Devices);
        View.SortDescriptions.Add(new SortDescription(nameof(LanDevice.IpSort), ListSortDirection.Ascending));
        ScanCommand = new RelayCommand(() => _ = ScanAsync());
    }

    public ObservableCollection<LanDevice> Devices { get; } = new();
    public ICollectionView View { get; }
    public ICommand ScanCommand { get; }

    public bool HasScanned => _scans > 0 || IsScanning;

    private LanDevice? _selected;
    public LanDevice? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>Check the common ports and probe names, type and model (on by default). Off = ping and ARP only.</summary>
    public bool Identify
    {
        get => _settings.LanIdentify;
        set
        {
            if (_settings.LanIdentify == value) return;
            _settings.LanIdentify = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    private bool _scanning;
    public bool IsScanning
    {
        get => _scanning;
        set
        {
            if (!Set(ref _scanning, value)) return;
            OnPropertyChanged(nameof(ScanButtonText));
            OnPropertyChanged(nameof(HasScanned));
        }
    }

    public string ScanButtonText => IsScanning ? "Stop" : "Start";

    private string _range = "";

    /// <summary>What to scan: networks, ranges or addresses. Pre-filled with the network this PC is connected to.</summary>
    public string RangeText
    {
        get => _range;
        set => Set(ref _range, value ?? "");
    }

    /// <summary>Fills in the current network the first time the page is opened (or when the box was cleared). Does not scan.</summary>
    public async Task PrepareAsync()
    {
        if (App.IsDemo)
        {
            if (RangeText.Length == 0) RangeText = "192.168.50.0/24";
            NetworkText = "Connected to Wi-Fi · this PC 192.168.50.10 · gateway 192.168.50.1";
            return;
        }
        var net = await Task.Run(() => LanProbes.GetCurrentNetwork());
        ShowNetwork(net);
        if (string.IsNullOrWhiteSpace(RangeText) && net != null) RangeText = net.CidrText;
    }

    private void ShowNetwork(LanNetwork? net)
    {
        NetworkText = net == null
            ? "Not connected to a network"
            : $"Connected to {(net.IsWireless ? "Wi-Fi" : "network")} “{net.AdapterName}” · this PC {net.Local}"
              + (net.Gateway != null ? $" · gateway {net.Gateway}" : "")
              + (net.Limited ? $" · your network is larger than /22, the local {net.CidrText} is suggested" : "");
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    private string _status = "Finds every device on the network you are connected to: computers, phones, printers, TVs, cameras and smart home devices.";
    public string StatusText
    {
        get => _status;
        set => Set(ref _status, value);
    }

    private string _network = "";
    public string NetworkText
    {
        get => _network;
        set => Set(ref _network, value);
    }

    public string SummaryText
    {
        get
        {
            if (Devices.Count == 0) return "";
            int issues = Devices.Count(d => d.IssueSeverity >= Severity.Warning);
            string text = $"{Devices.Count} devices";
            if (issues > 0) text += $" · {issues} with security warnings";
            return text;
        }
    }

    /// <summary>Starts a scan, or stops the running one.</summary>
    public async Task ScanAsync()
    {
        if (IsScanning)
        {
            _cts?.Cancel();
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsScanning = true;
        Progress = 0;
        var started = DateTime.Now;
        string? cidr = null;
        try
        {
            if (App.IsDemo)
            {
                cidr = await Demo.RunAsync(this, ct);
            }
            else
            {
                cidr = await ScanNetworkAsync(ct);
            }
            if (cidr != null)
                StatusText = string.Create(CultureInfo.InvariantCulture,
                    $"Scan finished: {Devices.Count} devices on {cidr} in {(DateTime.Now - started).TotalSeconds:0} s. Select a device to see everything WiFic found out about it.");
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Scan stopped. {Devices.Count} devices found so far.";
        }
        finally
        {
            foreach (var d in Devices)
            {
                d.IsProbing = false;
                d.Refresh();
            }
            foreach (var d in Devices) _known.Add(d.IpSort);
            _scans++;
            IsScanning = false;
            Progress = 0;
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(ScanButtonText));
        }
    }

    private LanDevice AddDevice(IPAddress ip)
    {
        uint key = LanProbes.ToUInt(ip);
        var existing = Devices.FirstOrDefault(d => d.IpSort == key);
        if (existing != null) return existing;
        var device = new LanDevice(ip) { IsNew = _scans > 0 && !_known.Contains(key) };
        Devices.Add(device);
        OnPropertyChanged(nameof(SummaryText));
        return device;
    }

    private void SetMac(LanDevice d, byte[] mac)
    {
        d.Mac = mac;
        d.Vendor = (mac[0] & 0x02) != 0 ? "" : _oui().Lookup(mac) ?? "";
    }

    private async Task<string?> ScanNetworkAsync(CancellationToken ct)
    {
        var net = await Task.Run(() => LanProbes.GetCurrentNetwork(), ct);
        ShowNetwork(net);
        if (string.IsNullOrWhiteSpace(RangeText) && net != null) RangeText = net.CidrText;
        var targets = LanProbes.ParseTargets(RangeText, out string? error);
        if (targets == null)
        {
            StatusText = error ?? "Enter an address, a range or a network to scan.";
            return null;
        }
        var targetSet = new HashSet<uint>(targets.Select(LanProbes.ToUInt));
        string label = RangeText.Trim();

        Devices.Clear();
        Selected = null;
        StatusText = $"Looking for devices in {label} ({targets.Count:N0} addresses)…";

        if (net != null && targetSet.Contains(LanProbes.ToUInt(net.Local)))
        {
            var self = AddDevice(net.Local);
            self.IsSelf = true;
            if (net.LocalMac.Length == 6) SetMac(self, net.LocalMac);
        }

        var ssdp = net != null
            ? LanProbes.SsdpDiscoverAsync(net.Local, 3500, ct)
            : Task.FromResult(new Dictionary<uint, string>());

        // 1. Ping sweep. Pinging also makes Windows resolve the MAC of local hosts that ignore ping.
        int done = 0;
        using (var gate = new SemaphoreSlim(128))
        {
            var pings = targets.Where(ip => net == null || !ip.Equals(net.Local)).Select(async ip =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    int? ms = await LanProbes.PingAsync(ip, 800);
                    if (ms != null)
                    {
                        var d = AddDevice(ip);
                        d.PingMs = ms;
                        d.Refresh();
                    }
                }
                finally
                {
                    gate.Release();
                    done++;
                    Progress = 45.0 * done / Math.Max(1, targets.Count);
                }
            }).ToList();
            await Task.WhenAll(pings);
        }
        ct.ThrowIfCancellationRequested();

        // 2. ARP cache: MAC addresses (local network only), and local devices with a firewall that blocks ping.
        foreach (var (ip, mac) in await Task.Run(() => LanProbes.ReadArpTable(), ct))
        {
            if (!targetSet.Contains(ip) || (net != null && ip == LanProbes.ToUInt(net.Local))) continue;
            SetMac(AddDevice(LanProbes.FromUInt(ip)), mac);
        }
        if (net?.Gateway != null)
        {
            uint gw = LanProbes.ToUInt(net.Gateway);
            foreach (var d in Devices.Where(d => d.IpSort == gw)) d.IsGateway = true;
        }
        foreach (var d in Devices) d.Refresh();

        // 3. Identify each device: names, open ports, SMB, web page, UPnP.
        if (!Identify)
        {
            Progress = 100;
            return label;
        }
        StatusText = $"Identifying {Devices.Count} devices…";
        var found = Devices.ToList();
        int identified = 0;
        using (var gate = new SemaphoreSlim(16))
        {
            var jobs = found.Select(async d =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    await IdentifyAsync(d, ct);
                }
                finally
                {
                    gate.Release();
                    identified++;
                    Progress = 45 + 50.0 * identified / Math.Max(1, found.Count);
                    d.IsProbing = false;
                    d.Refresh();
                    OnPropertyChanged(nameof(SummaryText));
                }
            }).ToList();
            jobs.Add(ApplyUpnpAsync(ssdp));
            await Task.WhenAll(jobs);
        }
        Progress = 100;
        return label;
    }

    private async Task ApplyUpnpAsync(Task<Dictionary<uint, string>> ssdp)
    {
        var found = await ssdp;
        await Task.WhenAll(found.Select(async pair =>
        {
            var info = await LanProbes.UpnpDescriptionAsync(pair.Value);
            var d = Devices.FirstOrDefault(x => x.IpSort == pair.Key);
            if (info == null || d == null) return;
            d.Upnp = info;
            d.Refresh();
        }));
    }

    private static async Task IdentifyAsync(LanDevice d, CancellationToken ct)
    {
        var dns = LanProbes.ReverseDnsAsync(d.Ip);
        var netbios = LanProbes.NetBiosAsync(d.Ip);
        var mdns = LanProbes.MdnsNameAsync(d.Ip);
        var snmp = LanProbes.SnmpAsync(d.Ip);

        var checks = LanDevice.CommonPorts.Select(p => p.Port).Distinct()
            .Select(async port => (port, open: await LanProbes.IsPortOpenAsync(d.Ip, port, 900, ct))).ToList();
        foreach (var (port, open) in await Task.WhenAll(checks))
            if (open) d.OpenPorts.Add(port);
        d.PortsChecked = true;
        d.Refresh();

        d.DnsName = await dns;
        (d.NetBiosName, d.Workgroup) = await netbios;
        d.MdnsName = await mdns;
        d.Snmp = await snmp;
        d.Refresh();

        if (d.OpenPorts.Contains(445))
            d.Smb = await LanProbes.SmbAsync(d.Ip);
        int web = d.OpenPorts.Contains(80) ? 80 : d.OpenPorts.Contains(8080) ? 8080 : 0;
        if (web != 0)
            (d.HttpTitle, d.HttpServer) = await LanProbes.HttpInfoAsync(d.Ip, web);
    }

    // ---- Demo ----

    /// <summary>Invented devices for screenshots (--demo).</summary>
    private static class Demo
    {
        private sealed record Fake(int Host, string Mac, int? Ping, int[] Ports, string? Dns = null, string? NetBios = null,
            string? Group = null, string? Mdns = null, SmbInfo? Smb = null, UpnpInfo? Upnp = null, string? Title = null,
            string? Server = null, bool Self = false, bool Gateway = false, bool New = false, SnmpInfo? Snmp = null);

        private static readonly Fake[] Devices =
        {
            new(1, "64:D1:54:3A:7C:0F", 1, new[] { 22, 53, 80, 443 }, Dns: "maple-router.lan", Title: "RouterOS router configuration page", Gateway: true),
            new(2, "50:C7:BF:21:9E:04", 1, new[] { 22, 80 }, Dns: "core-switch.lan", Title: "Switch Login", Snmp: new SnmpInfo("core-switch", "24-Port Gigabit L2+ Managed Switch, firmware 2.1.4", "Hallway rack")),
            new(10, "8C:8D:28:41:22:9A", 0, new[] { 139, 445 }, NetBios: "WORKSTATION", Group: "WORKGROUP", Smb: new SmbInfo("WORKSTATION", "workstation", "WORKSTATION", "Windows 10.0.26100"), Self: true),
            new(11, "A4:BB:6D:10:5E:02", 2, new[] { 139, 445, 3389 }, Dns: "office-pc.lan", NetBios: "OFFICE-PC", Group: "WORKGROUP", Smb: new SmbInfo("OFFICE-PC", "office-pc", "WORKGROUP", "Windows 10.0.22631")),
            new(20, "00:11:32:8A:41:C0", 1, new[] { 22, 80, 139, 443, 445, 5000, 5001 }, NetBios: "NAS-STORAGE", Group: "WORKGROUP", Smb: new SmbInfo("NAS-STORAGE", "nas-storage", "WORKGROUP", null), Upnp: new UpnpInfo("NAS-STORAGE", "Synology", "DS923+", null), Title: "Synology DiskStation"),
            new(21, "30:24:A9:5C:10:7E", 4, new[] { 80, 443, 631, 9100 }, Mdns: "Office-Printer", Title: "Color LaserJet MFP – Embedded Web Server",
                Snmp: new SnmpInfo("Office-Printer", "HP ETHERNET MULTI-ENVIRONMENT", "Office")),
            new(30, "F4:F5:D8:66:01:3B", 6, new[] { 8008, 8009 }, Mdns: "Living-Room-TV"),
            new(31, "8C:79:F5:12:EE:40", 9, new[] { 80 }, Upnp: new UpnpInfo("[TV] Living Room", "Samsung Electronics", "QE55 Series", null)),
            new(40, "5A:1F:22:9C:03:B1", 38, new[] { 62078 }, Mdns: "Johns-iPhone"),
            new(41, "D6:4E:87:31:AA:05", null, Array.Empty<int>(), New: true),
            new(50, "24:0A:C4:5B:12:6E", 12, new[] { 80 }, Dns: "smart-plug-kitchen.lan", Title: "Smart Plug"),
            new(51, "00:17:88:6B:2F:91", 3, new[] { 80, 443 }, Upnp: new UpnpInfo("Hue Bridge", "Signify", "Philips hue bridge 2015", "BSB002")),
            new(60, "DC:A6:32:7F:00:14", 2, new[] { 22, 53, 80, 5900 }, Dns: "pihole.lan", Mdns: "pihole", Title: "Pi-hole Admin Console"),
            new(61, "EC:71:DB:20:4C:88", 5, new[] { 23, 80, 554 }, Dns: "camera-garage.lan", Title: "IP Camera"),
            new(70, "48:A6:B8:90:3D:21", 7, new[] { 1883 }, Upnp: new UpnpInfo("Kitchen", "Sonos, Inc.", "Sonos One", null)),
        };

        public static async Task<string> RunAsync(LanViewModel vm, CancellationToken ct)
        {
            vm.Devices.Clear();
            vm.NetworkText = "Connected to Wi-Fi · this PC 192.168.50.10 · gateway 192.168.50.1";
            vm.StatusText = "Looking for devices on 192.168.50.0/24…";
            for (int i = 0; i < Devices.Length; i++)
            {
                await Task.Delay(120, ct);
                var f = Devices[i];
                var d = vm.AddDevice(IPAddress.Parse($"192.168.50.{f.Host}"));
                vm.SetMac(d, f.Mac.Split(':').Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray());
                d.IsSelf = f.Self;
                d.IsGateway = f.Gateway;
                d.IsNew = f.New;
                d.PingMs = f.Ping;
                d.Refresh();
                vm.Progress = 50.0 * (i + 1) / Devices.Length;
            }
            vm.StatusText = $"Identifying {Devices.Length} devices…";
            for (int i = 0; i < Devices.Length; i++)
            {
                await Task.Delay(150, ct);
                var f = Devices[i];
                var d = vm.Devices.First(x => x.IpText == $"192.168.50.{f.Host}");
                foreach (int p in f.Ports) d.OpenPorts.Add(p);
                d.PortsChecked = true;
                d.DnsName = f.Dns;
                d.NetBiosName = f.NetBios;
                d.Workgroup = f.Group;
                d.MdnsName = f.Mdns;
                d.Smb = f.Smb;
                d.Upnp = f.Upnp;
                d.HttpTitle = f.Title;
                d.HttpServer = f.Server;
                d.Snmp = f.Snmp;
                d.IsProbing = false;
                d.Refresh();
                vm.Progress = 50 + 50.0 * (i + 1) / Devices.Length;
            }
            vm.Selected = vm.Devices.FirstOrDefault(x => x.IpText == "192.168.50.20");
            return "192.168.50.0/24";
        }
    }
}
