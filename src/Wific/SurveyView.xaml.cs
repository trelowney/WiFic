// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using Wific.Survey;
using Wific.ViewModels;
using Wific.Wifi;

namespace Wific;

public partial class SurveyView : UserControl
{
    private MainViewModel _vm = null!;
    private SurveyProject? _project;
    private SurveyTarget _target = SurveyTarget.Any;
    private WifiBand? _band;
    private bool _ready;
    private bool _measuring;
    private bool _dirty;
    private Point? _pending;

    public SurveyView()
    {
        InitializeComponent();
        LegendBar.Background = HeatmapRenderer.LegendBrush();
        _ready = true;
        UpdateButtons();
        if (App.IsDemo) SetProject(DemoSurvey.Create());
    }

    public void Initialize(MainViewModel vm) => _vm = vm;

    private Window HostWindow => Window.GetWindow(this) ?? Application.Current.MainWindow!;

    // ---- Project ----

    private void SetProject(SurveyProject project)
    {
        var image = LoadImage(project.Image);
        _project = project;
        PlanImage.Source = image;
        PlanGrid.Width = image.PixelWidth;
        PlanGrid.Height = image.PixelHeight;
        EmptyHint.Visibility = Visibility.Collapsed;
        PlanViewbox.Visibility = Visibility.Visible;
        RefreshTargets();
        Redraw();
        UpdateButtons();
    }

    private static BitmapImage LoadImage(byte[] data)
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(data);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty || _project == null || _project.Points.Count == 0) return true;
        var answer = MessageBox.Show(HostWindow, "Save the current survey first?", "wific – Site survey",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return SaveAs();
        return true;
    }

    private void OnLoadPlanClick(object sender, RoutedEventArgs e)
    {
        if (_measuring || !ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (dialog.ShowDialog(HostWindow) != true) return;
        try
        {
            SetProject(new SurveyProject { Image = File.ReadAllBytes(dialog.FileName) });
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            ShowError("Cannot load the image: " + ex.Message);
        }
    }

    private void OnBlankClick(object sender, RoutedEventArgs e)
    {
        if (_measuring || !ConfirmDiscard()) return;
        SetProject(new SurveyProject { Image = CreateBlankPlan() });
        _dirty = false;
    }

    /// <summary>A light grid "paper" to survey on when no floor plan is available.</summary>
    private static byte[] CreateBlankPlan()
    {
        const int w = 1600, h = 1000;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            var minor = new Pen(new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF4)), 1);
            var major = new Pen(new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xE4)), 1);
            for (int x = 0; x <= w; x += 20) dc.DrawLine(x % 100 == 0 ? major : minor, new Point(x, 0), new Point(x, h));
            for (int y = 0; y <= h; y += 20) dc.DrawLine(y % 100 == 0 ? major : minor, new Point(0, y), new Point(w, y));
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_measuring || !ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = "wific survey (*.wificsurvey)|*.wificsurvey" };
        if (dialog.ShowDialog(HostWindow) != true) return;
        try
        {
            SetProject(SurveyProject.Load(dialog.FileName));
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidDataException or NotSupportedException or FileFormatException)
        {
            ShowError("Cannot open the survey: " + ex.Message);
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveAs();

    private bool SaveAs()
    {
        if (_project == null) return false;
        var dialog = new SaveFileDialog
        {
            Filter = "wific survey (*.wificsurvey)|*.wificsurvey",
            FileName = $"wific-survey-{DateTime.Now:yyyyMMdd-HHmm}.wificsurvey",
        };
        if (dialog.ShowDialog(HostWindow) != true) return false;
        try
        {
            _project.Save(dialog.FileName);
            _dirty = false;
            StatusText.Text = "Survey saved to " + dialog.FileName;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Cannot save the survey: " + ex.Message);
            return false;
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = $"wific-heatmap-{DateTime.Now:yyyyMMdd-HHmm}.png",
        };
        if (dialog.ShowDialog(HostWindow) != true) return;

        int w = (int)PlanGrid.Width, h = (int)PlanGrid.Height;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(PlanGrid), null, new Rect(0, 0, w, h));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        try
        {
            using var file = File.Create(dialog.FileName);
            encoder.Save(file);
            StatusText.Text = "Heatmap exported to " + dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Cannot export the image: " + ex.Message);
        }
    }

    /// <summary>Asks to save unsaved measurements; false when the app should not close yet.</summary>
    public bool CanClose() => !_measuring && ConfirmDiscard();

    // ---- Measuring ----

    private async void OnPlanLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (_project == null || _measuring) return;
        var position = e.GetPosition(PlanGrid);
        _measuring = true;
        _pending = position;
        UpdateButtons();
        StatusText.Text = "Measuring… stay at this spot for a few seconds.";
        Redraw();

        try
        {
            var result = await _vm.MeasureAsync();
            if (result == null || result.Networks.Count == 0)
            {
                ShowError("No networks were measured. Check that the Wi-Fi adapter is enabled and location access is allowed.");
                return;
            }

            var point = new SurveyPoint
            {
                X = position.X,
                Y = position.Y,
                Time = DateTime.Now,
                Samples = result.Networks.Select(n => new SurveySample
                {
                    Bssid = n.Bssid,
                    Ssid = n.Ssid,
                    Rssi = n.Rssi,
                    Band = n.Band,
                    Channel = n.Channel,
                }).ToList(),
            };
            _project.Points.Add(point);
            _dirty = true;
            RefreshTargets();
            StatusText.Text = string.Create(CultureInfo.InvariantCulture,
                $"Point {_project.Points.Count} measured: {point.Samples.Count} networks, strongest {SurveyMath.ValueAt(point, _target, _band)} dBm for the selected network.");
        }
        finally
        {
            _measuring = false;
            _pending = null;
            UpdateButtons();
            Redraw();
        }
    }

    private void OnPlanRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_project == null || _measuring || _project.Points.Count == 0) return;
        var position = e.GetPosition(PlanGrid);
        var nearest = _project.Points.OrderBy(p => (new Point(p.X, p.Y) - position).Length).First();
        if ((new Point(nearest.X, nearest.Y) - position).Length > MarkerRadius * 3) return;
        _project.Points.Remove(nearest);
        _dirty = true;
        RefreshTargets();
        Redraw();
        StatusText.Text = $"Point removed. {_project.Points.Count} points left.";
    }

    // ---- Display ----

    private double MarkerRadius => _project == null ? 8 : Math.Max(5, Math.Max(PlanGrid.Width, PlanGrid.Height) / 110);

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (TargetBox.SelectedItem is SurveyTarget t) _target = t;
        Redraw();
    }

    private void OnBandChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton rb) return;
        int tag = int.Parse((string)rb.Tag, CultureInfo.InvariantCulture);
        _band = tag < 0 ? null : (WifiBand)tag;
        Redraw();
    }

    private void RefreshTargets()
    {
        if (_project == null) return;
        var targets = SurveyMath.Targets(_project.Points);
        var keep = targets.FirstOrDefault(t => t.Ssid == _target.Ssid) ?? SurveyTarget.Any;
        _ready = false;
        TargetBox.ItemsSource = targets;
        TargetBox.SelectedItem = keep;
        _ready = true;
        _target = keep;
    }

    private void Redraw()
    {
        if (_project == null) return;
        var values = _project.Points
            .Select(p => (Position: new Point(p.X, p.Y), Value: (double)SurveyMath.ValueAt(p, _target, _band)))
            .ToList();
        HeatImage.Source = HeatmapRenderer.Render(PlanGrid.Width, PlanGrid.Height, values);

        Markers.Children.Clear();
        double r = MarkerRadius;
        foreach (var (pos, value) in values)
            AddMarker(pos, r, value <= SurveyMath.NoSignal ? "–" : ((int)value).ToString(CultureInfo.InvariantCulture), false);
        if (_pending is Point pending)
            AddMarker(pending, r, "…", true);
    }

    private void AddMarker(Point pos, double r, string label, bool pending)
    {
        var dot = new Ellipse
        {
            Width = r * 2,
            Height = r * 2,
            Fill = pending ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(0xE6, 0x1A, 0x1F, 0x2B)),
            Stroke = pending ? new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF)) : Brushes.White,
            StrokeThickness = Math.Max(1.5, r / 4),
        };
        Canvas.SetLeft(dot, pos.X - r);
        Canvas.SetTop(dot, pos.Y - r);
        Markers.Children.Add(dot);

        var text = new TextBlock
        {
            Text = label,
            FontSize = Math.Max(10, r * 1.15),
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x14, 0x1C)),
            Padding = new Thickness(r * 0.3, 0, r * 0.3, 0),
        };
        Canvas.SetLeft(text, pos.X + r * 1.3);
        Canvas.SetTop(text, pos.Y - r * 0.9);
        Markers.Children.Add(text);
    }

    private void UpdateButtons()
    {
        bool hasProject = _project != null;
        SaveButton.IsEnabled = hasProject && !_measuring;
        ExportButton.IsEnabled = hasProject && !_measuring;
        PlanGrid.Cursor = _measuring ? Cursors.Wait : Cursors.Cross;
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        MessageBox.Show(HostWindow, message, "wific – Site survey", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
