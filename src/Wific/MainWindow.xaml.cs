// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Wific.Updates;
using Wific.ViewModels;

namespace Wific;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _vm = new MainViewModel(settings);
        _vm.RestartRequested += (_, _) => Application.Current.Shutdown();
        DataContext = _vm;

        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += OnLoaded;
        Closed += (_, _) => _vm.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _vm.StartAsync();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow { Owner = this, DataContext = _vm };
        window.ShowDialog();
    }

    private void OnReleaseNotesClick(object sender, RoutedEventArgs e)
    {
        MainViewModel.OpenUri(Updater.ReleasesUrl);
    }
}

/// <summary>Switches the native window caption to dark mode (Windows 10 20H1+ / Windows 11).</summary>
internal static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int enabled = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }
}
