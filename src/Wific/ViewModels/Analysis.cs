// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wific.Wifi;

namespace Wific.ViewModels;

public enum Severity
{
    None = 0,
    Info = 1,
    Warning = 2,
    Critical = 3,
}

public sealed record Issue(Severity Severity, string Text);

/// <summary>One row of the Issues tab.</summary>
public sealed record IssueRow(Severity Severity, AccessPointViewModel AccessPoint, string Text)
{
    public string SeverityText => Severity.ToString();
    public string Ssid => AccessPoint.Ssid;
    public string Bssid => AccessPoint.Bssid;
    public string ChannelText => AccessPoint.ChannelText;
}

/// <summary>A physical access point: all BSSIDs that most likely belong to the same device.</summary>
public sealed class ApGroup : ObservableObject
{
    private string _title = "";
    private string _summary = "";
    private string _vendor = "";
    private int _bestRssi = -100;
    private bool _isMine;

    public ApGroup(string key) => Key = key;

    public string Key { get; }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Vendor { get => _vendor; private set => Set(ref _vendor, value); }
    public int BestRssi { get => _bestRssi; private set => Set(ref _bestRssi, value); }
    public bool IsMine { get => _isMine; private set => Set(ref _isMine, value); }

    public void Update(IReadOnlyList<AccessPointViewModel> members)
    {
        var ssids = members.Where(m => !m.IsHidden).Select(m => m.Ssid).Distinct(StringComparer.Ordinal).ToList();
        int hidden = members.Count(m => m.IsHidden);
        string names = ssids.Count == 0 ? "Hidden networks" : string.Join(", ", ssids.Take(3)) + (ssids.Count > 3 ? $" +{ssids.Count - 3}" : "");
        if (hidden > 0 && ssids.Count > 0) names += $" +{hidden} hidden";
        Title = names;

        Vendor = members.Select(m => m.Vendor).GroupBy(v => v).OrderByDescending(g => g.Key is "Unknown" or "Locally administered" ? 0 : g.Count()).First().Key;
        var bands = members.Select(m => m.Band).Distinct().OrderBy(b => b).Select(b => b switch
        {
            WifiBand.Band2_4 => "2.4",
            WifiBand.Band5 => "5",
            _ => "6",
        });
        var uptime = members.Select(m => m.UptimeText).FirstOrDefault(t => t != "–") ?? "–";
        Summary = $"{Vendor} · {members.Count} BSSIDs · {string.Join(" / ", bands)} GHz · up {uptime}";
        BestRssi = members.Max(m => m.Rssi);
        IsMine = members.Any(m => m.IsConnected);
    }
}

public sealed record ChannelAdvice(WifiBand Band, int Channel, int WidthMhz, double LowMhz, double HighMhz, string Label, bool Dfs);

public static class Analysis
{
    // ---------------------------------------------------------------- grouping

    /// <summary>
    /// Clusters BSSIDs into physical access points. Two BSSIDs are considered the same device when their MAC
    /// addresses are nearly identical (same OUI body, close host part; locally administered variants allowed)
    /// and, when both are known, their radios booted at about the same time (from the beacon TSF).
    /// </summary>
    public static Dictionary<AccessPointViewModel, string> GroupAccessPoints(IReadOnlyList<AccessPointViewModel> aps)
    {
        int n = aps.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        var boot = aps.Select(a => a.BootTime).ToArray();
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (!SameDevice(aps[i].Info.Mac, aps[j].Info.Mac, boot[i], boot[j])) continue;
                int a = Find(i), b = Find(j);
                if (a != b) parent[b] = a;
            }
        }

        var result = new Dictionary<AccessPointViewModel, string>();
        foreach (var cluster in Enumerable.Range(0, n).GroupBy(Find))
        {
            string key = cluster.Select(i => aps[i].Bssid).OrderBy(s => s, StringComparer.Ordinal).First();
            foreach (int i in cluster) result[aps[i]] = key;
        }
        return result;
    }

    private static bool SameDevice(byte[] a, byte[] b, DateTime? bootA, DateTime? bootB)
    {
        if (a.Length < 6 || b.Length < 6) return false;
        if (a[1] != b[1] || a[2] != b[2] || a[3] != b[3]) return false;
        // First octet: allow differences only in the locally-administered / virtual-BSSID bits.
        if (((a[0] ^ b[0]) & 0xF1) != 0) return false;
        int hostA = (a[4] << 8) | a[5];
        int hostB = (b[4] << 8) | b[5];
        if (Math.Abs(hostA - hostB) > 48) return false;
        if (bootA is DateTime x && bootB is DateTime y && Math.Abs((x - y).TotalSeconds) > 300) return false;
        return true;
    }

    // ---------------------------------------------------------------- issues

    private static readonly HashSet<int> GoodChannels24 = new() { 1, 5, 6, 9, 11, 13 };

    public static List<Issue> DetectIssues(AccessPointViewModel ap, IReadOnlyList<AccessPointViewModel> all)
    {
        var issues = new List<Issue>();
        var info = ap.Info;
        string sec = info.Security;

        if (sec == "WEP")
            issues.Add(new Issue(Severity.Critical, "WEP encryption can be cracked in minutes."));
        else if (sec == "WPA-Personal")
            issues.Add(new Issue(Severity.Critical, "WPA (TKIP) is deprecated and insecure."));
        else if (sec == "Open")
            issues.Add(new Issue(Severity.Warning, "Open network: traffic is not encrypted."));
        else if (info.Ciphers.Contains("TKIP", StringComparison.Ordinal))
            issues.Add(new Issue(Severity.Warning, "TKIP is still enabled; it slows clients down to 54 Mbps."));

        if (info.Band == WifiBand.Band2_4)
        {
            if (!GoodChannels24.Contains(info.Channel))
                issues.Add(new Issue(Severity.Warning, $"Channel {info.Channel} overlaps neighbours; use 1, 6 or 11."));
            if (info.ChannelWidthMhz >= 40)
                issues.Add(new Issue(Severity.Warning, "40 MHz in 2.4 GHz occupies most of the band."));
        }

        if (info.ChannelUtilization is int util && util >= 70)
            issues.Add(new Issue(Severity.Info, $"Channel is busy: {util} % airtime in use."));

        if (info.Standard == "Legacy")
            issues.Add(new Issue(Severity.Info, $"Legacy {info.Protocol} only; slows down the whole channel."));

        if (!ap.IsHidden)
        {
            var twin = all.FirstOrDefault(o => !ReferenceEquals(o, ap) && !o.IsStale && o.Ssid == ap.Ssid
                                               && o.Security != sec && !ReferenceEquals(o.Group, ap.Group)
                                               && (o.Security == "Open" || sec == "Open" || o.Security == "WEP" || sec == "WEP"
                                                   || o.Security.Contains("Enterprise", StringComparison.Ordinal) != sec.Contains("Enterprise", StringComparison.Ordinal)));
            if (twin != null)
                issues.Add(new Issue(Severity.Warning, $"Possible evil twin: the same SSID is broadcast as \"{twin.Security}\" by {twin.Bssid}."));
        }

        return issues;
    }

    // ---------------------------------------------------------------- channel advice

    private static readonly string[] FullSixGhzCountries = { "US", "CA", "KR", "BR", "AR", "CL", "CO", "SA", "TW", "PE", "CR" };

    /// <summary>
    /// Picks the least congested channel for a new or re-configured AP in the band. Interference is the
    /// received power of every other network overlapping the candidate, weighted by its channel load.
    /// BSSIDs of the AP you are connected to are ignored (that is the AP you would move).
    /// </summary>
    public static ChannelAdvice? AdviseChannel(WifiBand band, IReadOnlyList<AccessPointViewModel> aps)
    {
        var mine = aps.FirstOrDefault(a => a.IsConnected)?.Group;
        var others = aps.Where(a => a.Band == band && !a.IsStale && (mine == null || !ReferenceEquals(a.Group, mine))).ToList();

        var candidates = new List<(int Channel, int Width, int CenterChannel, string Label, bool Dfs)>();
        if (band == WifiBand.Band2_4)
        {
            candidates.Add((1, 20, 1, "1", false));
            candidates.Add((6, 20, 6, "6", false));
            candidates.Add((11, 20, 11, "11", false));
        }
        else if (band == WifiBand.Band5)
        {
            candidates.Add((36, 80, 42, "36–48", false));
            candidates.Add((149, 80, 155, "149–161", false));
            candidates.Add((52, 80, 58, "52–64", true));
            candidates.Add((100, 80, 106, "100–112", true));
            candidates.Add((116, 80, 122, "116–128", true));
            candidates.Add((132, 80, 138, "132–144", true));
        }
        else
        {
            bool full = aps.Any(a => FullSixGhzCountries.Contains(a.Country));
            int maxCenter = full ? 215 : 87;
            for (int center = 7; center <= maxCenter; center += 16)
                candidates.Add((center - 6, 80, center, $"{center - 6}–{center + 6}", false));
        }

        ChannelAdvice? best = null;
        double bestScore = double.MaxValue;
        foreach (var c in candidates)
        {
            double centerMhz = Channels.ChannelToFrequency(band, c.CenterChannel);
            double lo = centerMhz - c.Width / 2.0, hi = centerMhz + c.Width / 2.0;
            double score = 0;
            foreach (var ap in others)
            {
                double half = (band == WifiBand.Band2_4 ? Math.Max(22, ap.ChannelWidthMhz) : ap.ChannelWidthMhz) / 2.0;
                double apLo = ap.CenterFrequencyMhz - half, apHi = ap.CenterFrequencyMhz + half;
                double overlap = Math.Min(hi, apHi) - Math.Max(lo, apLo);
                if (overlap <= 0) continue;
                double power = Math.Pow(10, ap.Rssi / 10.0);
                double load = 1 + (ap.Info.ChannelUtilization ?? 20) / 100.0;
                score += power * load * (overlap / (hi - lo));
            }
            if (c.Dfs) score = score * 1.15 + 1e-12; // small preference for non-DFS channels
            if (score < bestScore - 1e-15)
            {
                bestScore = score;
                string label = band == WifiBand.Band2_4
                    ? $"channel {c.Label}"
                    : $"channels {c.Label} ({c.Width} MHz{(c.Dfs ? ", DFS" : "")})";
                best = new ChannelAdvice(band, c.Channel, c.Width, lo, hi, label, c.Dfs);
            }
        }
        return best;
    }

    public static string FormatAdvice(ChannelAdvice? advice) => advice == null
        ? "No recommendation"
        : string.Create(CultureInfo.InvariantCulture, $"Best: {advice.Label}");
}
