// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Wific.ViewModels;

namespace Wific.Controls;

/// <summary>RSSI over time for the visible networks; the selected one is highlighted.</summary>
public sealed class SignalHistoryGraph : GraphBase
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(75);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < PadLeft + PadRight + 20 || ActualHeight < PadTop + PadBottom + 20) return;

        var r = PlotArea;
        var now = DateTime.Now;
        var start = now - Window;
        double TimeToX(DateTime t) => r.Left + (t - start).TotalSeconds / Window.TotalSeconds * r.Width;

        DrawDbmAxis(dc);
        for (int m = 0; m <= 5; m++)
        {
            double x = TimeToX(start + TimeSpan.FromMinutes(m));
            dc.DrawLine(GridPen, new Point(x, r.Top), new Point(x, r.Bottom + 4));
            var label = Text(m == 5 ? "now" : $"-{5 - m} min", 10.5, AxisText);
            double lx = Math.Min(x - label.Width / 2, r.Right - label.Width);
            dc.DrawText(label, new Point(lx, r.Bottom + 6));
        }

        var items = Items?.OfType<AccessPointViewModel>().ToList();
        if (items == null || items.Count == 0) return;
        var selected = Selected as AccessPointViewModel;

        dc.PushClip(new RectangleGeometry(r));
        foreach (var ap in items.Where(a => a != selected))
            DrawSeries(dc, ap, TimeToX, start, false);
        if (selected != null && items.Contains(selected))
            DrawSeries(dc, selected, TimeToX, start, true);
        dc.Pop();

        if (selected != null)
        {
            var t = Text($"{selected.Ssid}  {selected.Rssi} dBm", 12, selected.Brush, true);
            dc.DrawText(t, new Point(r.Right - t.Width - 6, r.Top + 4));
        }
    }

    private void DrawSeries(DrawingContext dc, AccessPointViewModel ap, Func<DateTime, double> timeToX, DateTime start, bool isSelected)
    {
        var points = ap.History.Where(s => s.Time >= start - MaxGap).ToList();
        if (points.Count == 0) return;

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < points.Count; i++)
            {
                var p = new Point(timeToX(points[i].Time), DbmToY(points[i].Rssi));
                bool newFigure = i == 0 || points[i].Time - points[i - 1].Time > MaxGap;
                if (newFigure) g.BeginFigure(p, false, false);
                else g.LineTo(p, true, true);
            }
        }
        geo.Freeze();

        byte alpha = (byte)AlphaFor(isSelected, ap);
        var pen = new Pen(new SolidColorBrush(WithAlpha(ap.Color, alpha)), isSelected ? 2.6 : 1.2)
        {
            LineJoin = PenLineJoin.Round,
        };
        dc.DrawGeometry(null, pen, geo);

        if (points.Count == 1 || isSelected)
        {
            var last = points[^1];
            var c = new Point(timeToX(last.Time), DbmToY(last.Rssi));
            dc.DrawEllipse(new SolidColorBrush(WithAlpha(ap.Color, alpha)), null, c, isSelected ? 4 : 2, isSelected ? 4 : 2);
        }
    }

    private static int AlphaFor(bool isSelected, AccessPointViewModel ap) => isSelected ? 0xFF : ap.IsStale ? 0x40 : 0x99;
}
