// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Wific.ViewModels;
using Wific.Wifi;

namespace Wific.Controls;

/// <summary>Spectrum view: every BSS is drawn as a shape spanning its full channel width, height = RSSI.</summary>
public sealed class ChannelGraph : GraphBase
{
    public static readonly DependencyProperty BandProperty = DependencyProperty.Register(
        nameof(Band), typeof(WifiBand), typeof(ChannelGraph),
        new FrameworkPropertyMetadata(WifiBand.Band2_4, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AdviceProperty = DependencyProperty.Register(
        nameof(Advice), typeof(ChannelAdvice), typeof(ChannelGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public WifiBand Band
    {
        get => (WifiBand)GetValue(BandProperty);
        set => SetValue(BandProperty, value);
    }

    public ChannelAdvice? Advice
    {
        get => (ChannelAdvice?)GetValue(AdviceProperty);
        set => SetValue(AdviceProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < PadLeft + PadRight + 20 || ActualHeight < PadTop + PadBottom + 20) return;

        var r = PlotArea;
        var (min, max) = Channels.GraphRange(Band);
        double FreqToX(double f) => r.Left + (f - min) / (max - min) * r.Width;

        if (Band == WifiBand.Band5)
        {
            double x1 = FreqToX(5250), x2 = FreqToX(5730);
            dc.DrawRectangle(Res("GraphDfs", Color.FromArgb(0x14, 0xFF, 0xB0, 0x3B)), null, new Rect(x1, r.Top, x2 - x1, r.Height));
            var dfs = Text("DFS", 10, AxisText);
            dc.DrawText(dfs, new Point((x1 + x2 - dfs.Width) / 2, r.Top + 2));
        }

        if (Advice is { } advice && advice.Band == Band)
        {
            double x1 = FreqToX(advice.LowMhz), x2 = FreqToX(advice.HighMhz);
            var good = Res("Good", Color.FromRgb(0x3D, 0xD6, 0x8C));
            dc.DrawRectangle(Res("GraphBest", Color.FromArgb(0x26, 0x3D, 0xD6, 0x8C)), null, new Rect(x1, r.Top, Math.Max(1, x2 - x1), r.Height));
            var pen = new Pen(good, 1) { DashStyle = DashStyles.Dash };
            dc.DrawLine(pen, new Point(x1, r.Top), new Point(x1, r.Bottom));
            dc.DrawLine(pen, new Point(x2, r.Top), new Point(x2, r.Bottom));
            var label = Text("Best", 10.5, good, true);
            dc.DrawText(label, new Point((x1 + x2 - label.Width) / 2, r.Top + 2));
        }

        DrawDbmAxis(dc);

        foreach (int ch in Channels.AxisChannels(Band))
        {
            double x = FreqToX(Channels.ChannelToFrequency(Band, ch));
            dc.DrawLine(GridPen, new Point(x, r.Top), new Point(x, r.Bottom + 4));
            var t = Text(ch.ToString(CultureInfo.InvariantCulture), 10.5, AxisText);
            dc.DrawText(t, new Point(x - t.Width / 2, r.Bottom + 6));
        }

        var items = Items?.OfType<AccessPointViewModel>().Where(a => a.Band == Band).ToList()
                    ?? new List<AccessPointViewModel>();
        var selected = Selected as AccessPointViewModel;

        var placed = new List<Rect>();
        dc.PushClip(new RectangleGeometry(new Rect(r.Left, 0, r.Width, r.Bottom)));
        foreach (var ap in items.Where(a => a != selected).OrderBy(a => a.Rssi))
            DrawNetwork(dc, ap, FreqToX, r, false, placed);
        if (selected != null && items.Contains(selected))
            DrawNetwork(dc, selected, FreqToX, r, true, placed);
        dc.Pop();

        if (items.Count == 0)
        {
            var t = Text($"No networks in the {Channels.BandName(Band)} band", 13, AxisText);
            dc.DrawText(t, new Point(r.Left + (r.Width - t.Width) / 2, r.Top + (r.Height - t.Height) / 2));
        }
    }

    private void DrawNetwork(DrawingContext dc, AccessPointViewModel ap, Func<double, double> freqToX, Rect r, bool isSelected, List<Rect> placed)
    {
        double half = ap.ChannelWidthMhz / 2.0;
        double left = freqToX(ap.CenterFrequencyMhz - half);
        double right = freqToX(ap.CenterFrequencyMhz + half);
        double top = DbmToY(ap.Rssi);
        double bottom = r.Bottom;
        double edge = (right - left) * 0.12;

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(left, bottom), true, true);
            g.BezierTo(new Point(left + edge * 0.4, top), new Point(left + edge * 0.6, top), new Point(left + edge, top), true, true);
            g.LineTo(new Point(right - edge, top), true, true);
            g.BezierTo(new Point(right - edge * 0.6, top), new Point(right - edge * 0.4, top), new Point(right, bottom), true, true);
        }
        geo.Freeze();

        byte fillAlpha = (byte)(isSelected ? 0x55 : ap.IsStale ? 0x0C : 0x22);
        byte strokeAlpha = (byte)(isSelected ? 0xFF : ap.IsStale ? 0x55 : 0xD0);
        var fill = new SolidColorBrush(WithAlpha(ap.Color, fillAlpha));
        var pen = new Pen(new SolidColorBrush(WithAlpha(ap.Color, strokeAlpha)), isSelected ? 2.5 : 1.3);
        if (ap.IsStale) pen.DashStyle = DashStyles.Dash;
        dc.DrawGeometry(fill, pen, geo);

        var label = Text(ap.Ssid, isSelected ? 12 : 11, new SolidColorBrush(WithAlpha(ap.Color, strokeAlpha)), isSelected);
        double lx = (left + right - label.Width) / 2;
        double ly = top - label.Height - 1;
        if (ly < r.Top - 12) ly = top + 2;
        // Move the label up (or down near the top) until it no longer overlaps an already placed one.
        var box = new Rect(lx, ly, label.Width, label.Height);
        for (int i = 0; i < 12 && placed.Exists(p => p.IntersectsWith(box)); i++)
        {
            double next = box.Y - label.Height;
            box.Y = next < r.Top - 12 ? box.Y + label.Height * (i + 2) : next;
        }
        placed.Add(box);
        ly = box.Y;
        dc.DrawText(label, new Point(lx, ly));
    }
}
