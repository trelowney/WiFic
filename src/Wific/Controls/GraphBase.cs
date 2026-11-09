// WiFic - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Wific.Controls;

/// <summary>Shared plumbing for the lightweight, dependency-free graphs.</summary>
public abstract class GraphBase : FrameworkElement
{
    protected const double MinDbm = -100;
    protected const double MaxDbm = -20;
    protected const double PadLeft = 46, PadRight = 14, PadTop = 14, PadBottom = 30;

    protected Brush Background => Res("GraphBg", Color.FromRgb(0x12, 0x15, 0x1C));
    protected Brush AxisText => Res("GraphText", Color.FromRgb(0x7D, 0x86, 0x9C));
    protected Pen GridPen => new(Res("GraphGrid", Color.FromRgb(0x23, 0x29, 0x35)), 1);
    protected Pen AxisPen => new(Res("GraphAxis", Color.FromRgb(0x33, 0x3B, 0x4A)), 1);

    /// <summary>Looks up a theme brush (changes when the theme is switched).</summary>
    protected Brush Res(string key, Color fallback) => TryFindResource(key) as Brush ?? Frozen(fallback);

    protected static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    protected static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable), typeof(GraphBase),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(object), typeof(GraphBase),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(GraphBase),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Items
    {
        get => (IEnumerable?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public object? Selected
    {
        get => GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    public static readonly DependencyProperty NowProperty = DependencyProperty.Register(
        nameof(Now), typeof(DateTime?), typeof(GraphBase),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Reference time of the graph; null means the current time (playback sets a past time).</summary>
    public DateTime? Now
    {
        get => (DateTime?)GetValue(NowProperty);
        set => SetValue(NowProperty, value);
    }

    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    protected Rect PlotArea => new(PadLeft, PadTop,
        System.Math.Max(1, ActualWidth - PadLeft - PadRight),
        System.Math.Max(1, ActualHeight - PadTop - PadBottom));

    protected double DbmToY(double dbm)
    {
        var r = PlotArea;
        double v = System.Math.Clamp(dbm, MinDbm, MaxDbm);
        return r.Bottom - (v - MinDbm) / (MaxDbm - MinDbm) * r.Height;
    }

    protected void DrawDbmAxis(DrawingContext dc)
    {
        var r = PlotArea;
        for (int dbm = (int)MinDbm; dbm <= (int)MaxDbm; dbm += 10)
        {
            double y = DbmToY(dbm);
            dc.DrawLine(GridPen, new Point(r.Left, y), new Point(r.Right, y));
            var t = Text(dbm.ToString(CultureInfo.InvariantCulture), 10.5, AxisText);
            dc.DrawText(t, new Point(r.Left - t.Width - 8, y - t.Height / 2));
        }
        var unit = Text("dBm", 10, AxisText);
        dc.DrawText(unit, new Point(4, r.Top - 12 < 0 ? 0 : r.Top - 12));
        dc.DrawLine(AxisPen, new Point(r.Left, r.Bottom), new Point(r.Right, r.Bottom));
    }

    protected FormattedText Text(string s, double size, Brush brush, bool bold = false)
    {
        return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            bold ? FaceBold : Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    protected static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    protected static Pen FrozenPen(Color c, double thickness)
    {
        var p = new Pen(Frozen(c), thickness);
        p.Freeze();
        return p;
    }

    protected static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}
