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
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(75);

    public static readonly DependencyProperty MinutesProperty = DependencyProperty.Register(
        nameof(Minutes), typeof(int), typeof(SignalHistoryGraph),
        new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedOnlyProperty = DependencyProperty.Register(
        nameof(SelectedOnly), typeof(bool), typeof(SignalHistoryGraph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Minutes
    {
        get => (int)GetValue(MinutesProperty);
        set => SetValue(MinutesProperty, value);
    }

    public bool SelectedOnly
    {
        get => (bool)GetValue(SelectedOnlyProperty);
        set => SetValue(SelectedOnlyProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < PadLeft + PadRight + 20 || ActualHeight < PadTop + PadBottom + 20) return;

        var r = PlotArea;
        int minutes = Math.Max(1, Minutes);
        var window = TimeSpan.FromMinutes(minutes);
        var now = Now ?? DateTime.Now;
        var start = now - window;
        double TimeToX(DateTime t) => r.Left + (t - start).TotalSeconds / window.TotalSeconds * r.Width;

        DrawDbmAxis(dc);
        int ticks = minutes == 1 ? 4 : 5;
        for (int i = 0; i <= ticks; i++)
        {
            var t = start + TimeSpan.FromTicks(window.Ticks * i / ticks);
            double x = TimeToX(t);
            dc.DrawLine(GridPen, new Point(x, r.Top), new Point(x, r.Bottom + 4));
            var ago = now - t;
            string text = i == ticks ? "now" : ago.TotalSeconds < 60 ? $"-{(int)Math.Round(ago.TotalSeconds)} s" : $"-{ago.TotalMinutes:0.#} min";
            var label = Text(text, 10.5, AxisText);
            double lx = Math.Min(x - label.Width / 2, r.Right - label.Width);
            dc.DrawText(label, new Point(lx, r.Bottom + 6));
        }

        var selected = Selected as AccessPointViewModel;
        var items = Items?.OfType<AccessPointViewModel>().ToList();
        if (items == null) return;
        if (SelectedOnly) items = items.Where(a => a == selected).ToList();

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
        else if (SelectedOnly)
        {
            var t = Text("Select a network in the table", 13, AxisText);
            dc.DrawText(t, new Point(r.Left + (r.Width - t.Width) / 2, r.Top + (r.Height - t.Height) / 2));
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
