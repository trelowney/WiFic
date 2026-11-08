// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Navigation;
using Wific.ViewModels;

namespace Wific;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this, ThemeManager.IsDark);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnLinkClick(object sender, RequestNavigateEventArgs e)
    {
        MainViewModel.OpenUri(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}
