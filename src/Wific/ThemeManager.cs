// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace Wific;

/// <summary>Switches between the dark and light colour dictionaries at runtime.</summary>
public static class ThemeManager
{
    public const string ModeSystem = "System";
    public const string ModeDark = "Dark";
    public const string ModeLight = "Light";

    private static string _mode = ModeSystem;

    public static bool IsDark { get; private set; } = true;

    public static event EventHandler? ThemeChanged;

    public static void Initialize(string mode)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _mode == ModeSystem)
                Application.Current?.Dispatcher.Invoke(() => Apply(_mode));
        };
        Apply(mode);
    }

    public static void Apply(string mode)
    {
        _mode = mode is ModeDark or ModeLight ? mode : ModeSystem;
        bool dark = _mode == ModeDark || (_mode == ModeSystem && !SystemUsesLightTheme());
        IsDark = dark;

        var app = Application.Current;
        if (app == null) return;
        var source = new Uri($"pack://application:,,,/wific;component/Themes/Colors.{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute);
        var merged = app.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Colors.", StringComparison.Ordinal) == true);
        var replacement = new ResourceDictionary { Source = source };
        if (existing != null)
            merged[merged.IndexOf(existing)] = replacement;
        else
            merged.Insert(0, replacement);

        foreach (Window w in app.Windows)
            DarkTitleBar.Apply(w, dark);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }
}
