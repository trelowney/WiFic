// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wific.Native;

namespace Wific.Wifi;

/// <summary>Decodes a raw BSS entry and its 802.11 information elements into a <see cref="BssInfo"/>.</summary>
internal static class IeParser
{
    private static readonly Dictionary<int, string> ElementNames = new()
    {
        [0] = "SSID", [1] = "Supported Rates", [3] = "DS Parameter Set", [5] = "TIM", [7] = "Country",
        [11] = "BSS Load", [32] = "Power Constraint", [35] = "TPC Report", [37] = "Channel Switch Announcement",
        [42] = "ERP Information", [45] = "HT Capabilities", [48] = "RSN", [50] = "Extended Supported Rates",
        [54] = "Mobility Domain (802.11r)", [59] = "Supported Operating Classes", [61] = "HT Operation",
        [70] = "RM Enabled Capabilities (802.11k)", [71] = "Multiple BSSID", [74] = "Overlapping BSS Scan Params",
        [107] = "Interworking", [111] = "Roaming Consortium", [113] = "Mesh Configuration", [114] = "Mesh ID",
        [127] = "Extended Capabilities", [133] = "Cisco CCX (AP name)", [191] = "VHT Capabilities",
        [192] = "VHT Operation", [195] = "VHT Transmit Power Envelope", [201] = "Reduced Neighbor Report",
        [221] = "Vendor Specific",
    };

    private static readonly Dictionary<int, string> ExtensionNames = new()
    {
        [35] = "HE Capabilities", [36] = "HE Operation", [38] = "MU EDCA Parameter Set",
        [39] = "Spatial Reuse Parameter Set", [59] = "HE 6 GHz Band Capabilities", [106] = "EHT Operation",
        [107] = "Multi-Link", [108] = "EHT Capabilities",
    };

    // Max PHY rate per spatial stream (Mbps, shortest guard interval) indexed by width 20/40/80/160/320.
    private static readonly double[] HtRates = { 72.2, 150, 150, 150, 150 };
    private static readonly double[] VhtRates = { 86.7, 200, 433.3, 866.7, 866.7 };
    private static readonly double[] HeRates = { 143.4, 286.8, 600.5, 1201, 1201 };
    private static readonly double[] EhtRates = { 172.1, 344.1, 720.6, 1441.2, 2882.4 };

    public static BssInfo Parse(RawBssEntry raw)
    {
        var info = new BssInfo
        {
            Mac = raw.Bssid,
            Bssid = string.Join(":", raw.Bssid.Select(b => b.ToString("X2"))),
            Rssi = raw.Rssi,
            LinkQuality = raw.LinkQuality,
            TsfMicroseconds = raw.Tsf,
            BeaconIntervalTu = raw.BeaconPeriod,
            FrequencyMhz = (int)(raw.CenterFrequencyKhz / 1000),
        };

        info.ObservedUtc = HostTimestampToUtc(raw.HostTimestamp);
        info.Band = Channels.BandFromFrequency(info.FrequencyMhz);
        info.Channel = Channels.FrequencyToChannel(info.FrequencyMhz);
        info.CenterFrequencyMhz = info.FrequencyMhz;
        info.CenterChannel = info.Channel;

        info.Ssid = DecodeSsid(raw.Ssid);
        info.IsHidden = info.Ssid.Length == 0;

        var state = new ParseState();
        ReadElements(raw.InformationElements, info, state);

        ApplyChannelWidth(info, state);
        ApplyStandard(info, state, raw.Rates);
        ApplySecurity(info, state, raw.Capability);
        return info;
    }

    private static DateTime? HostTimestampToUtc(long fileTime)
    {
        if (fileTime <= 0) return null;
        try
        {
            var dt = DateTime.FromFileTimeUtc(fileTime);
            // Accept only plausible values; some drivers report a relative counter instead.
            if (dt.Year < 2015 || dt > DateTime.UtcNow.AddMinutes(5)) return null;
            return dt;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string DecodeSsid(byte[] ssid)
    {
        if (ssid.Length == 0 || ssid.All(b => b == 0)) return "";
        try
        {
            return new UTF8Encoding(false, true).GetString(ssid);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(ssid);
        }
    }

    private sealed class ParseState
    {
        public bool HasHt, HasVht, HasHe, HasEht;
        public byte[]? HtOperation, VhtOperation, HeOperation, EhtOperation;
        public int HtStreams, VhtStreams, HeStreams;
        public byte[]? Rsn;
        public bool HasWpa1;
        public bool Has11k, Has11r, Has11v;
    }

    private static void ReadElements(byte[] ies, BssInfo info, ParseState st)
    {
        int pos = 0;
        while (pos + 2 <= ies.Length)
        {
            int id = ies[pos];
            int len = ies[pos + 1];
            if (pos + 2 + len > ies.Length) break;
            var data = new byte[len];
            Array.Copy(ies, pos + 2, data, 0, len);
            pos += 2 + len;

            if (id == 255 && len >= 1)
            {
                int ext = data[0];
                var body = data.AsSpan(1).ToArray();
                info.Elements.Add(new InformationElement(id, ext,
                    ExtensionNames.TryGetValue(ext, out var en) ? en : "Extension", body));
                HandleExtension(ext, body, info, st);
                continue;
            }

            info.Elements.Add(new InformationElement(id, null,
                ElementNames.TryGetValue(id, out var n) ? n : "Unknown", data));
            HandleElement(id, data, info, st);
        }
    }

    private static void HandleElement(int id, byte[] d, BssInfo info, ParseState st)
    {
        switch (id)
        {
            case 0:
                if (info.Ssid.Length == 0 && d.Length > 0 && !d.All(b => b == 0))
                {
                    info.Ssid = DecodeSsid(d);
                    info.IsHidden = info.Ssid.Length == 0;
                }
                break;
            case 7:
                if (d.Length >= 2 && d[0] >= 0x20 && d[0] < 0x7F && d[1] >= 0x20 && d[1] < 0x7F)
                    info.Country = Encoding.ASCII.GetString(d, 0, 2).Trim();
                break;
            case 11:
                if (d.Length >= 3)
                {
                    info.StationCount = d[0] | (d[1] << 8);
                    info.ChannelUtilization = (int)Math.Round(d[2] * 100.0 / 255.0);
                }
                break;
            case 45:
                st.HasHt = true;
                if (d.Length >= 7)
                    st.HtStreams = Enumerable.Range(3, 4).Count(i => d[i] != 0);
                break;
            case 48:
                st.Rsn = d;
                break;
            case 54:
                st.Has11r = true;
                break;
            case 61:
                st.HtOperation = d;
                break;
            case 70:
                st.Has11k = true;
                break;
            case 127:
                // Extended Capabilities bit 19 = BSS Transition (802.11v)
                if (d.Length >= 3 && (d[2] & 0x08) != 0) st.Has11v = true;
                break;
            case 133:
                if (d.Length >= 26)
                    info.ApName = ReadAsciiZ(d, 10, 16);
                break;
            case 191:
                st.HasVht = true;
                if (d.Length >= 6) st.VhtStreams = CountStreams(d[4], d[5]);
                break;
            case 192:
                st.VhtOperation = d;
                break;
            case 221:
                HandleVendor(d, info, st);
                break;
        }
    }

    private static void HandleExtension(int ext, byte[] d, BssInfo info, ParseState st)
    {
        switch (ext)
        {
            case 35:
                st.HasHe = true;
                if (d.Length >= 19) st.HeStreams = CountStreams(d[17], d[18]);
                break;
            case 36:
                st.HeOperation = d;
                if (d.Length >= 4 && (d[3] & 0x80) == 0) info.BssColor = d[3] & 0x3F;
                break;
            case 106:
                st.EhtOperation = d;
                break;
            case 107:
                info.MultiLink = true;
                break;
            case 108:
                st.HasEht = true;
                break;
        }
    }

    private static void HandleVendor(byte[] d, BssInfo info, ParseState st)
    {
        if (d.Length < 4) return;
        bool microsoft = d[0] == 0x00 && d[1] == 0x50 && d[2] == 0xF2;
        if (microsoft && d[3] == 1) st.HasWpa1 = true;
        if (microsoft && d[3] == 4) ParseWps(d, 4, info);
    }

    private static void ParseWps(byte[] d, int pos, BssInfo info)
    {
        while (pos + 4 <= d.Length)
        {
            int type = (d[pos] << 8) | d[pos + 1];
            int len = (d[pos + 2] << 8) | d[pos + 3];
            pos += 4;
            if (pos + len > d.Length) break;
            string Text() => Encoding.UTF8.GetString(d, pos, len).Trim('\0', ' ');
            switch (type)
            {
                case 0x1011: info.WpsDeviceName = Text(); break;
                case 0x1021: info.WpsManufacturer = Text(); break;
                case 0x1023: info.WpsModel = (Text() + " " + info.WpsModel).Trim(); break;
                case 0x1024: info.WpsModel = (info.WpsModel + " " + Text()).Trim(); break;
            }
            pos += len;
        }
    }

    private static string ReadAsciiZ(byte[] d, int offset, int max)
    {
        int end = offset;
        while (end < d.Length && end < offset + max && d[end] != 0) end++;
        var s = Encoding.ASCII.GetString(d, offset, end - offset);
        return new string(s.Where(c => c >= 0x20 && c < 0x7F).ToArray()).Trim();
    }

    /// <summary>Counts spatial streams in a VHT/HE MCS map (2 bits per stream, 3 = not supported).</summary>
    private static int CountStreams(byte lo, byte hi)
    {
        int map = lo | (hi << 8);
        int count = 0;
        for (int i = 0; i < 8; i++)
        {
            if (((map >> (i * 2)) & 0x3) != 3) count = i + 1;
        }
        return count;
    }

    private static void SetWidth(BssInfo info, int width, int centerChannel)
    {
        info.ChannelWidthMhz = width;
        if (centerChannel > 0)
        {
            info.CenterChannel = centerChannel;
            info.CenterFrequencyMhz = Channels.ChannelToFrequency(info.Band, centerChannel);
        }
    }

    private static void ApplyChannelWidth(BssInfo info, ParseState st)
    {
        // HT (40 MHz) - valid on 2.4 and 5 GHz
        if (st.HtOperation is { Length: >= 2 } ht)
        {
            int offset = ht[1] & 0x03;
            bool anyWidth = (ht[1] & 0x04) != 0;
            if (anyWidth && offset is 1 or 3)
            {
                int center = offset == 1 ? info.Channel + 2 : info.Channel - 2;
                SetWidth(info, 40, center);
            }
        }

        // VHT (80/160 MHz) - 5 GHz
        if (info.Band == WifiBand.Band5 && st.VhtOperation is { Length: >= 3 } vht)
            ApplyVhtOperation(info, vht[0], vht[1], vht[2]);

        // HE operation: optional VHT info (5 GHz) and 6 GHz operation info
        if (st.HeOperation is { Length: >= 6 } he)
        {
            int parameters = he[0] | (he[1] << 8) | (he[2] << 16);
            bool vhtPresent = (parameters & (1 << 14)) != 0;
            bool coHosted = (parameters & (1 << 15)) != 0;
            bool sixGhzPresent = (parameters & (1 << 17)) != 0;
            int pos = 6;
            if (vhtPresent)
            {
                if (he.Length >= pos + 3 && info.Band == WifiBand.Band5 && st.VhtOperation == null)
                    ApplyVhtOperation(info, he[pos], he[pos + 1], he[pos + 2]);
                pos += 3;
            }
            if (coHosted) pos += 1;
            if (sixGhzPresent && he.Length >= pos + 5)
            {
                int control = he[pos + 1];
                int ccfs0 = he[pos + 2];
                int ccfs1 = he[pos + 3];
                switch (control & 0x03)
                {
                    case 0: SetWidth(info, 20, info.Channel); break;
                    case 1: SetWidth(info, 40, ccfs0); break;
                    case 2: SetWidth(info, 80, ccfs0); break;
                    case 3:
                        if (ccfs1 != 0 && Math.Abs(ccfs1 - ccfs0) == 8) SetWidth(info, 160, ccfs1);
                        else if (ccfs1 != 0 && Math.Abs(ccfs1 - ccfs0) > 16) { SetWidth(info, 160, ccfs0); info.Is80Plus80 = true; }
                        else SetWidth(info, 160, ccfs0);
                        break;
                }
            }
        }

        // EHT operation (up to 320 MHz)
        if (st.EhtOperation is { Length: >= 8 } eht && (eht[0] & 0x01) != 0)
        {
            int ccfs0 = eht[6];
            int ccfs1 = eht[7];
            switch (eht[5] & 0x07)
            {
                case 0: SetWidth(info, 20, info.Channel); break;
                case 1: SetWidth(info, 40, ccfs0); break;
                case 2: SetWidth(info, 80, ccfs0); break;
                case 3: SetWidth(info, 160, ccfs1 != 0 ? ccfs1 : ccfs0); break;
                case 4: SetWidth(info, 320, ccfs1 != 0 ? ccfs1 : ccfs0); break;
            }
        }
    }

    private static void ApplyVhtOperation(BssInfo info, int width, int ccfs0, int ccfs1)
    {
        switch (width)
        {
            case 1:
                if (ccfs1 == 0) SetWidth(info, 80, ccfs0);
                else if (Math.Abs(ccfs1 - ccfs0) == 8) SetWidth(info, 160, ccfs1);
                else if (Math.Abs(ccfs1 - ccfs0) > 16) { SetWidth(info, 160, ccfs0); info.Is80Plus80 = true; }
                else SetWidth(info, 80, ccfs0);
                break;
            case 2:
                SetWidth(info, 160, ccfs0);
                break;
            case 3:
                SetWidth(info, 160, ccfs0);
                info.Is80Plus80 = true;
                break;
        }
    }

    private static int WidthIndex(int width) => width switch
    {
        <= 20 => 0,
        <= 40 => 1,
        <= 80 => 2,
        <= 160 => 3,
        _ => 4,
    };

    private static void ApplyStandard(BssInfo info, ParseState st, ushort[] rates)
    {
        int w = WidthIndex(info.ChannelWidthMhz);
        double legacy = rates.Length == 0 ? 0 : rates.Max(r => (r & 0x7F) / 2.0);
        int streams;
        double perStream;

        if (st.HasEht || st.EhtOperation != null)
        {
            info.Standard = "Wi-Fi 7";
            info.Protocol = "802.11be";
            streams = Math.Max(1, st.HeStreams > 0 ? st.HeStreams : st.VhtStreams);
            perStream = EhtRates[w];
        }
        else if (st.HasHe || st.HeOperation != null)
        {
            info.Standard = info.Band == WifiBand.Band6 ? "Wi-Fi 6E" : "Wi-Fi 6";
            info.Protocol = "802.11ax";
            streams = Math.Max(1, st.HeStreams);
            perStream = HeRates[w];
        }
        else if (info.Band == WifiBand.Band5 && (st.HasVht || st.VhtOperation != null))
        {
            info.Standard = "Wi-Fi 5";
            info.Protocol = "802.11ac";
            streams = Math.Max(1, st.VhtStreams);
            perStream = VhtRates[w];
        }
        else if (st.HasHt || st.HtOperation != null)
        {
            info.Standard = "Wi-Fi 4";
            info.Protocol = "802.11n";
            streams = Math.Max(1, st.HtStreams);
            perStream = HtRates[w];
        }
        else
        {
            info.Standard = "Legacy";
            info.Protocol = info.Band == WifiBand.Band2_4 ? (legacy > 11 ? "802.11g" : "802.11b") : "802.11a";
            streams = 1;
            perStream = legacy;
        }

        info.SpatialStreams = streams;
        info.MaxRateMbps = Math.Round(Math.Max(legacy, perStream * streams), 1);

        var roaming = new List<string>();
        if (st.Has11k) roaming.Add("k");
        if (st.Has11v) roaming.Add("v");
        if (st.Has11r) roaming.Add("r");
        info.Roaming = roaming.Count == 0 ? "" : "802.11" + string.Join("/", roaming);
    }

    private static void ApplySecurity(BssInfo info, ParseState st, ushort capability)
    {
        bool privacy = (capability & 0x0010) != 0;
        if (st.Rsn is not { Length: >= 2 } rsn)
        {
            info.Security = st.HasWpa1 ? "WPA-Personal" : privacy ? "WEP" : "Open";
            info.Ciphers = st.HasWpa1 ? "TKIP" : privacy ? "WEP" : "";
            return;
        }

        var ciphers = new SortedSet<string>();
        var akms = new HashSet<int>();
        int capabilities = 0;
        int p = 2; // version
        if (rsn.Length >= p + 4) p += 4; // group cipher
        if (rsn.Length >= p + 2)
        {
            int count = rsn[p] | (rsn[p + 1] << 8);
            p += 2;
            for (int i = 0; i < count && rsn.Length >= p + 4; i++, p += 4)
                ciphers.Add(CipherName(rsn[p + 3]));
        }
        if (rsn.Length >= p + 2)
        {
            int count = rsn[p] | (rsn[p + 1] << 8);
            p += 2;
            for (int i = 0; i < count && rsn.Length >= p + 4; i++, p += 4)
            {
                bool ieee = rsn[p] == 0x00 && rsn[p + 1] == 0x0F && rsn[p + 2] == 0xAC;
                akms.Add(ieee ? rsn[p + 3] : 1000 + rsn[p + 3]);
            }
        }
        if (rsn.Length >= p + 2) capabilities = rsn[p] | (rsn[p + 1] << 8);

        bool mfpr = (capabilities & 0x40) != 0;
        bool mfpc = (capabilities & 0x80) != 0;
        info.Pmf = mfpr ? "Required" : mfpc ? "Capable" : "No";
        info.Ciphers = string.Join(", ", ciphers);

        bool sae = akms.Overlaps(new[] { 8, 9, 24, 25 });
        bool psk = akms.Overlaps(new[] { 2, 4, 6 });
        bool suiteB = akms.Overlaps(new[] { 12, 13 });
        bool eap = akms.Overlaps(new[] { 1, 3, 5, 11 });
        bool owe = akms.Contains(18);

        if (suiteB) info.Security = "WPA3-Enterprise 192";
        else if (eap) info.Security = mfpr && akms.Contains(5) ? "WPA3-Enterprise" : "WPA2-Enterprise";
        else if (sae && psk) info.Security = "WPA2/WPA3-Personal";
        else if (sae) info.Security = "WPA3-Personal";
        else if (psk) info.Security = st.HasWpa1 ? "WPA/WPA2-Personal" : "WPA2-Personal";
        else if (owe) info.Security = "OWE (Enhanced Open)";
        else info.Security = "WPA2";
    }

    private static string CipherName(byte type) => type switch
    {
        1 => "WEP-40",
        2 => "TKIP",
        4 => "CCMP",
        5 => "WEP-104",
        8 => "GCMP",
        9 => "GCMP-256",
        10 => "CCMP-256",
        _ => $"Cipher {type}",
    };
}
