// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
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
    public bool AlwaysOnTop { get; set; }
    public bool Show24 { get; set; } = true;
    public bool Show5 { get; set; } = true;
    public bool Show6 { get; set; } = true;
    public string? LastAdapter { get; set; }
    public string? SkippedVersion { get; set; }

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
                settings.Normalize();
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
        return new AppSettings();
    }

    public void Save()
    {
        Normalize();
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

    private void Normalize()
    {
        ScanIntervalSeconds = Math.Clamp(ScanIntervalSeconds, 3, 300);
        RemoveAfterMinutes = Math.Clamp(RemoveAfterMinutes, 1, 1440);
    }
}
