// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Wific.Controls;

/// <summary>Tiny RSSI trend line (-100..-20 dBm) used in the connection strip.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<int>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<int>? Values
    {
        get => (IReadOnlyList<int>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (values == null || values.Count < 2 || w < 4 || h < 4) return;

        var list = values.ToList();
        int lo = Math.Min(list.Min() - 3, -90), hi = Math.Max(list.Max() + 3, -40);
        double Y(int v) => h - 1 - (double)(v - lo) / (hi - lo) * (h - 2);
        double step = w / (list.Count - 1);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(0, Y(list[0])), false, false);
            for (int i = 1; i < list.Count; i++)
                g.LineTo(new Point(i * step, Y(list[i])), true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, 1.4) { LineJoin = PenLineJoin.Round }, geo);
    }
}
