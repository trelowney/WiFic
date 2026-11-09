// WiFic - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Wific.Wifi;

namespace Wific.Survey;

/// <summary>One network heard at a measurement point.</summary>
public sealed class SurveySample
{
    public string Bssid { get; set; } = "";
    public string Ssid { get; set; } = "";
    public int Rssi { get; set; }
    public WifiBand Band { get; set; }
    public int Channel { get; set; }
}

/// <summary>A measurement taken at a position on the floor plan (image pixel coordinates).</summary>
public sealed class SurveyPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public DateTime Time { get; set; }
    public List<SurveySample> Samples { get; set; } = new();
}

/// <summary>A site survey: the floor plan image and all measurement points. Saved as .wificsurvey (JSON).</summary>
public sealed class SurveyProject
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public int Version { get; set; } = 1;
    public byte[] Image { get; set; } = Array.Empty<byte>();
    public List<SurveyPoint> Points { get; set; } = new();

    public void Save(string path) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, Options));

    public static SurveyProject Load(string path)
    {
        var project = JsonSerializer.Deserialize<SurveyProject>(File.ReadAllBytes(path), Options)
                      ?? throw new InvalidDataException("The file is not a WiFic survey.");
        if (project.Image.Length == 0) throw new InvalidDataException("The survey does not contain a floor plan.");
        project.Points ??= new List<SurveyPoint>();
        return project;
    }
}

/// <summary>What the heatmap shows.</summary>
public sealed record SurveyTarget(string Label, string? Ssid)
{
    public static readonly SurveyTarget Any = new("Strongest signal (any network)", null);

    public override string ToString() => Label;
}

public static class SurveyMath
{
    public const int NoSignal = -100;

    /// <summary>Best RSSI at a point for the given network and band (null band = all bands).</summary>
    public static int ValueAt(SurveyPoint point, SurveyTarget target, WifiBand? band)
    {
        int best = NoSignal;
        foreach (var s in point.Samples)
        {
            if (band is WifiBand b && s.Band != b) continue;
            if (target.Ssid != null && !string.Equals(s.Ssid, target.Ssid, StringComparison.Ordinal)) continue;
            if (s.Rssi > best) best = s.Rssi;
        }
        return best;
    }

    /// <summary>SSIDs found in the survey, strongest first.</summary>
    public static List<SurveyTarget> Targets(IEnumerable<SurveyPoint> points)
    {
        var list = new List<SurveyTarget> { SurveyTarget.Any };
        list.AddRange(points.SelectMany(p => p.Samples)
            .Where(s => s.Ssid.Length > 0)
            .GroupBy(s => s.Ssid, StringComparer.Ordinal)
            .OrderByDescending(g => g.Max(s => s.Rssi))
            .Select(g => new SurveyTarget(g.Key, g.Key)));
        return list;
    }
}
