// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using Wific.Wifi;

namespace Wific.ViewModels;

public readonly record struct SignalSample(DateTime Time, int Rssi);

public sealed class AccessPointViewModel : ObservableObject
{
    private const int MaxHistory = 900;

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4F, 0x8C, 0xFF), Color.FromRgb(0x3D, 0xD6, 0x8C), Color.FromRgb(0xFF, 0xB0, 0x3B),
        Color.FromRgb(0xFF, 0x5C, 0x7A), Color.FromRgb(0xB0, 0x7C, 0xFF), Color.FromRgb(0x2E, 0xD3, 0xE6),
        Color.FromRgb(0xF2, 0x7B, 0x35), Color.FromRgb(0x9B, 0xE1, 0x5D), Color.FromRgb(0xFF, 0x7C, 0xE0),
        Color.FromRgb(0x5E, 0xA8, 0xFF), Color.FromRgb(0xF5, 0xD5, 0x47), Color.FromRgb(0x6F, 0xE3, 0xC1),
    };

    private BssInfo _info;
    private string _vendor = "";
    private bool _vendorDerived;
    private bool _isStale;
    private bool _isConnected;
    private DateTime _firstSeen;
    private DateTime _lastSeen;
    private TimeSpan? _uptimeBase;
    private DateTime _uptimeAt;
    private TimeSpan? _uptime;

    public AccessPointViewModel(BssInfo info, string vendor, bool vendorDerived, DateTime now)
    {
        _info = info;
        Bssid = info.Bssid;
        _vendor = vendor;
        _vendorDerived = vendorDerived;
        _firstSeen = now;

        int hash = 17;
        foreach (char c in info.Bssid) hash = unchecked(hash * 31 + c);
        Color = Palette[(int)((uint)hash % Palette.Length)];
        var brush = new SolidColorBrush(Color);
        brush.Freeze();
        Brush = brush;

        Update(info, vendor, vendorDerived, now);
    }

    public string Bssid { get; }
    public Color Color { get; }
    public Brush Brush { get; }
    public List<SignalSample> History { get; } = new();
    public BssInfo Info => _info;

    public string Ssid => _info.IsHidden ? "[Hidden network]" : _info.Ssid;
    public bool IsHidden => _info.IsHidden;
    public string Vendor => _vendor;
    public string VendorText => _vendorDerived ? _vendor + " (virtual BSSID)" : _vendor;

    public int Rssi => _info.Rssi;
    public int LinkQuality => _info.LinkQuality;
    public double SignalPercent => Math.Clamp((_info.Rssi + 100) * 100.0 / 70.0, 0, 100);

    public WifiBand Band => _info.Band;
    public string BandText => Channels.BandName(_info.Band);
    public int FrequencyMhz => _info.FrequencyMhz;
    public int Channel => _info.Channel;
    public int CenterChannel => _info.CenterChannel;
    public int CenterFrequencyMhz => _info.CenterFrequencyMhz;
    public int ChannelWidthMhz => _info.ChannelWidthMhz;
    public string WidthText => _info.Is80Plus80 ? "80+80 MHz" : $"{_info.ChannelWidthMhz} MHz";
    public string BandShort => _info.Band switch
    {
        WifiBand.Band2_4 => "2.4G",
        WifiBand.Band5 => "5G",
        _ => "6G",
    };
    public string ChannelText => $"{_info.Channel} · {BandShort}";
    public string CenterChannelText => _info.ChannelWidthMhz > 20 && _info.CenterChannel != _info.Channel
        ? $"{_info.Channel} (center {_info.CenterChannel})"
        : _info.Channel.ToString(CultureInfo.InvariantCulture);

    // ---- Analysis results (set by MainViewModel after every scan) ----

    private ApGroup? _group;
    private IReadOnlyList<Issue> _issues = Array.Empty<Issue>();

    public ApGroup? Group
    {
        get => _group;
        set => Set(ref _group, value);
    }

    public IReadOnlyList<Issue> Issues
    {
        get => _issues;
        set
        {
            _issues = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IssueSeverity));
            OnPropertyChanged(nameof(HasIssues));
            OnPropertyChanged(nameof(IssueTooltip));
            OnPropertyChanged(nameof(IssueSymbol));
        }
    }

    public Severity IssueSeverity => _issues.Count == 0 ? Severity.None : _issues.Max(i => i.Severity);
    public bool HasIssues => _issues.Count > 0;
    public string IssueSymbol => IssueSeverity == Severity.Info ? "i" : "!";
    public string? IssueTooltip => _issues.Count == 0 ? null : string.Join("\n", _issues.Select(i => "• " + i.Text));

    /// <summary>Estimated moment the AP radio started (now minus TSF uptime), used for grouping.</summary>
    public DateTime? BootTime => _uptimeBase is TimeSpan b ? _uptimeAt - b : null;

    public string Standard => _info.Standard;
    public string Protocol => _info.Protocol;
    public string StandardText => $"{_info.Standard} ({_info.Protocol.Replace("802.", "", StringComparison.Ordinal)})";
    public int SpatialStreams => _info.SpatialStreams;
    public double MaxRateMbps => _info.MaxRateMbps;
    public string MaxRateText => _info.MaxRateMbps >= 1000
        ? (_info.MaxRateMbps / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " Gbps"
        : _info.MaxRateMbps.ToString("0.#", CultureInfo.InvariantCulture) + " Mbps";

    public string Security => _info.Security;
    public string Ciphers => _info.Ciphers;
    public string Pmf => _info.Pmf;

    public int StationCountSort => _info.StationCount ?? -1;
    public string StationCountText => _info.StationCount?.ToString(CultureInfo.InvariantCulture) ?? "–";
    public int UtilizationSort => _info.ChannelUtilization ?? -1;
    public string UtilizationText => _info.ChannelUtilization is int u ? $"{u} %" : "–";

    public double UptimeSort => _uptime?.TotalSeconds ?? -1;
    public string UptimeText => _uptime is TimeSpan t ? FormatUptime(t) : "–";

    public DateTime FirstSeen => _firstSeen;
    public DateTime LastSeen => _lastSeen;
    public string LastSeenText => FormatAgo(DateTime.Now - _lastSeen);

    public string Country => _info.Country;
    public string Device
    {
        get
        {
            var parts = new List<string>();
            if (_info.ApName.Length > 0) parts.Add(_info.ApName);
            if (_info.WpsManufacturer.Length > 0) parts.Add(_info.WpsManufacturer);
            if (_info.WpsModel.Length > 0) parts.Add(_info.WpsModel);
            if (_info.WpsDeviceName.Length > 0 && !parts.Contains(_info.WpsDeviceName)) parts.Add(_info.WpsDeviceName);
            return string.Join(" · ", parts);
        }
    }

    public string BssColorText => _info.BssColor?.ToString(CultureInfo.InvariantCulture) ?? "–";
    public string BeaconText => $"{_info.BeaconIntervalTu} TU ({_info.BeaconIntervalTu * 1.024:0.#} ms)";
    public string Roaming => _info.Roaming.Length > 0 ? _info.Roaming : "–";
    public string MultiLinkText => _info.MultiLink ? "Yes (MLO)" : "No";
    public IReadOnlyList<InformationElement> Elements => _info.Elements;

    public bool IsStale
    {
        get => _isStale;
        set => Set(ref _isStale, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => Set(ref _isConnected, value);
    }

    public void Update(BssInfo info, string vendor, bool vendorDerived, DateTime now)
    {
        _info = info;
        _vendor = vendor;
        _vendorDerived = vendorDerived;

        DateTime observed = info.ObservedUtc?.ToLocalTime() ?? now;
        if (observed > now) observed = now;
        _lastSeen = observed;

        // TSF counts microseconds since the radio started beaconing. Reject implausible values (> 10 years).
        const ulong tenYearsUs = 10UL * 365 * 24 * 3600 * 1_000_000;
        if (info.TsfMicroseconds > 0 && info.TsfMicroseconds < tenYearsUs)
        {
            _uptimeBase = TimeSpan.FromTicks((long)info.TsfMicroseconds * 10);
            _uptimeAt = observed;
        }
        else
        {
            _uptimeBase = null;
        }

        History.Add(new SignalSample(now, info.Rssi));
        if (History.Count > MaxHistory) History.RemoveRange(0, History.Count - MaxHistory);

        RefreshTime(now);
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Re-evaluates time-dependent texts (uptime, last seen).</summary>
    public void RefreshTime(DateTime now)
    {
        _uptime = _uptimeBase is TimeSpan b ? b + (now - _uptimeAt) : null;
        OnPropertyChanged(nameof(UptimeText));
        OnPropertyChanged(nameof(UptimeSort));
        OnPropertyChanged(nameof(LastSeenText));
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalDays >= 1
            ? $"{(int)t.TotalDays}d {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private static string FormatAgo(TimeSpan t)
    {
        if (t.TotalSeconds < 2) return "now";
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds} s ago";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} min ago";
        return $"{(int)t.TotalHours} h ago";
    }
}
