// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Wific.Native;

namespace Wific.Wifi;

/// <summary>
/// Made-up networks for screenshots and demos (start wific with <c>--demo</c>). Network names are invented and
/// only the vendor prefixes of the MAC addresses are real, so nothing here describes a real place.
/// </summary>
public sealed class DemoScanner : IWifiScanner
{
    private sealed record Radio(
        string Ssid, int Offset, bool Laa, WifiBand Band, int Channel, int Width, int Center,
        string Standard, string Protocol, int Streams, double Rate,
        string Security, string Ciphers, string Pmf, int Rssi, int? Clients, int? Load, bool MultiLink = false);

    private sealed record Device(byte[] Mac, double UptimeHours, string Country, Radio[] Radios, string ApName = "", string WpsName = "");

    private static readonly Guid AdapterId = new("6d656f77-7769-6669-6364-656d6f000001");

    private static readonly Device[] Devices =
    {
        new(new byte[] { 0x04, 0xD9, 0xF5, 0x3A }, 79.4, "CZ", new Radio[]
        {
            new("Maple-Home", 0x10, false, WifiBand.Band2_4, 6, 20, 6, "Wi-Fi 7", "802.11be", 2, 688, "WPA2/WPA3-Personal", "CCMP", "Capable", -48, 6, 18),
            new("Maple-Guest", 0x11, true, WifiBand.Band2_4, 6, 20, 6, "Wi-Fi 7", "802.11be", 2, 688, "WPA2-Personal", "CCMP", "Capable", -49, 1, 18),
            new("Maple-Home", 0x14, false, WifiBand.Band5, 36, 160, 50, "Wi-Fi 7", "802.11be", 2, 2882, "WPA2/WPA3-Personal", "CCMP", "Capable", -44, 9, 12, true),
            new("Maple-Home", 0x18, false, WifiBand.Band6, 37, 320, 31, "Wi-Fi 7", "802.11be", 2, 5765, "WPA3-Personal", "GCMP-256, CCMP", "Required", -58, 2, 3, true),
        }, WpsName: "Home router"),
        new(new byte[] { 0x50, 0xC7, 0xBF, 0x8E }, 412.2, "CZ", new Radio[]
        {
            new("Kowalski", 0x02, false, WifiBand.Band2_4, 1, 20, 1, "Wi-Fi 6", "802.11ax", 2, 574, "WPA2-Personal", "CCMP", "No", -67, 4, 41),
            new("Kowalski_5G", 0x03, false, WifiBand.Band5, 100, 80, 106, "Wi-Fi 6", "802.11ax", 2, 1201, "WPA2-Personal", "CCMP", "No", -71, 3, 22),
        }),
        new(new byte[] { 0x24, 0x5A, 0x4C, 0x12 }, 696.0, "CZ", new Radio[]
        {
            new("Office-Net", 0x40, false, WifiBand.Band5, 149, 80, 155, "Wi-Fi 6", "802.11ax", 2, 1201, "WPA2-Enterprise", "CCMP", "Capable", -63, 14, 47),
            new("Office-Net", 0x38, false, WifiBand.Band2_4, 11, 20, 11, "Wi-Fi 6", "802.11ax", 2, 574, "WPA2-Enterprise", "CCMP", "Capable", -66, 8, 52),
        }, ApName: "AP-Lobby"),
        new(new byte[] { 0x78, 0x8A, 0x20, 0x44 }, 695.8, "CZ", new Radio[]
        {
            new("Office-Net", 0x90, false, WifiBand.Band5, 52, 80, 58, "Wi-Fi 6", "802.11ax", 2, 1201, "WPA2-Enterprise", "CCMP", "Capable", -78, 6, 20),
        }, ApName: "AP-Floor2"),
        new(new byte[] { 0x3C, 0xA6, 0x2F, 0x71 }, 51.0, "DE", new Radio[]
        {
            new("Garden Flat", 0x20, false, WifiBand.Band2_4, 6, 40, 8, "Wi-Fi 6", "802.11ax", 2, 574, "WPA2-Personal", "CCMP", "No", -72, 5, 63),
            new("Garden Flat", 0x21, false, WifiBand.Band5, 44, 80, 42, "Wi-Fi 6", "802.11ax", 2, 1201, "WPA2-Personal", "CCMP", "No", -80, 2, 15),
        }, WpsName: "ISP gateway"),
        new(new byte[] { 0x24, 0x0A, 0xC4, 0x5B }, 1300.0, "", new Radio[]
        {
            new("IoT-Hub", 0x01, false, WifiBand.Band2_4, 11, 20, 11, "Wi-Fi 4", "802.11n", 1, 72, "WPA2-Personal", "CCMP", "No", -70, 3, 30),
        }),
        new(new byte[] { 0x00, 0x14, 0x6C, 0x2E }, 2200.0, "", new Radio[]
        {
            new("OldRouter", 0x05, false, WifiBand.Band2_4, 3, 20, 3, "Legacy", "802.11g", 1, 54, "WPA-Personal", "TKIP", "No", -83, null, null),
        }),
        new(new byte[] { 0x48, 0x8F, 0x5A, 0x90 }, 700.5, "CZ", new Radio[]
        {
            new("", 0x30, false, WifiBand.Band5, 120, 40, 118, "Wi-Fi 5", "802.11ac", 2, 400, "WPA2-Personal", "CCMP", "No", -76, null, null),
            new("Workshop", 0x31, false, WifiBand.Band2_4, 1, 20, 1, "Wi-Fi 4", "802.11n", 2, 144, "WPA2-Personal", "CCMP", "No", -78, null, null),
        }),
        new(new byte[] { 0x0C, 0x8D, 0xDB, 0x6A }, 130.0, "CZ", new Radio[]
        {
            new("Cafe Corner", 0x50, false, WifiBand.Band2_4, 1, 20, 1, "Wi-Fi 6", "802.11ax", 2, 574, "Open", "", "No", -81, 11, 72),
            new("Cafe Corner", 0x52, false, WifiBand.Band5, 36, 40, 38, "Wi-Fi 6", "802.11ax", 2, 574, "Open", "", "No", -79, 7, 35),
        }),
        new(new byte[] { 0xA0, 0x40, 0xA0, 0xC3 }, 260.0, "CZ", new Radio[]
        {
            new("Novak-6E", 0x0A, false, WifiBand.Band6, 69, 160, 79, "Wi-Fi 6E", "802.11ax", 2, 2402, "WPA3-Personal", "CCMP", "Required", -74, 1, 4),
            new("Novak", 0x0B, false, WifiBand.Band5, 116, 80, 122, "Wi-Fi 6", "802.11ax", 2, 1201, "WPA2/WPA3-Personal", "CCMP", "Capable", -77, 2, 9),
        }),
        new(new byte[] { 0xAC, 0x84, 0xC6, 0x19 }, 18.3, "CZ", new Radio[]
        {
            new("Penthouse", 0x60, false, WifiBand.Band2_4, 11, 20, 11, "Wi-Fi 4", "802.11n", 2, 300, "WPA2-Personal", "CCMP", "No", -74, 2, 49),
            new("Penthouse", 0x61, false, WifiBand.Band5, 157, 80, 155, "Wi-Fi 5", "802.11ac", 2, 867, "WPA2-Personal", "CCMP", "No", -75, 3, 28),
        }),
    };

    private readonly DateTime _startUtc = DateTime.UtcNow;

    private static byte[] MacOf(Device d, Radio r)
    {
        var mac = new byte[] { d.Mac[0], d.Mac[1], d.Mac[2], d.Mac[3], 0x7C, (byte)r.Offset };
        if (r.Laa) mac[0] |= 0x02;
        return mac;
    }

    private static string Format(byte[] mac) => string.Join(":", mac.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    private static readonly (Device Device, Radio Radio) Connected = (Devices[0], Devices[0].Radios[2]);

    private int RssiOf(Radio r)
    {
        double t = (DateTime.UtcNow - _startUtc).TotalSeconds;
        double wave = 4 * Math.Sin(t / 23.0 + r.Offset + r.Channel) + 2 * Math.Sin(t / 7.0 + r.Channel * 0.3);
        return (int)Math.Round(r.Rssi + wave + (Random.Shared.NextDouble() - 0.5) * 2);
    }

    public IReadOnlyList<WlanInterface> GetInterfaces() =>
        new[] { new WlanInterface(AdapterId, "Demo Wi-Fi 7 adapter", 1) };

    public void TriggerScan(Guid iface)
    {
    }

    public ScanResult Read(Guid iface)
    {
        var now = DateTime.UtcNow;
        var result = new ScanResult { ConnectedBssid = Format(MacOf(Connected.Device, Connected.Radio)) };
        foreach (var d in Devices)
        {
            foreach (var r in d.Radios)
            {
                var mac = MacOf(d, r);
                int rssi = RssiOf(r);
                int frequency = Channels.ChannelToFrequency(r.Band, r.Channel);
                bool he = r.Protocol is "802.11ax" or "802.11be";
                result.Networks.Add(new BssInfo
                {
                    Ssid = r.Ssid,
                    IsHidden = r.Ssid.Length == 0,
                    Mac = mac,
                    Bssid = Format(mac),
                    Rssi = rssi,
                    LinkQuality = Math.Clamp(2 * (rssi + 100), 0, 100),
                    FrequencyMhz = frequency,
                    Band = r.Band,
                    Channel = r.Channel,
                    ChannelWidthMhz = r.Width,
                    CenterChannel = r.Center,
                    CenterFrequencyMhz = Channels.ChannelToFrequency(r.Band, r.Center),
                    Standard = r.Standard,
                    Protocol = r.Protocol,
                    SpatialStreams = r.Streams,
                    MaxRateMbps = r.Rate,
                    Security = r.Security,
                    Ciphers = r.Ciphers,
                    Pmf = r.Pmf,
                    StationCount = r.Clients,
                    ChannelUtilization = r.Load,
                    TsfMicroseconds = (ulong)(d.UptimeHours * 3600e6 + (now - _startUtc).TotalMilliseconds * 1000),
                    ObservedUtc = now,
                    BeaconIntervalTu = 100,
                    Country = d.Country,
                    BssColor = he ? 1 + (r.Offset * 7 + d.Mac[3]) % 63 : null,
                    MultiLink = r.MultiLink,
                    Roaming = he ? "802.11k/v" : "",
                    ApName = d.ApName,
                    WpsDeviceName = d.WpsName,
                });
            }
        }
        return result;
    }

    public (ConnectionSnapshot Connection, int? Rssi)? ReadConnection(Guid iface)
    {
        var (d, r) = Connected;
        var snapshot = new ConnectionSnapshot(
            r.Ssid, Encoding.UTF8.GetBytes(r.Ssid), MacOf(d, r),
            PhyType: 11, SignalQuality: 90, RxRateKbps: 2_401_000, TxRateKbps: 2_041_000,
            AuthAlgorithm: 9, CipherAlgorithm: 4);
        return (snapshot, RssiOf(r));
    }

    public void Dispose()
    {
    }
}
