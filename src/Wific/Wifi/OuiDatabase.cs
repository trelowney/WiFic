// WiFic - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace Wific.Wifi;

/// <summary>
/// IEEE MAC address block registry (MA-L /24, MA-M /28, MA-S /36) used to resolve the vendor of a BSSID.
/// The data is the Wireshark "manuf" file, embedded as a gzip resource at build time.
/// </summary>
public sealed class OuiDatabase
{
    private readonly Dictionary<long, string> _l24 = new();
    private readonly Dictionary<long, string> _m28 = new();
    private readonly Dictionary<long, string> _s36 = new();

    public int Count => _l24.Count + _m28.Count + _s36.Count;

    public static OuiDatabase LoadEmbedded()
    {
        var db = new OuiDatabase();
        using var stream = typeof(OuiDatabase).Assembly.GetManifestResourceStream("wific.manuf.gz");
        if (stream == null) return db;
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        string? line;
        while ((line = reader.ReadLine()) != null)
            db.AddLine(line);
        return db;
    }

    private void AddLine(string line)
    {
        if (line.Length == 0 || line[0] == '#') return;
        var parts = line.Split('\t');
        if (parts.Length < 2) return;

        string prefix = parts[0].Trim();
        string name = (parts.Length >= 3 ? parts[2] : parts[1]).Trim();
        if (name.Length == 0) name = parts[1].Trim();

        int bits = 24;
        int slash = prefix.IndexOf('/');
        if (slash > 0)
        {
            if (!int.TryParse(prefix.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out bits)) return;
            prefix = prefix[..slash];
        }

        var bytes = prefix.Split(':', '-', '.');
        long value = 0;
        int count = 0;
        foreach (var b in bytes)
        {
            if (count == 6) break;
            if (!byte.TryParse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte v)) return;
            value = (value << 8) | v;
            count++;
        }
        value <<= 8 * (6 - count);

        var table = bits switch { 24 => _l24, 28 => _m28, 36 => _s36, _ => null };
        if (table == null) return;
        table[value >> (48 - bits)] = name;
    }

    /// <summary>Registry names that do not say which brand it is.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Routerboard.com"] = "MikroTik",
        ["Mikrotikls SIA"] = "MikroTik",
    };

    public string? Lookup(byte[] mac)
    {
        if (mac.Length < 6) return null;
        long value = 0;
        for (int i = 0; i < 6; i++) value = (value << 8) | mac[i];

        string? name = _s36.TryGetValue(value >> 12, out var s) ? s
            : _m28.TryGetValue(value >> 20, out var m) ? m
            : _l24.TryGetValue(value >> 24, out var l) ? l
            : null;
        return name == null ? null : Aliases.TryGetValue(name, out var alias) ? alias : name;
    }

    /// <summary>
    /// Resolves the vendor. Locally administered BSSIDs (common for secondary SSIDs on one radio)
    /// are retried with the U/L bit cleared, which usually reveals the base MAC vendor.
    /// </summary>
    public (string Vendor, bool Derived) Resolve(byte[] mac)
    {
        var direct = Lookup(mac);
        if (direct != null) return (direct, false);
        if (mac.Length >= 6 && (mac[0] & 0x02) != 0)
        {
            var copy = (byte[])mac.Clone();
            copy[0] = (byte)(copy[0] & ~0x02);
            var derived = Lookup(copy);
            if (derived != null) return (derived, true);
            return ("Locally administered", false);
        }
        return ("Unknown", false);
    }
}
