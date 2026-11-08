// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Wific.Native;

namespace Wific.ViewModels;

public sealed record RoamEvent(DateTime Time, string From, string To);

/// <summary>Live state of the adapter's current Wi-Fi connection.</summary>
public sealed class ConnectionViewModel : ObservableObject
{
    private const int MaxSamples = 120;

    private bool _isConnected;
    private string _ssid = "";
    private string _bssid = "";
    private int _rssi;
    private string _rates = "";
    private string _security = "";
    private AccessPointViewModel? _accessPoint;
    private int _revision;

    public List<int> Samples { get; } = new();
    public List<RoamEvent> Roams { get; } = new();

    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public string Ssid { get => _ssid; private set => Set(ref _ssid, value); }
    public string Bssid { get => _bssid; private set => Set(ref _bssid, value); }
    public int Rssi { get => _rssi; private set => Set(ref _rssi, value); }
    public string Rates { get => _rates; private set => Set(ref _rates, value); }
    public string Security { get => _security; private set => Set(ref _security, value); }
    public int Revision { get => _revision; private set => Set(ref _revision, value); }

    public AccessPointViewModel? AccessPoint
    {
        get => _accessPoint;
        set
        {
            if (Set(ref _accessPoint, value)) OnPropertyChanged(nameof(LinkText));
        }
    }

    /// <summary>"5 GHz · ch 36 · 80 MHz · Wi-Fi 6 · Ubiquiti" taken from the scanned AP.</summary>
    public string LinkText
    {
        get
        {
            var ap = _accessPoint;
            if (ap == null) return "";
            return $"{ap.BandText} · ch {ap.Channel} · {ap.WidthText} · {ap.Standard} · {ap.Vendor}";
        }
    }

    public string RoamText
    {
        get
        {
            if (Roams.Count == 0) return "No roaming yet";
            var last = Roams[^1];
            return $"Roamed {Ago(DateTime.Now - last.Time)} ({Roams.Count}×)";
        }
    }

    public string RoamTooltip
    {
        get
        {
            if (Roams.Count == 0) return "wific records every change of access point (BSSID) while it is running.";
            var sb = new StringBuilder("Recent roaming events:");
            foreach (var r in Roams.AsEnumerable().Reverse().Take(8))
                sb.Append(CultureInfo.InvariantCulture, $"\n{r.Time:HH:mm:ss}  {r.From} → {r.To}");
            return sb.ToString();
        }
    }

    public void Update((ConnectionSnapshot Connection, int? Rssi)? data)
    {
        if (data is not { } d)
        {
            if (IsConnected)
            {
                IsConnected = false;
                Samples.Clear();
                Revision++;
            }
            return;
        }

        var c = d.Connection;
        string bssid = string.Join(":", c.Bssid.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        string ssid = c.Ssid.Length > 0 ? Encoding.UTF8.GetString(c.Ssid) : c.Profile;

        if (IsConnected && bssid != Bssid && ssid == Ssid && Bssid.Length > 0)
        {
            Roams.Add(new RoamEvent(DateTime.Now, Bssid, bssid));
            if (Roams.Count > 50) Roams.RemoveAt(0);
        }

        IsConnected = true;
        Ssid = ssid;
        Bssid = bssid;
        // Fall back to the signal quality (0-100 % ~ -100..-50 dBm) when the driver does not report RSSI.
        Rssi = d.Rssi ?? (c.SignalQuality / 2 - 100);
        Rates = $"↓ {FormatRate(c.RxRateKbps)}  ↑ {FormatRate(c.TxRateKbps)}";
        Security = AuthName(c.AuthAlgorithm);

        Samples.Add(Rssi);
        if (Samples.Count > MaxSamples) Samples.RemoveAt(0);
        OnPropertyChanged(nameof(RoamText));
        OnPropertyChanged(nameof(RoamTooltip));
        OnPropertyChanged(nameof(LinkText));
        Revision++;
    }

    private static string FormatRate(uint kbps)
    {
        double mbps = kbps / 1000.0;
        return mbps >= 1000
            ? (mbps / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " Gbps"
            : mbps.ToString("0", CultureInfo.InvariantCulture) + " Mbps";
    }

    private static string AuthName(int algorithm) => algorithm switch
    {
        1 => "Open",
        2 => "Shared key",
        3 => "WPA-Enterprise",
        4 => "WPA-Personal",
        6 => "WPA2-Enterprise",
        7 => "WPA2-Personal",
        8 => "WPA3-Enterprise 192",
        9 => "WPA3-Personal",
        10 => "OWE",
        11 => "WPA3-Enterprise",
        _ => "Secured",
    };

    private static string Ago(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds} s ago";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} min ago";
        return $"{(int)t.TotalHours} h ago";
    }
}
