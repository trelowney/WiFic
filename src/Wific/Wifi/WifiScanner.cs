// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Wific.Native;

namespace Wific.Wifi;

public sealed class ScanResult
{
    public List<BssInfo> Networks { get; } = new();
    public string? ConnectedBssid { get; set; }
}

/// <summary>Source of scan results: the real WLAN API or the demo data.</summary>
public interface IWifiScanner : IDisposable
{
    IReadOnlyList<WlanInterface> GetInterfaces();
    void TriggerScan(Guid iface);
    ScanResult Read(Guid iface);
    (ConnectionSnapshot Connection, int? Rssi)? ReadConnection(Guid iface);
}

/// <summary>Owns the WLAN client handle. All methods are blocking and may be called from a worker thread.</summary>
public sealed class WifiScanner : IWifiScanner
{
    private readonly object _sync = new();
    private IntPtr _handle;

    public WifiScanner()
    {
        _handle = WlanApi.Open();
    }

    public IReadOnlyList<WlanInterface> GetInterfaces()
    {
        lock (_sync)
        {
            return WlanApi.EnumInterfaces(_handle);
        }
    }

    /// <summary>Asks the driver for a fresh scan. Results become available a few seconds later.</summary>
    public void TriggerScan(Guid iface)
    {
        lock (_sync)
        {
            uint rc = WlanApi.Scan(_handle, iface);
            if (rc != WlanApi.ERROR_SUCCESS)
                throw new WlanException(rc, "WlanScan");
        }
    }

    public ScanResult Read(Guid iface)
    {
        lock (_sync)
        {
            var result = new ScanResult();
            foreach (var raw in WlanApi.GetBssList(_handle, iface))
            {
                if (raw.CenterFrequencyKhz == 0) continue;
                result.Networks.Add(IeParser.Parse(raw));
            }

            var connected = WlanApi.GetConnectedBssid(_handle, iface);
            if (connected != null)
                result.ConnectedBssid = string.Join(":", connected.Select(b => b.ToString("X2")));
            return result;
        }
    }

    /// <summary>Current association and live RSSI; null when not connected.</summary>
    public (ConnectionSnapshot Connection, int? Rssi)? ReadConnection(Guid iface)
    {
        lock (_sync)
        {
            if (_handle == IntPtr.Zero) return null;
            var c = WlanApi.QueryConnection(_handle, iface);
            if (c == null) return null;
            return (c, WlanApi.QueryRssi(_handle, iface));
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            WlanApi.Close(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
