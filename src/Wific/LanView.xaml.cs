// WiFic - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wific.ViewModels;

namespace Wific;

/// <summary>The "Network devices" page: an IP scanner for the local network or any range.</summary>
public partial class LanView : UserControl
{
    public LanView()
    {
        InitializeComponent();
    }

    private void OnRangeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not LanViewModel vm || vm.IsScanning) return;
        _ = vm.ScanAsync();
        e.Handled = true;
    }

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url }) MainViewModel.OpenUri(url);
    }
}
