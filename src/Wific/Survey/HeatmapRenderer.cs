// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wific.Survey;

/// <summary>
/// Interpolates measured RSSI values over the floor plan (inverse distance weighting) and colours them.
/// Areas far away from any measurement fade out, so the map never pretends to know more than was measured.
/// </summary>
public static class HeatmapRenderer
{
    /// <summary>Colour scale: dBm → colour. Shared with the legend.</summary>
    public static readonly (double Dbm, Color Color)[] Scale =
    {
        (-30, Color.FromRgb(0x00, 0xC8, 0x53)),
        (-50, Color.FromRgb(0x64, 0xDD, 0x17)),
        (-60, Color.FromRgb(0xFF, 0xD6, 0x00)),
        (-67, Color.FromRgb(0xFF, 0x91, 0x00)),
        (-75, Color.FromRgb(0xFF, 0x3D, 0x00)),
        (-85, Color.FromRgb(0xB7, 0x1C, 0x1C)),
        (-95, Color.FromRgb(0x4A, 0x14, 0x8C)),
    };

    public static Color ColorFor(double dbm)
    {
        if (dbm >= Scale[0].Dbm) return Scale[0].Color;
        for (int i = 1; i < Scale.Length; i++)
        {
            if (dbm >= Scale[i].Dbm)
            {
                var (d0, c0) = Scale[i - 1];
                var (d1, c1) = Scale[i];
                double t = (d0 - dbm) / (d0 - d1);
                return Color.FromRgb(
                    (byte)(c0.R + (c1.R - c0.R) * t),
                    (byte)(c0.G + (c1.G - c0.G) * t),
                    (byte)(c0.B + (c1.B - c0.B) * t));
            }
        }
        return Scale[^1].Color;
    }

    public static LinearGradientBrush LegendBrush()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        double span = Scale[0].Dbm - Scale[^1].Dbm;
        foreach (var (dbm, color) in Scale)
            brush.GradientStops.Add(new GradientStop(color, (Scale[0].Dbm - dbm) / span));
        brush.Freeze();
        return brush;
    }

    /// <summary>Renders the heatmap at a reduced resolution; the caller stretches it over the plan.</summary>
    public static WriteableBitmap? Render(double planWidth, double planHeight, IReadOnlyList<(Point Position, double Value)> points)
    {
        if (points.Count == 0 || planWidth < 1 || planHeight < 1) return null;

        double longest = Math.Max(planWidth, planHeight);
        double cell = Math.Max(1, longest / 260);
        int w = Math.Max(1, (int)Math.Ceiling(planWidth / cell));
        int h = Math.Max(1, (int)Math.Ceiling(planHeight / cell));

        // Coverage radius: about the typical spacing between measurements, never tiny.
        double spacing = points.Count < 2
            ? longest * 0.12
            : Median(points.Select(p => points.Where(q => q.Position != p.Position)
                .Select(q => (q.Position - p.Position).Length).DefaultIfEmpty(longest * 0.12).Min()));
        double radius = Math.Clamp(spacing * 1.1, longest * 0.05, longest * 0.25);

        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            double py = (y + 0.5) * cell;
            for (int x = 0; x < w; x++)
            {
                double px = (x + 0.5) * cell;
                double sumW = 0, sumV = 0, nearest = double.MaxValue;
                foreach (var (pos, value) in points)
                {
                    double dx = pos.X - px, dy = pos.Y - py;
                    double d2 = dx * dx + dy * dy;
                    double d = Math.Sqrt(d2);
                    if (d < nearest) nearest = d;
                    double weight = 1.0 / Math.Max(d2, 1);
                    sumW += weight;
                    sumV += weight * value;
                }

                double alpha = nearest <= radius ? 1 : Math.Max(0, 1 - (nearest - radius) / radius);
                if (alpha <= 0) continue;
                var c = ColorFor(sumV / sumW);
                int i = (y * w + x) * 4;
                byte a = (byte)(alpha * 255);
                // Premultiplied BGRA
                pixels[i] = (byte)(c.B * alpha);
                pixels[i + 1] = (byte)(c.G * alpha);
                pixels[i + 2] = (byte)(c.R * alpha);
                pixels[i + 3] = a;
            }
        }

        var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static double Median(IEnumerable<double> values)
    {
        var list = values.OrderBy(v => v).ToList();
        if (list.Count == 0) return 0;
        return list.Count % 2 == 1 ? list[list.Count / 2] : (list[list.Count / 2 - 1] + list[list.Count / 2]) / 2;
    }
}
