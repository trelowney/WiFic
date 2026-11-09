// WiFic - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Wific.Controls;
using Wific.Updates;
using Wific.ViewModels;

namespace Wific;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly List<ColumnState> _defaultColumns = new();

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _vm = new MainViewModel(settings);
        _vm.RestartRequested += (_, _) => Application.Current.Shutdown();
        DataContext = _vm;
        SurveyPage.Initialize(_vm);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => _vm.Dispose();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        DarkTitleBar.Apply(this, ThemeManager.IsDark);

        // Fit the window to the screen (e.g. 1920x1080 at 125 % scaling is only 1536x816 usable).
        var area = SystemParameters.WorkArea;
        double width = _settings.WindowWidth > 0 ? _settings.WindowWidth : 1440;
        double height = _settings.WindowHeight > 0 ? _settings.WindowHeight : 900;
        Width = Math.Min(width, area.Width - 24);
        Height = Math.Min(height, area.Height - 24);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

        double maxBottom = Math.Max(160, Height * 0.45);
        BottomRow.Height = new GridLength(Math.Min(_settings.BottomPanelHeight, maxBottom));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var c in NetworkGrid.Columns)
        {
            _defaultColumns.Add(new ColumnState
            {
                Key = HeaderOf(c),
                Visible = c.Visibility == Visibility.Visible,
                DisplayIndex = c.DisplayIndex,
                Width = c.Width.IsStar ? 0 : c.Width.DisplayValue,
            });
        }
        ApplyColumns(_settings.Columns);
        if (App.OpenSurveyAtStart) _vm.Page = 1;
        if (App.OpenLanAtStart)
        {
            _vm.Page = 2;
            _ = _vm.Lan.ScanAsync();
        }

        await _vm.StartAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!SurveyPage.CanClose())
        {
            e.Cancel = true;
            return;
        }
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }
        _settings.BottomPanelHeight = BottomRow.ActualHeight;
        _settings.LayoutVersion = AppSettings.CurrentLayoutVersion;
        _settings.Columns = NetworkGrid.Columns.Select(c => new ColumnState
        {
            Key = HeaderOf(c),
            Visible = c.Visibility == Visibility.Visible,
            DisplayIndex = c.DisplayIndex,
            Width = c.Width.IsStar || c.ActualWidth < 30 ? 0 : c.ActualWidth,
        }).ToList();
        _settings.Save();
    }

    // ---- Columns ----

    private static string HeaderOf(DataGridColumn c) => c.Header as string ?? "";

    private void ApplyColumns(IReadOnlyCollection<ColumnState> states)
    {
        if (states.Count == 0) return;
        var byKey = states.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var c in NetworkGrid.Columns)
        {
            if (!byKey.TryGetValue(HeaderOf(c), out var s)) continue;
            c.Visibility = s.Visible ? Visibility.Visible : Visibility.Collapsed;
        }

        int count = NetworkGrid.Columns.Count;
        foreach (var s in states.OrderBy(x => x.DisplayIndex))
        {
            var c = NetworkGrid.Columns.FirstOrDefault(col => HeaderOf(col) == s.Key);
            if (c != null && s.DisplayIndex >= 0 && s.DisplayIndex < count)
                c.DisplayIndex = s.DisplayIndex;
        }
    }

    private void OnColumnMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Items.Clear();
        foreach (var c in NetworkGrid.Columns.OrderBy(c => c.DisplayIndex))
        {
            string key = HeaderOf(c);
            var item = new MenuItem
            {
                Header = key == "!" ? "Issues (!)" : key,
                IsCheckable = true,
                IsChecked = c.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };
            var column = c;
            item.Click += (_, _) =>
            {
                bool visibleCount = NetworkGrid.Columns.Count(x => x.Visibility == Visibility.Visible) > 1;
                if (!item.IsChecked && !visibleCount)
                {
                    item.IsChecked = true;
                    return;
                }
                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                ColumnFit.Fit(NetworkGrid);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset columns" };
        reset.Click += (_, _) =>
        {
            foreach (var c in NetworkGrid.Columns)
            {
                var d = _defaultColumns.FirstOrDefault(x => x.Key == HeaderOf(c));
                if (d == null) continue;
                c.Visibility = d.Visible ? Visibility.Visible : Visibility.Collapsed;
            }
            ApplyColumns(_defaultColumns);
            ColumnFit.Fit(NetworkGrid);
        };
        menu.Items.Add(reset);
    }

    // ---- Selection ----

    private void OnNetworkSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NetworkGrid.SelectedItem != null)
            NetworkGrid.ScrollIntoView(NetworkGrid.SelectedItem);
    }

    private void OnIssueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is IssueRow row)
            _vm.SelectedAccessPoint = row.AccessPoint;
    }

    private void OnShowConnectedClick(object sender, RoutedEventArgs e) => _vm.SelectConnected();

    // ---- Menus ----

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        MainMenu.DataContext = _vm;
        MainMenu.PlacementTarget = MenuButton;
        MainMenu.Placement = PlacementMode.Bottom;
        MainMenu.IsOpen = true;
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

/// <summary>Switches the native window caption between dark and light (Windows 10 20H1+ / Windows 11).</summary>
internal static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int enabled = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }
}
