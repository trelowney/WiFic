// WiFic - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wific.Wifi;

namespace Wific.Survey;

/// <summary>An invented flat with simulated measurements, shown in the survey window in demo mode.</summary>
public static class DemoSurvey
{
    private const int W = 1600, H = 1000;
    private const double PixelsPerMeter = 80;

    private sealed record Source(string Ssid, string Bssid, WifiBand Band, int Channel, double X, double Y, double RefDbm, double Exponent);

    private static readonly Source[] Sources =
    {
        new("Maple-Home", "64:D1:54:3A:7C:10", WifiBand.Band2_4, 6, 430, 330, -33, 2.7),
        new("Maple-Home", "64:D1:54:3A:7C:14", WifiBand.Band5, 36, 430, 330, -37, 3.0),
        new("Maple-Home", "64:D1:54:3A:7C:18", WifiBand.Band6, 37, 430, 330, -41, 3.3),
        new("Kowalski", "50:C7:BF:8E:7C:02", WifiBand.Band2_4, 1, 1900, 1150, -40, 2.6),
        new("Garden Flat", "3C:A6:2F:71:7C:20", WifiBand.Band2_4, 6, -300, 1200, -40, 2.6),
    };

    public static SurveyProject Create()
    {
        var project = new SurveyProject { Image = DrawPlan() };
        var random = new Random(11);
        var start = DateTime.Now.AddMinutes(-12);
        int n = 0;
        for (double y = 190; y < H - 100; y += 205)
        {
            for (double x = 170; x < W - 100; x += 210)
            {
                double px = x + (random.NextDouble() - 0.5) * 60;
                double py = y + (random.NextDouble() - 0.5) * 50;
                var point = new SurveyPoint { X = px, Y = py, Time = start.AddSeconds(25 * n++) };
                foreach (var s in Sources)
                {
                    double meters = Math.Max(1, Math.Sqrt((px - s.X) * (px - s.X) + (py - s.Y) * (py - s.Y)) / PixelsPerMeter);
                    double rssi = s.RefDbm - 10 * s.Exponent * Math.Log10(meters) + (random.NextDouble() - 0.5) * 4;
                    if (rssi < -92) continue;
                    point.Samples.Add(new SurveySample
                    {
                        Bssid = s.Bssid, Ssid = s.Ssid, Band = s.Band, Channel = s.Channel, Rssi = (int)Math.Round(rssi),
                    });
                }
                project.Points.Add(point);
            }
        }
        return project;
    }

    private static byte[] DrawPlan()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
            var minor = new Pen(new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF4)), 1);
            var major = new Pen(new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xE4)), 1);
            for (int x = 0; x <= W; x += 20) dc.DrawLine(x % 100 == 0 ? major : minor, new Point(x, 0), new Point(x, H));
            for (int y = 0; y <= H; y += 20) dc.DrawLine(y % 100 == 0 ? major : minor, new Point(0, y), new Point(W, y));

            var wall = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x3A, 0x48)), 12) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };
            var inner = new Pen(wall.Brush, 7) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };
            dc.DrawRectangle(null, wall, new Rect(80, 80, 1440, 840));

            void Line(Pen pen, double x1, double y1, double x2, double y2) => dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
            // Horizontal hall walls with door gaps.
            Line(inner, 80, 540, 300, 540);
            Line(inner, 400, 540, 820, 540);
            Line(inner, 920, 540, 1180, 540);
            Line(inner, 1280, 540, 1520, 540);
            Line(inner, 80, 660, 520, 660);
            Line(inner, 620, 660, 1050, 660);
            Line(inner, 1150, 660, 1520, 660);
            // Vertical room walls.
            Line(inner, 820, 80, 820, 540);
            Line(inner, 1180, 80, 1180, 540);
            Line(inner, 520, 660, 520, 920);
            Line(inner, 1050, 660, 1050, 920);

            var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var text = new SolidColorBrush(Color.FromRgb(0x7A, 0x82, 0x92));
            void Label(string s, double x, double y)
            {
                var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 30, text, 1.0);
                dc.DrawText(ft, new Point(x - ft.Width / 2, y - ft.Height / 2));
            }
            Label("Living room", 450, 440);
            Label("Kitchen", 1000, 310);
            Label("Office", 1350, 310);
            Label("Hall", 800, 600);
            Label("Bedroom", 300, 790);
            Label("Bath", 785, 790);
            Label("Kids' room", 1285, 790);
        }
        var bitmap = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
