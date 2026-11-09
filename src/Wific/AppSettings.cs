// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Wific;

/// <summary>
/// User settings. Stored portably as wific.settings.json next to the executable; if that folder is
/// read-only, %APPDATA%\wific is used instead.
/// </summary>
public sealed class AppSettings
{
    private const string FileName = "wific.settings.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public bool CheckForUpdates { get; set; } = true;
    public int ScanIntervalSeconds { get; set; } = 5;
    public int RemoveAfterMinutes { get; set; } = 10;
    public bool ShowHiddenNetworks { get; set; } = true;
    public bool HideStale { get; set; }
    public bool LanIdentify { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool Show24 { get; set; } = true;
    public bool Show5 { get; set; } = true;
    public bool Show6 { get; set; } = true;
    public string? LastAdapter { get; set; }
    public string? SkippedVersion { get; set; }

    public string Theme { get; set; } = "System";
    public bool GroupByAccessPoint { get; set; } = true;
    public int HistoryMinutes { get; set; } = 5;
    public bool HistorySelectedOnly { get; set; }
    public int ChannelBand { get; set; }

    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public double BottomPanelHeight { get; set; } = 290;

    public List<ColumnState> Columns { get; set; } = new();

    /// <summary>
    /// Version of the table layout the saved <see cref="Columns"/> belong to. Bump <see cref="CurrentLayoutVersion"/>
    /// whenever columns are added, removed or resized in a new release; older layouts are then discarded.
    /// </summary>
    public int LayoutVersion { get; set; }

    public const int CurrentLayoutVersion = 4;

    public static string? LoadedFrom { get; private set; }

    private static string PortablePath => Path.Combine(AppContext.BaseDirectory, FileName);

    private static string RoamingPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wific", FileName);

    public static AppSettings Load()
    {
        foreach (var path in new[] { PortablePath, RoamingPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
                if (settings == null) continue;
                LoadedFrom = path;
                settings.Normalize(migrate: true);
                return settings;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (JsonException)
            {
            }
        }
        return new AppSettings { LayoutVersion = CurrentLayoutVersion };
    }

    public void Save()
    {
        if (App.IsDemo) return; // demo runs never touch the real settings file
        SaveCore();
    }

    private void SaveCore()
    {
        Normalize(migrate: false);
        string json = JsonSerializer.Serialize(this, Options);
        foreach (var path in new[] { LoadedFrom ?? PortablePath, PortablePath, RoamingPath })
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
                LoadedFrom = path;
                return;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Normalize(bool migrate)
    {
        ScanIntervalSeconds = Math.Clamp(ScanIntervalSeconds, 3, 300);
        RemoveAfterMinutes = Math.Clamp(RemoveAfterMinutes, 1, 1440);
        HistoryMinutes = HistoryMinutes is 1 or 5 or 15 ? HistoryMinutes : 5;
        ChannelBand = Math.Clamp(ChannelBand, 0, 2);
        BottomPanelHeight = Math.Clamp(BottomPanelHeight, 160, 2000);
        Columns ??= new List<ColumnState>();

        // Migration between versions: a layout from another table version is dropped, and so is a layout that
        // contains impossible values (columns squeezed below a usable width, duplicates).
        bool invalid = Columns.Any(c => c.Visible && c.Width > 0 && c.Width < 30)
                       || Columns.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != Columns.Count;
        if ((migrate && LayoutVersion != CurrentLayoutVersion) || invalid)
        {
            Columns = new List<ColumnState>();
            LayoutVersion = CurrentLayoutVersion;
        }

        if (WindowWidth is > 0 and < 600 || WindowHeight is > 0 and < 400)
        {
            WindowWidth = 0;
            WindowHeight = 0;
        }
    }
}

/// <summary>Persisted visibility, order and width of one table column (keyed by header text).</summary>
public sealed class ColumnState
{
    public string Key { get; set; } = "";
    public bool Visible { get; set; } = true;
    public int DisplayIndex { get; set; }
    public double Width { get; set; }
}
