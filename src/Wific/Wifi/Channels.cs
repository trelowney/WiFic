// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wific.Wifi;

public enum WifiBand
{
    Band2_4 = 0,
    Band5 = 1,
    Band6 = 2,
}

public static class Channels
{
    public static WifiBand BandFromFrequency(int mhz)
    {
        if (mhz < 3000) return WifiBand.Band2_4;
        if (mhz < 5930) return WifiBand.Band5;
        return WifiBand.Band6;
    }

    public static int FrequencyToChannel(int mhz)
    {
        switch (BandFromFrequency(mhz))
        {
            case WifiBand.Band2_4:
                return mhz == 2484 ? 14 : (mhz - 2407) / 5;
            case WifiBand.Band5:
                return (mhz - 5000) / 5;
            default:
                return mhz == 5935 ? 2 : (mhz - 5950) / 5;
        }
    }

    public static int ChannelToFrequency(WifiBand band, int channel)
    {
        switch (band)
        {
            case WifiBand.Band2_4:
                return channel == 14 ? 2484 : 2407 + 5 * channel;
            case WifiBand.Band5:
                return 5000 + 5 * channel;
            default:
                return channel == 2 ? 5935 : 5950 + 5 * channel;
        }
    }

    public static string BandName(WifiBand band) => band switch
    {
        WifiBand.Band2_4 => "2.4 GHz",
        WifiBand.Band5 => "5 GHz",
        _ => "6 GHz",
    };

    /// <summary>Frequency range (MHz) shown by the channel graph for a band.</summary>
    public static (int Min, int Max) GraphRange(WifiBand band) => band switch
    {
        WifiBand.Band2_4 => (2397, 2497),
        WifiBand.Band5 => (5150, 5910),
        _ => (5925, 7125),
    };

    /// <summary>Channels labelled on the X axis of the channel graph.</summary>
    public static int[] AxisChannels(WifiBand band) => band switch
    {
        WifiBand.Band2_4 => new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 },
        WifiBand.Band5 => new[] { 36, 44, 52, 60, 68, 100, 108, 116, 124, 132, 140, 149, 157, 165, 173 },
        _ => new[] { 1, 17, 33, 49, 65, 81, 97, 113, 129, 145, 161, 177, 193, 209, 225 },
    };
}
