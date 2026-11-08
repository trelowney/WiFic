// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Wific.Native;

/// <summary>Thin P/Invoke layer over the Windows Native Wifi API (wlanapi.dll).</summary>
internal static class WlanApi
{
    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_ACCESS_DENIED = 5;
    public const uint ERROR_INVALID_STATE = 5023;

    private const int WlanIntfOpcodeCurrentConnection = 7;
    private const int Dot11BssTypeAny = 3;

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanScan(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pDot11Ssid, IntPtr pIeData, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetNetworkBssList(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pDot11Ssid, int dot11BssType,
        [MarshalAs(UnmanagedType.Bool)] bool bSecurityEnabled, IntPtr pReserved, out IntPtr ppWlanBssList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, int opCode, IntPtr pReserved,
        out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    public static IntPtr Open()
    {
        uint rc = WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr handle);
        if (rc != ERROR_SUCCESS)
            throw new WlanException(rc, "WlanOpenHandle");
        return handle;
    }

    public static void Close(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            WlanCloseHandle(handle, IntPtr.Zero);
    }

    public static List<WlanInterface> EnumInterfaces(IntPtr handle)
    {
        var result = new List<WlanInterface>();
        uint rc = WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr list);
        if (rc != ERROR_SUCCESS)
            throw new WlanException(rc, "WlanEnumInterfaces");
        try
        {
            // WLAN_INTERFACE_INFO_LIST: DWORD count, DWORD index, WLAN_INTERFACE_INFO[] (532 bytes each)
            int count = Marshal.ReadInt32(list, 0);
            for (int i = 0; i < count; i++)
            {
                IntPtr item = list + 8 + i * 532;
                var guidBytes = new byte[16];
                Marshal.Copy(item, guidBytes, 0, 16);
                string description = Marshal.PtrToStringUni(item + 16) ?? "Wi-Fi adapter";
                int state = Marshal.ReadInt32(item, 16 + 512);
                result.Add(new WlanInterface(new Guid(guidBytes), description, state));
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }
        return result;
    }

    public static uint Scan(IntPtr handle, Guid iface)
    {
        return WlanScan(handle, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Reads the cached BSS list of the interface. Each entry is copied into managed memory.</summary>
    public static List<RawBssEntry> GetBssList(IntPtr handle, Guid iface)
    {
        var result = new List<RawBssEntry>();
        uint rc = WlanGetNetworkBssList(handle, ref iface, IntPtr.Zero, Dot11BssTypeAny, false, IntPtr.Zero, out IntPtr list);
        if (rc != ERROR_SUCCESS)
            throw new WlanException(rc, "WlanGetNetworkBssList");
        try
        {
            // WLAN_BSS_LIST: DWORD dwTotalSize, DWORD dwNumberOfItems, WLAN_BSS_ENTRY[] (360 bytes each)
            int count = Marshal.ReadInt32(list, 4);
            for (int i = 0; i < count; i++)
            {
                IntPtr e = list + 8 + i * 360;

                int ssidLen = Math.Clamp(Marshal.ReadInt32(e, 0), 0, 32);
                var ssid = new byte[ssidLen];
                if (ssidLen > 0) Marshal.Copy(e + 4, ssid, 0, ssidLen);

                var bssid = new byte[6];
                Marshal.Copy(e + 40, bssid, 0, 6);

                int bssType = Marshal.ReadInt32(e, 48);
                int phyType = Marshal.ReadInt32(e, 52);
                int rssi = Marshal.ReadInt32(e, 56);
                int linkQuality = Marshal.ReadInt32(e, 60);
                ushort beaconPeriod = unchecked((ushort)Marshal.ReadInt16(e, 66));
                ulong tsf = unchecked((ulong)Marshal.ReadInt64(e, 72));
                long hostTimestamp = Marshal.ReadInt64(e, 80);
                ushort capability = unchecked((ushort)Marshal.ReadInt16(e, 88));
                uint centerFreqKhz = unchecked((uint)Marshal.ReadInt32(e, 92));

                int rateCount = Math.Clamp(Marshal.ReadInt32(e, 96), 0, 126);
                var rates = new ushort[rateCount];
                for (int r = 0; r < rateCount; r++)
                    rates[r] = unchecked((ushort)Marshal.ReadInt16(e, 100 + r * 2));

                int ieOffset = Marshal.ReadInt32(e, 352);
                int ieSize = Marshal.ReadInt32(e, 356);
                var ies = Array.Empty<byte>();
                if (ieSize > 0 && ieSize < 65536)
                {
                    ies = new byte[ieSize];
                    Marshal.Copy(e + ieOffset, ies, 0, ieSize);
                }

                result.Add(new RawBssEntry(ssid, bssid, bssType, phyType, rssi, linkQuality, beaconPeriod, tsf,
                    hostTimestamp, capability, centerFreqKhz, rates, ies));
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }
        return result;
    }

    /// <summary>Returns the BSSID the interface is currently associated with, or null.</summary>
    public static byte[]? GetConnectedBssid(IntPtr handle, Guid iface)
    {
        uint rc = WlanQueryInterface(handle, ref iface, WlanIntfOpcodeCurrentConnection, IntPtr.Zero, out uint size, out IntPtr data, IntPtr.Zero);
        if (rc != ERROR_SUCCESS || data == IntPtr.Zero)
            return null;
        try
        {
            // WLAN_CONNECTION_ATTRIBUTES: isState(4) mode(4) profile WCHAR[256](512) then
            // WLAN_ASSOCIATION_ATTRIBUTES: DOT11_SSID(36) bssType(4) bssid(6)
            if (size < 566) return null;
            int state = Marshal.ReadInt32(data, 0);
            if (state != 1) return null; // wlan_interface_state_connected
            var bssid = new byte[6];
            Marshal.Copy(data + 560, bssid, 0, 6);
            return bssid;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }
}

public sealed record WlanInterface(Guid Id, string Description, int State)
{
    public override string ToString() => Description;
}

internal sealed record RawBssEntry(
    byte[] Ssid,
    byte[] Bssid,
    int BssType,
    int PhyType,
    int Rssi,
    int LinkQuality,
    ushort BeaconPeriod,
    ulong Tsf,
    long HostTimestamp,
    ushort Capability,
    uint CenterFrequencyKhz,
    ushort[] Rates,
    byte[] InformationElements);

internal sealed class WlanException : Exception
{
    public uint ErrorCode { get; }

    public WlanException(uint code, string function)
        : base($"{function} failed with error {code} ({new System.ComponentModel.Win32Exception((int)code).Message})")
    {
        ErrorCode = code;
    }
}
