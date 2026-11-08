// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Wific;

/// <summary>Maps RSSI (dBm) to a traffic-light colour.</summary>
public sealed class RssiToBrushConverter : IValueConverter
{
    private static readonly Brush Excellent = Make(0x3D, 0xD6, 0x8C);
    private static readonly Brush Good = Make(0x9B, 0xD8, 0x4E);
    private static readonly Brush Fair = Make(0xF5, 0xC5, 0x42);
    private static readonly Brush Weak = Make(0xF2, 0x8C, 0x38);
    private static readonly Brush Poor = Make(0xF0, 0x52, 0x5E);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int rssi = value is int i ? i : -100;
        return rssi >= -55 ? Excellent : rssi >= -65 ? Good : rssi >= -72 ? Fair : rssi >= -80 ? Weak : Poor;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Collapsed when the value is null, visible otherwise.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value == null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Maps an issue severity to the theme brush (Critical / Warn / InfoFg).</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is ViewModels.Severity s ? s switch
        {
            ViewModels.Severity.Critical => "Critical",
            ViewModels.Severity.Warning => "Warn",
            _ => "InfoFg",
        } : "InfoFg";
        return System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Visible when the value is false.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public static readonly InverseBoolToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Visible when the integer value is greater than zero.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public static readonly CountToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
