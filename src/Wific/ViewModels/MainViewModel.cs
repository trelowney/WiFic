// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Wific.Native;
using Wific.Updates;
using Wific.Wifi;

namespace Wific.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dictionary<string, AccessPointViewModel> _byBssid = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _clock;
    private readonly CancellationTokenSource _cts = new();
    private readonly AppSettings _settings;
    private WifiScanner? _scanner;
    private OuiDatabase _oui = new();
    private bool _resetPending;

    private WlanInterface? _selectedInterface;
    private AccessPointViewModel? _selected;
    private string _search = "";
    private bool _isPaused;
    private bool _isScanning;
    private string _status = "Starting…";
    private string? _error;
    private bool _locationProblem;
    private int _revision;
    private DateTime? _lastScan;

    private UpdateInfo? _update;
    private bool _isUpdating;
    private double _updateProgress;
    private string? _updateMessage;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;

        View = CollectionViewSource.GetDefaultView(AccessPoints);
        View.Filter = Filter;
        View.SortDescriptions.Add(new SortDescription(nameof(AccessPointViewModel.Rssi), ListSortDirection.Descending));
        if (View is ICollectionViewLiveShaping live)
        {
            live.IsLiveSorting = true;
            live.IsLiveFiltering = true;
            live.LiveFilteringProperties.Add(nameof(AccessPointViewModel.IsStale));
            live.IsLiveGrouping = true;
            live.LiveGroupingProperties.Add(nameof(AccessPointViewModel.Group));
        }
        ApplyGrouping();

        ToggleScanCommand = new RelayCommand(() => IsPaused = !IsPaused);
        ExportCommand = new RelayCommand(Export, () => AccessPoints.Count > 0);
        ClearCommand = new RelayCommand(Clear);
        OpenLocationSettingsCommand = new RelayCommand(() => OpenUri("ms-settings:privacy-location"));
        InstallUpdateCommand = new RelayCommand(() => _ = InstallUpdateAsync(), () => _update != null && !_isUpdating);
        DismissUpdateCommand = new RelayCommand(DismissUpdate, () => !_isUpdating);
        SkipUpdateCommand = new RelayCommand(SkipUpdate, () => _update != null && !_isUpdating);
        CheckUpdatesNowCommand = new RelayCommand(() => _ = CheckForUpdatesAsync(true), () => !_isUpdating);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Tick();
        _clock.Start();
    }

    /// <summary>Raised when an update was installed and this instance should close.</summary>
    public event EventHandler? RestartRequested;

    public ObservableCollection<WlanInterface> Interfaces { get; } = new();
    public ObservableCollection<AccessPointViewModel> AccessPoints { get; } = new();
    public ICollectionView View { get; }
    public IReadOnlyList<int> Intervals { get; } = new[] { 3, 5, 10, 30, 60 };
    public IReadOnlyList<int> RemoveAfterOptions { get; } = new[] { 1, 2, 5, 10, 30, 60 };
    public IReadOnlyList<string> Themes { get; } = new[] { ThemeManager.ModeSystem, ThemeManager.ModeDark, ThemeManager.ModeLight };

    public ConnectionViewModel Connection { get; } = new();
    public ObservableCollection<IssueRow> Issues { get; } = new();
    private readonly Dictionary<string, ApGroup> _groups = new(StringComparer.Ordinal);
    private ChannelAdvice?[] _advice = new ChannelAdvice?[3];
    private bool _connectionBusy;

    public int IssueCount => Issues.Count;
    public string IssuesHeader => Issues.Count == 0 ? "Issues" : $"Issues ({Issues.Count})";

    public ChannelAdvice? Advice24 => _advice[0];
    public ChannelAdvice? Advice5 => _advice[1];
    public ChannelAdvice? Advice6 => _advice[2];
    public ChannelAdvice? CurrentAdvice => _advice[ChannelBand];
    public string AdviceText => Analysis.FormatAdvice(CurrentAdvice)
        + (Connection.IsConnected && CurrentAdvice != null ? " · your AP excluded" : "");

    public WifiBand CurrentBand => (WifiBand)ChannelBand;

    public int ChannelBand
    {
        get => _settings.ChannelBand;
        set
        {
            _settings.ChannelBand = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentBand));
            OnPropertyChanged(nameof(CurrentAdvice));
            OnPropertyChanged(nameof(AdviceText));
            OnPropertyChanged(nameof(IsBand24));
            OnPropertyChanged(nameof(IsBand5));
            OnPropertyChanged(nameof(IsBand6));
        }
    }

    // Two-way friendly flags for the segmented band selector.
    public bool IsBand24 { get => ChannelBand == 0; set { if (value) ChannelBand = 0; } }
    public bool IsBand5 { get => ChannelBand == 1; set { if (value) ChannelBand = 1; } }
    public bool IsBand6 { get => ChannelBand == 2; set { if (value) ChannelBand = 2; } }

    public int HistoryMinutes
    {
        get => _settings.HistoryMinutes;
        set { _settings.HistoryMinutes = value; SettingChanged(); NotifyHistory(); }
    }

    public bool IsHistory1 { get => HistoryMinutes == 1; set { if (value) HistoryMinutes = 1; } }
    public bool IsHistory5 { get => HistoryMinutes == 5; set { if (value) HistoryMinutes = 5; } }
    public bool IsHistory15 { get => HistoryMinutes == 15; set { if (value) HistoryMinutes = 15; } }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(IsHistory1));
        OnPropertyChanged(nameof(IsHistory5));
        OnPropertyChanged(nameof(IsHistory15));
    }

    public bool HistorySelectedOnly
    {
        get => _settings.HistorySelectedOnly;
        set { _settings.HistorySelectedOnly = value; SettingChanged(); }
    }

    public bool GroupByAccessPoint
    {
        get => _settings.GroupByAccessPoint;
        set
        {
            _settings.GroupByAccessPoint = value;
            ApplyGrouping();
            SettingChanged();
        }
    }

    public string Theme
    {
        get => _settings.Theme;
        set
        {
            _settings.Theme = value;
            ThemeManager.Apply(value);
            SettingChanged();
        }
    }

    private void ApplyGrouping()
    {
        View.GroupDescriptions.Clear();
        if (_settings.GroupByAccessPoint)
            View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AccessPointViewModel.Group)));
    }

    /// <summary>Selects the access point the adapter is connected to.</summary>
    public void SelectConnected()
    {
        var ap = AccessPoints.FirstOrDefault(a => a.IsConnected);
        if (ap != null) SelectedAccessPoint = ap;
    }

    private void Analyze()
    {
        var all = AccessPoints.ToList();

        // Physical AP groups
        var keys = Analysis.GroupAccessPoints(all);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cluster in all.GroupBy(a => keys[a]))
        {
            if (!_groups.TryGetValue(cluster.Key, out var group))
            {
                group = new ApGroup(cluster.Key);
                _groups[cluster.Key] = group;
            }
            used.Add(cluster.Key);
            var members = cluster.ToList();
            foreach (var ap in members) ap.Group = group;
            group.Update(members);
        }
        foreach (var stale in _groups.Keys.Where(k => !used.Contains(k)).ToList())
            _groups.Remove(stale);

        // Issues
        Issues.Clear();
        foreach (var ap in all)
        {
            ap.Issues = ap.IsStale ? Array.Empty<Issue>() : Analysis.DetectIssues(ap, all);
            foreach (var issue in ap.Issues)
                Issues.Add(new IssueRow(issue.Severity, ap, issue.Text));
        }
        var sorted = Issues.OrderByDescending(i => i.Severity).ThenBy(i => i.Ssid, StringComparer.CurrentCulture).ToList();
        Issues.Clear();
        foreach (var row in sorted) Issues.Add(row);
        OnPropertyChanged(nameof(IssueCount));
        OnPropertyChanged(nameof(IssuesHeader));

        // Channel advice
        _advice = new[]
        {
            Analysis.AdviseChannel(WifiBand.Band2_4, all),
            Analysis.AdviseChannel(WifiBand.Band5, all),
            Analysis.AdviseChannel(WifiBand.Band6, all),
        };
        if (!all.Any(a => a.Band == WifiBand.Band6)) _advice[2] = null;
        OnPropertyChanged(nameof(Advice24));
        OnPropertyChanged(nameof(Advice5));
        OnPropertyChanged(nameof(Advice6));
        OnPropertyChanged(nameof(CurrentAdvice));
        OnPropertyChanged(nameof(AdviceText));

        Connection.AccessPoint = all.FirstOrDefault(a => a.IsConnected);
    }

    private async System.Threading.Tasks.Task PollConnectionAsync()
    {
        var scanner = _scanner;
        var iface = SelectedInterface;
        if (scanner == null || iface == null || _connectionBusy) return;
        _connectionBusy = true;
        try
        {
            var data = await System.Threading.Tasks.Task.Run(() => scanner.ReadConnection(iface.Id));
            Connection.Update(data);
            if (data is { } d)
            {
                string bssid = string.Join(":", d.Connection.Bssid.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
                Connection.AccessPoint = AccessPoints.FirstOrDefault(a => string.Equals(a.Bssid, bssid, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Connection.AccessPoint = null;
            }
        }
        catch (WlanException)
        {
            Connection.Update(null);
        }
        finally
        {
            _connectionBusy = false;
        }
    }

    public ICommand ToggleScanCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand OpenLocationSettingsCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand DismissUpdateCommand { get; }
    public ICommand SkipUpdateCommand { get; }
    public ICommand CheckUpdatesNowCommand { get; }

    public string AppVersion { get; } = GetVersion();

    public WlanInterface? SelectedInterface
    {
        get => _selectedInterface;
        set
        {
            if (Set(ref _selectedInterface, value))
            {
                _resetPending = true;
                if (value != null)
                {
                    _settings.LastAdapter = value.Id.ToString();
                    _settings.Save();
                }
            }
        }
    }

    public AccessPointViewModel? SelectedAccessPoint
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                Revision++;
        }
    }

    // ---- Settings-backed options ----

    public bool Show24 { get => _settings.Show24; set { _settings.Show24 = value; SettingChanged(); } }
    public bool Show5 { get => _settings.Show5; set { _settings.Show5 = value; SettingChanged(); } }
    public bool Show6 { get => _settings.Show6; set { _settings.Show6 = value; SettingChanged(); } }
    public bool HideStale { get => _settings.HideStale; set { _settings.HideStale = value; SettingChanged(); } }
    public bool ShowHiddenNetworks { get => _settings.ShowHiddenNetworks; set { _settings.ShowHiddenNetworks = value; SettingChanged(); } }
    public bool AlwaysOnTop { get => _settings.AlwaysOnTop; set { _settings.AlwaysOnTop = value; SettingChanged(); } }
    public bool CheckForUpdates { get => _settings.CheckForUpdates; set { _settings.CheckForUpdates = value; SettingChanged(); } }
    public int ScanInterval { get => _settings.ScanIntervalSeconds; set { _settings.ScanIntervalSeconds = value; SettingChanged(); } }
    public int RemoveAfterMinutes { get => _settings.RemoveAfterMinutes; set { _settings.RemoveAfterMinutes = value; SettingChanged(); } }

    public string SettingsLocation => AppSettings.LoadedFrom ?? "Not saved yet (defaults)";

    private void SettingChanged([CallerMemberName] string? name = null)
    {
        _settings.Save();
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(SettingsLocation));
        RefreshFilter();
    }

    public string SearchText
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) RefreshFilter(); }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (Set(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(ScanButtonText));
                UpdateStatus();
            }
        }
    }

    public string ScanButtonText => _isPaused ? "Resume" : "Pause";

    public bool IsScanning
    {
        get => _isScanning;
        private set => Set(ref _isScanning, value);
    }

    public string StatusText
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string? ErrorText
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public bool LocationProblem
    {
        get => _locationProblem;
        private set => Set(ref _locationProblem, value);
    }

    /// <summary>Incremented whenever graphs need to be redrawn.</summary>
    public int Revision
    {
        get => _revision;
        private set => Set(ref _revision, value);
    }

    public int Count24 => AccessPoints.Count(a => a.Band == WifiBand.Band2_4 && !a.IsStale);
    public int Count5 => AccessPoints.Count(a => a.Band == WifiBand.Band5 && !a.IsStale);
    public int Count6 => AccessPoints.Count(a => a.Band == WifiBand.Band6 && !a.IsStale);
    public int OuiCount => _oui.Count;

    // ---- Updates ----

    public bool HasUpdate => _update != null;
    public string UpdateText => _update == null ? "" : $"wific {_update.Tag} is available - you are running {AppVersion}.";

    public bool IsUpdating
    {
        get => _isUpdating;
        private set => Set(ref _isUpdating, value);
    }

    public double UpdateProgress
    {
        get => _updateProgress;
        private set => Set(ref _updateProgress, value);
    }

    public string? UpdateMessage
    {
        get => _updateMessage;
        private set => Set(ref _updateMessage, value);
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (manual) UpdateMessage = "Checking for updates…";
        var info = await Updater.CheckAsync(_cts.Token);
        if (info != null && !manual && string.Equals(info.Tag, _settings.SkippedVersion, StringComparison.OrdinalIgnoreCase))
            info = null;
        _update = info;
        if (manual)
            UpdateMessage = info == null ? $"You are running the latest version ({AppVersion})." : $"Version {info.Tag} is available.";
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void DismissUpdate()
    {
        _update = null;
        OnPropertyChanged(nameof(HasUpdate));
    }

    private void SkipUpdate()
    {
        if (_update == null) return;
        _settings.SkippedVersion = _update.Tag;
        _settings.Save();
        DismissUpdate();
    }

    private async Task InstallUpdateAsync()
    {
        var update = _update;
        if (update == null) return;
        if (update.AssetUrl == null || !Updater.IsWritableLocation())
        {
            // The exe cannot be replaced in place (e.g. a protected folder) - let the user download it manually.
            OpenUri(update.PageUrl);
            return;
        }

        IsUpdating = true;
        UpdateMessage = $"Downloading {update.Tag}…";
        try
        {
            var progress = new Progress<double>(p => UpdateProgress = p * 100);
            if (await Updater.InstallAsync(update, progress, _cts.Token))
                RestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Application is closing.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException
                                       or InvalidOperationException or Win32Exception)
        {
            UpdateMessage = "Update failed: " + ex.Message;
        }
        finally
        {
            IsUpdating = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ---- Scanning ----

    public async Task StartAsync()
    {
        _oui = await Task.Run(() => OuiDatabase.LoadEmbedded());
        OnPropertyChanged(nameof(OuiCount));

        if (_settings.CheckForUpdates)
            _ = CheckForUpdatesAsync(false);

        try
        {
            _scanner = new WifiScanner();
            var scanner = _scanner;
            var list = await Task.Run(() => scanner.GetInterfaces());
            foreach (var i in list) Interfaces.Add(i);
            SelectedInterface = Interfaces.FirstOrDefault(i => i.Id.ToString() == _settings.LastAdapter)
                                ?? Interfaces.FirstOrDefault();
            if (SelectedInterface == null)
                ErrorText = "No Wi-Fi adapter found. Make sure the wireless adapter is enabled.";
        }
        catch (WlanException ex)
        {
            ErrorText = "The WLAN service is not available: " + ex.Message;
            StatusText = "Not scanning";
            return;
        }

        await RunLoopAsync(_cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.Now;
            var iface = SelectedInterface;
            if (!IsPaused && iface != null && _scanner != null)
            {
                if (_resetPending) Clear();
                IsScanning = true;
                UpdateStatus();
                try
                {
                    if (first)
                    {
                        // Show what Windows already has cached right away.
                        await ReadAndApplyAsync(iface);
                        first = false;
                    }

                    await TriggerAsync(iface);
                    await Task.Delay(3500, ct);
                    if (ReferenceEquals(iface, SelectedInterface))
                        await ReadAndApplyAsync(iface);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    IsScanning = false;
                    UpdateStatus();
                }
            }

            var wait = TimeSpan.FromSeconds(ScanInterval) - (DateTime.Now - started);
            if (wait < TimeSpan.FromMilliseconds(500)) wait = TimeSpan.FromMilliseconds(500);
            try
            {
                await Task.Delay(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TriggerAsync(WlanInterface iface)
    {
        var scanner = _scanner!;
        try
        {
            await Task.Run(() => scanner.TriggerScan(iface.Id));
        }
        catch (WlanException ex)
        {
            if (ex.ErrorCode == WlanApi.ERROR_ACCESS_DENIED) ShowLocationProblem();
            // Other errors (e.g. a scan already running) are not fatal; cached results are still read.
        }
    }

    private async Task ReadAndApplyAsync(WlanInterface iface)
    {
        var scanner = _scanner!;
        try
        {
            var result = await Task.Run(() => scanner.Read(iface.Id));
            Apply(result);
            ErrorText = null;
            LocationProblem = false;
        }
        catch (WlanException ex)
        {
            if (ex.ErrorCode == WlanApi.ERROR_ACCESS_DENIED) ShowLocationProblem();
            else ErrorText = ex.Message;
        }
    }

    private void ShowLocationProblem()
    {
        LocationProblem = true;
        ErrorText = "Windows blocked access to Wi-Fi scan results. Turn on Location services and allow desktop apps to access your location.";
    }

    private void Apply(ScanResult result)
    {
        var now = DateTime.Now;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in result.Networks)
        {
            if (!seen.Add(info.Bssid)) continue;
            var (vendor, derived) = _oui.Resolve(info.Mac);
            if (_byBssid.TryGetValue(info.Bssid, out var ap))
            {
                ap.Update(info, vendor, derived, now);
            }
            else
            {
                ap = new AccessPointViewModel(info, vendor, derived, now);
                _byBssid[info.Bssid] = ap;
                AccessPoints.Add(ap);
            }
            ap.IsStale = false;
            ap.IsConnected = string.Equals(info.Bssid, result.ConnectedBssid, StringComparison.OrdinalIgnoreCase);
        }

        var removeAfter = TimeSpan.FromMinutes(_settings.RemoveAfterMinutes);
        foreach (var ap in AccessPoints.ToList())
        {
            if (seen.Contains(ap.Bssid)) continue;
            ap.IsStale = true;
            ap.IsConnected = false;
            if (now - ap.LastSeen > removeAfter)
            {
                if (ReferenceEquals(ap, SelectedAccessPoint)) SelectedAccessPoint = null;
                AccessPoints.Remove(ap);
                _byBssid.Remove(ap.Bssid);
            }
        }

        Analyze();
        _lastScan = now;
        OnPropertyChanged(nameof(Count24));
        OnPropertyChanged(nameof(Count5));
        OnPropertyChanged(nameof(Count6));
        Revision++;
    }

    private void Tick()
    {
        var now = DateTime.Now;
        foreach (var ap in AccessPoints) ap.RefreshTime(now);
        UpdateStatus();
        if (!IsPaused) _ = PollConnectionAsync();
    }

    private void UpdateStatus()
    {
        int active = AccessPoints.Count(a => !a.IsStale);
        string state = IsPaused ? "Paused" : IsScanning ? "Scanning…" : "Idle";
        string last = _lastScan is DateTime t ? $" · last update {(int)(DateTime.Now - t).TotalSeconds} s ago" : "";
        StatusText = $"{state} · {active} active BSSIDs · {AccessPoints.Count} total{last}";
    }

    private bool Filter(object item)
    {
        if (item is not AccessPointViewModel ap) return false;
        if (ap.Band == WifiBand.Band2_4 && !Show24) return false;
        if (ap.Band == WifiBand.Band5 && !Show5) return false;
        if (ap.Band == WifiBand.Band6 && !Show6) return false;
        if (HideStale && ap.IsStale) return false;
        if (!ShowHiddenNetworks && ap.IsHidden) return false;
        if (_search.Length > 0)
        {
            var s = _search;
            return ap.Ssid.Contains(s, StringComparison.OrdinalIgnoreCase)
                || ap.Bssid.Contains(s, StringComparison.OrdinalIgnoreCase)
                || ap.Vendor.Contains(s, StringComparison.OrdinalIgnoreCase)
                || ap.Device.Contains(s, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    private void RefreshFilter()
    {
        View.Refresh();
        Revision++;
    }

    private void Clear()
    {
        _resetPending = false;
        SelectedAccessPoint = null;
        AccessPoints.Clear();
        _byBssid.Clear();
        _groups.Clear();
        Issues.Clear();
        OnPropertyChanged(nameof(IssuesHeader));
        Revision++;
    }

    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"wific-scan-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("SSID,BSSID,Vendor,RSSI (dBm),Signal (%),Band,Frequency (MHz),Channel,Center channel,Width,Standard,Protocol,Security,Ciphers,PMF,Max rate (Mbps),Spatial streams,Clients,Utilization (%),Uptime,Country,Device,Roaming,BSS color,Last seen");
        foreach (var ap in View.Cast<AccessPointViewModel>())
        {
            var fields = new object?[]
            {
                ap.Ssid, ap.Bssid, ap.VendorText, ap.Rssi, ap.LinkQuality, ap.BandText, ap.FrequencyMhz, ap.Channel,
                ap.CenterChannel, ap.WidthText, ap.Standard, ap.Protocol, ap.Security, ap.Ciphers, ap.Pmf,
                ap.MaxRateMbps, ap.SpatialStreams, ap.Info.StationCount, ap.Info.ChannelUtilization, ap.UptimeText,
                ap.Country, ap.Device, ap.Roaming, ap.BssColorText, ap.LastSeen.ToString("s", CultureInfo.InvariantCulture),
            };
            sb.AppendLine(string.Join(",", fields.Select(Csv)));
        }

        try
        {
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText = "Export failed: " + ex.Message;
        }
    }

    private static string Csv(object? value)
    {
        string s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
    }

    public static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // Nothing sensible to do if the shell cannot open the link.
        }
    }

    private static string GetVersion()
    {
        var attr = typeof(MainViewModel).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault();
        var v = attr?.InformationalVersion ?? "0.0.0";
        int plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? v[..plus] : v;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _clock.Stop();
        _scanner?.Dispose();
        _cts.Dispose();
    }
}
