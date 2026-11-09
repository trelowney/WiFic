// WiFic - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wific.Wifi;

/// <summary>Everything WiFic knows about one BSSID after decoding its beacon / probe response.</summary>
public sealed class BssInfo
{
    public string Ssid { get; set; } = "";
    public bool IsHidden { get; set; }
    public byte[] Mac { get; set; } = new byte[6];
    public string Bssid { get; set; } = "";

    public int Rssi { get; set; }
    public int LinkQuality { get; set; }

    public int FrequencyMhz { get; set; }
    public WifiBand Band { get; set; }
    public int Channel { get; set; }

    public int ChannelWidthMhz { get; set; } = 20;
    public bool Is80Plus80 { get; set; }
    public int CenterFrequencyMhz { get; set; }
    public int CenterChannel { get; set; }

    public string Standard { get; set; } = "";
    public string Protocol { get; set; } = "";
    public int SpatialStreams { get; set; } = 1;
    public double MaxRateMbps { get; set; }

    public string Security { get; set; } = "Open";
    public string Ciphers { get; set; } = "";
    public string Pmf { get; set; } = "No";

    public int? StationCount { get; set; }
    public int? ChannelUtilization { get; set; }

    public ulong TsfMicroseconds { get; set; }
    public DateTime? ObservedUtc { get; set; }
    public int BeaconIntervalTu { get; set; }

    public string Country { get; set; } = "";
    public string WpsManufacturer { get; set; } = "";
    public string WpsModel { get; set; } = "";
    public string WpsDeviceName { get; set; } = "";
    public string ApName { get; set; } = "";
    public int? BssColor { get; set; }
    public bool MultiLink { get; set; }
    public string Roaming { get; set; } = "";

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<InformationElement> Elements { get; } = new();
}

public sealed record InformationElement(int Id, int? ExtensionId, string Name, byte[] Data)
{
    [JsonIgnore]
    public string IdText => ExtensionId is int ext ? $"255/{ext}" : Id.ToString();
    [JsonIgnore]
    public int Length => Data.Length;
    [JsonIgnore]
    public string Hex => Convert.ToHexString(Data, 0, Math.Min(64, Data.Length)) + (Data.Length > 64 ? "…" : "");
}
