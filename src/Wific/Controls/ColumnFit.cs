// WiFic - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wific.Controls;

/// <summary>
/// Spreads DataGrid columns over the available width. Every column keeps its MinWidth (wide enough for its
/// content); the remaining space goes to columns with a Weight, so nothing is cut off and there is no horizontal
/// scrolling unless the window really is too narrow. Runs again whenever the grid's visible width changes.
/// </summary>
public static class ColumnFit
{
    public static readonly DependencyProperty WeightProperty =
        DependencyProperty.RegisterAttached("Weight", typeof(double), typeof(ColumnFit), new PropertyMetadata(0.0));

    public static double GetWeight(DependencyObject o) => (double)o.GetValue(WeightProperty);

    public static void SetWeight(DependencyObject o, double value) => o.SetValue(WeightProperty, value);

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ColumnFit), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid || e.NewValue is not true) return;
        grid.Loaded += (_, _) =>
        {
            var scroller = FindChild<ScrollViewer>(grid);
            if (scroller != null)
            {
                scroller.ScrollChanged += (_, args) =>
                {
                    if (Math.Abs(args.ViewportWidthChange) > 0.5) Fit(grid);
                };
            }
            Fit(grid);
        };
        grid.SizeChanged += (_, args) =>
        {
            if (args.WidthChanged) Fit(grid);
        };
    }

    /// <summary>Recomputes all column widths (also call after showing or hiding a column).</summary>
    public static void Fit(DataGrid grid)
    {
        var scroller = FindChild<ScrollViewer>(grid);
        double available = scroller is { ViewportWidth: > 0 } ? scroller.ViewportWidth : grid.ActualWidth - 16;
        if (available <= 0) return;
        available -= 2;

        var columns = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
        if (columns.Count == 0) return;
        double minimum = columns.Sum(c => c.MinWidth);
        double weights = columns.Sum(GetWeight);
        double extra = Math.Max(0, available - minimum);
        foreach (var c in columns)
        {
            double width = c.MinWidth + (weights > 0 ? extra * GetWeight(c) / weights : 0);
            c.Width = new DataGridLength(Math.Floor(width));
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is T found) return found;
        }
        return null;
    }
}
