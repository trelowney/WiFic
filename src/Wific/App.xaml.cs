// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Wific.Updates;

namespace Wific;

public partial class App : Application
{
    /// <summary>True when started with --demo: invented networks, settings are not saved.</summary>
    public static bool IsDemo { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        Updater.CleanupOldVersion();

        // --demo shows made-up networks (used for the screenshots) and never reads or writes the settings file.
        // --theme dark|light picks the theme for that run.
        IsDemo = e.Args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));
        var settings = IsDemo
            ? new AppSettings { LayoutVersion = AppSettings.CurrentLayoutVersion, CheckForUpdates = false }
            : AppSettings.Load();
        int themeIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--theme", StringComparison.OrdinalIgnoreCase));
        if (IsDemo && themeIndex >= 0 && themeIndex + 1 < e.Args.Length)
            settings.Theme = string.Equals(e.Args[themeIndex + 1], "light", StringComparison.OrdinalIgnoreCase) ? ThemeManager.ModeLight : ThemeManager.ModeDark;
        ThemeManager.Initialize(settings.Theme);
        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "wific – unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
