// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wific.Updates;

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? AssetUrl, long AssetSize, string? Sha256);

/// <summary>
/// Portable self-updater backed by GitHub Releases. The running exe is renamed to *.old (Windows allows
/// renaming a running image), the new exe takes its place and is started; the old file is deleted on next start.
/// </summary>
public static class Updater
{
    public const string Repository = "trelowney/wific";
    public const string ProjectUrl = "https://github.com/" + Repository;
    public const string ReleasesUrl = ProjectUrl + "/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("wific-updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Version CurrentVersion =>
        typeof(Updater).Assembly.GetName().Version is Version v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    public static string AssetName => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? "wific-win-arm64.exe"
        : "wific-win-x64.exe";

    /// <summary>Removes the leftover from a previous update.</summary>
    public static void CleanupOldVersion()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Returns information about a newer release, or null when up to date or offline.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            if (latest <= CurrentVersion) return null;

            string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesUrl : ReleasesUrl;
            string? url = null;
            long size = 0;
            string? sha = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    url = asset.GetProperty("browser_download_url").GetString();
                    size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        var digest = d.GetString() ?? "";
                        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            sha = digest[7..];
                    }
                }
            }
            return new UpdateInfo(latest, tag, page, url, size, sha);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Downloads the update, swaps the executable and starts the new version.</summary>
    /// <returns>true when the new version was started and this instance should exit.</returns>
    public static async Task<bool> InstallAsync(UpdateInfo update, IProgress<double> progress, CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the executable path.");
        if (update.AssetUrl == null)
            throw new InvalidOperationException($"The release does not contain {AssetName}.");

        string dir = Path.GetDirectoryName(exe)!;
        string temp = Path.Combine(dir, Path.GetFileName(exe) + ".download");
        string old = exe + ".old";

        using (var response = await Http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.AssetSize;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress.Report((double)done / total);
            }
        }

        if (update.AssetSize > 0 && new FileInfo(temp).Length != update.AssetSize)
        {
            File.Delete(temp);
            throw new IOException("The downloaded file has an unexpected size.");
        }

        if (update.Sha256 != null)
        {
            string actual;
            await using (var fs = File.OpenRead(temp))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                throw new IOException("The downloaded file failed the SHA-256 check.");
            }
        }

        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(temp, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir });
        return true;
    }

    public static bool IsWritableLocation()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return false;
            string probe = Path.Combine(Path.GetDirectoryName(exe)!, ".wific-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
