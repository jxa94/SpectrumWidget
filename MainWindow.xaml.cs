using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;
using Windows.Storage.Streams;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace SpectrumWidget;

public partial class MainWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "SpectrumWidget";

    readonly Settings _settings;
    readonly AudioCapture _audio = new();
    readonly MediaWatcher _media = new();
    float[] _bands;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastFrame;

    TrayIcon? _tray;
    ContextMenu _menu = null!;
    readonly List<(MenuItem item, Func<bool> isChecked)> _checks = new();
    readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    // 播放状态
    string? _coverHash;
    int _propsVersion;
    TimeSpan _tlPos, _tlEnd;
    DateTimeOffset _tlStamp;
    bool _playing;
    double _rate = 1;
    bool _canSeek;
    int _lastPosSec = -1, _lastDurSec = -1;

    public MainWindow(Settings settings)
    {
        _settings = settings;
        _bands = new float[Math.Clamp(settings.BarCount, 16, 128)];
        InitializeComponent();

        Topmost = settings.Topmost;
        ApplyScale();
        ApplyOpacity();
        Spectrum.Mode = settings.Style;

        Card.SizeChanged += (_, e) =>
            BgLayer.Clip = new RectangleGeometry(new Rect(e.NewSize), 22, 22);

        BuildMenu();
        MouseLeftButtonDown += OnDragStart;
        MouseRightButtonUp += (_, e) => { ShowMenu(fromTray: false); e.Handled = true; };
        LocationChanged += (_, _) => { _saveTimer.Stop(); _saveTimer.Start(); };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            if (Left > -5000) { _settings.Left = Left; _settings.Top = Top; _settings.Save(); }
        };

        SourceInitialized += (_, _) => ApplyExStyle();
        Loaded += OnLoaded;
        Closed += (_, _) => Cleanup();

        _tray = new TrayIcon(ToggleVisible, () => ShowMenu(fromTray: true));
        SetIdle();
    }

    // ───────────── 启动 / 位置 ─────────────

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceWindow();

        _audio.Start();
        CompositionTarget.Rendering += OnFrame;

        _media.Changed += kind => Dispatcher.InvokeAsync(() => OnMediaChanged(kind));
        try
        {
            await _media.InitAsync();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            SubText.Text = "无法访问系统媒体控件";
        }
        _pollTimer.Tick += (_, _) => { _media.Pick(); RefreshTimeline(); };
        _pollTimer.Start();
    }

    void PlaceWindow()
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        double w = ActualWidth, h = ActualHeight;
        bool ok = _settings.Left is double l && _settings.Top is double t
                  && l + w * 0.3 > vl && l + w * 0.7 < vl + vw && t + 20 > vt && t + h * 0.5 < vt + vh;
        if (ok)
        {
            Left = _settings.Left!.Value;
            Top = _settings.Top!.Value;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - w;
            Top = wa.Bottom - h;
        }
    }

    void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && Spectrum.IsMouseOver)
        {
            CycleStyle();
            return;
        }
        if (_settings.Locked) return;
        try { DragMove(); } catch { }
    }

    void ToggleVisible()
    {
        if (IsVisible) Hide();
        else { Show(); Activate(); }
    }

    // ───────────── 每帧 ─────────────

    void OnFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastFrame;
        if (dt < 0.012) return; // 高刷屏上限约 80fps，省电
        _lastFrame = now;
        dt = Math.Min(dt, 0.1);
        if (!IsVisible) return;

        _audio.GetBands(_bands, dt);
        Spectrum.Update(_bands, dt);
        UpdateProgress();
    }

    void UpdateProgress()
    {
        if (_tlEnd <= TimeSpan.Zero)
        {
            FillScale.ScaleX = 0;
            return;
        }
        var pos = _tlPos;
        if (_playing)
            pos += TimeSpan.FromTicks((long)((DateTimeOffset.Now - _tlStamp).Ticks * _rate));
        if (pos > _tlEnd) pos = _tlEnd;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;

        FillScale.ScaleX = pos.TotalSeconds / _tlEnd.TotalSeconds;
        int ps = (int)pos.TotalSeconds, ds = (int)_tlEnd.TotalSeconds;
        if (ps != _lastPosSec) { PosText.Text = Fmt(pos); _lastPosSec = ps; }
        if (ds != _lastDurSec) { DurText.Text = Fmt(_tlEnd); _lastDurSec = ds; }
    }

    static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ───────────── 媒体信息 ─────────────

    void OnMediaChanged(MediaChange kind)
    {
        switch (kind)
        {
            case MediaChange.Session:
            case MediaChange.Properties:
                _ = RefreshPropertiesAsync();
                RefreshPlayback();
                RefreshTimeline();
                break;
            case MediaChange.Playback:
                RefreshPlayback();
                RefreshTimeline();
                break;
            case MediaChange.Timeline:
                RefreshTimeline();
                break;
        }
    }

    void SetIdle()
    {
        TitleText.Text = "没有正在播放的媒体";
        ArtistText.Text = "在任意播放器里放点音乐吧";
        SubText.Text = "";
        TitleText.ToolTip = null;
        _tlEnd = TimeSpan.Zero; _tlPos = TimeSpan.Zero; _playing = false;
        PosText.Text = DurText.Text = "0:00";
        _lastPosSec = _lastDurSec = -1;
        PlayBtn.Content = "";
        PrevBtn.IsEnabled = PlayBtn.IsEnabled = NextBtn.IsEnabled = false;
        SetCover(null, null);
    }

    async Task RefreshPropertiesAsync()
    {
        int ver = ++_propsVersion;
        var s = _media.Session;
        if (s == null) { SetIdle(); return; }
        try
        {
            var p = await s.TryGetMediaPropertiesAsync();
            if (ver != _propsVersion) return;
            if (p == null) return;

            string title = string.IsNullOrWhiteSpace(p.Title) ? "未知曲目" : p.Title;
            string artist = !string.IsNullOrWhiteSpace(p.Artist) ? p.Artist : p.AlbumArtist ?? "";
            string app = MediaWatcher.PrettyAppName(s.SourceAppUserModelId);
            string sub = string.IsNullOrWhiteSpace(p.AlbumTitle) ? app : $"{p.AlbumTitle}  ·  {app}";

            TitleText.Text = title;
            TitleText.ToolTip = title;
            ArtistText.Text = artist;
            SubText.Text = sub;

            if (p.Thumbnail == null) { SetCover(null, null); return; }

            byte[]? bytes = await ReadThumbnailAsync(p.Thumbnail);
            if (ver != _propsVersion) return;
            if (bytes == null || bytes.Length == 0) { SetCover(null, null); return; }

            string hash = Convert.ToHexString(SHA1.HashData(bytes));
            if (hash == _coverHash) return;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.DecodePixelWidth = 400;
            bmp.EndInit();
            bmp.Freeze();
            SetCover(bmp, hash);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference thumb)
    {
        try
        {
            using var ras = await thumb.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    void SetCover(BitmapSource? bmp, string? hash)
    {
        if (bmp == null && _coverHash == null && CoverBorder.Opacity == 0) return;
        _coverHash = hash;

        var fade = new Duration(TimeSpan.FromMilliseconds(bmp == null ? 250 : 450));
        if (bmp == null)
        {
            CoverBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fade));
            BgCover.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fade));
            ApplyAccent(ColorUtil.DefaultA, ColorUtil.DefaultB);
            return;
        }

        CoverBrush.ImageSource = bmp;
        BgBrush.ImageSource = bmp;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        CoverBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, fade) { EasingFunction = ease });
        BgCover.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.6, fade) { EasingFunction = ease });

        try
        {
            var (a, b) = ColorUtil.Extract(bmp);
            ApplyAccent(a, b);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    void ApplyAccent(Color a, Color b)
    {
        Spectrum.SetAccent(a, b);
        var brush = new SolidColorBrush(a);
        brush.Freeze();
        Application.Current.Resources["AccentBrush"] = brush;
    }

    void RefreshPlayback()
    {
        var s = _media.Session;
        if (s == null) { SetIdle(); return; }
        try
        {
            var info = s.GetPlaybackInfo();
            _playing = info.PlaybackStatus == PlaybackStatus.Playing;
            _rate = info.PlaybackRate ?? 1.0;
            if (_rate <= 0) _rate = 1;
            var c = info.Controls;
            PrevBtn.IsEnabled = c.IsPreviousEnabled;
            NextBtn.IsEnabled = c.IsNextEnabled;
            PlayBtn.IsEnabled = c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled;
            PlayBtn.Content = _playing ? "" : "";
            PlayBtn.ToolTip = _playing ? "暂停" : "播放";
            _canSeek = c.IsPlaybackPositionEnabled;
            SeekArea.Cursor = _canSeek ? Cursors.Hand : null;
        }
        catch (Exception ex) { App.Log(ex); }
    }

    void RefreshTimeline()
    {
        var s = _media.Session;
        if (s == null) return;
        try
        {
            var tl = s.GetTimelineProperties();
            var end = tl.EndTime - tl.StartTime;
            var pos = tl.Position - tl.StartTime;
            var now = DateTimeOffset.Now;
            var stamp = tl.LastUpdatedTime;
            // 有些播放器的 LastUpdatedTime 不靠谱：离谱的话就当作“刚刚更新”
            if (stamp > now || now - stamp > TimeSpan.FromHours(6)) stamp = now;

            // 播放中重复拿到同一份（旧的）时间线时不要回跳
            if (_playing && end == _tlEnd && pos == _tlPos && stamp == _tlStamp) return;
            _tlEnd = end;
            _tlPos = pos;
            _tlStamp = stamp;
        }
        catch { }
    }

    // ───────────── 控制按钮 ─────────────

    async void Prev_Click(object sender, RoutedEventArgs e)
    {
        try { if (_media.Session is { } s) await s.TrySkipPreviousAsync(); } catch { }
    }

    async void Next_Click(object sender, RoutedEventArgs e)
    {
        try { if (_media.Session is { } s) await s.TrySkipNextAsync(); } catch { }
    }

    async void Play_Click(object sender, RoutedEventArgs e)
    {
        try { if (_media.Session is { } s) await s.TryTogglePlayPauseAsync(); } catch { }
    }

    async void Seek_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_canSeek || _tlEnd <= TimeSpan.Zero || _media.Session is not { } s) return;
        e.Handled = true;
        double frac = Math.Clamp(e.GetPosition(Track).X / Track.ActualWidth, 0, 1);
        var target = TimeSpan.FromTicks((long)(_tlEnd.Ticks * frac));
        _tlPos = target;
        _tlStamp = DateTimeOffset.Now;
        try { await s.TryChangePlaybackPositionAsync(target.Ticks); } catch { }
    }

    // ───────────── 菜单 ─────────────

    void BuildMenu()
    {
        _menu = new ContextMenu();

        MenuItem Item(string header, Action onClick, Func<bool>? isChecked = null)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => onClick();
            if (isChecked != null) _checks.Add((mi, isChecked));
            return mi;
        }

        _menu.Items.Add(Item("显示 / 隐藏", ToggleVisible));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("始终置顶", () => { _settings.Topmost = !_settings.Topmost; Topmost = _settings.Topmost; _settings.Save(); },
            () => _settings.Topmost));
        _menu.Items.Add(Item("锁定位置", () => { _settings.Locked = !_settings.Locked; _settings.Save(); },
            () => _settings.Locked));
        _menu.Items.Add(Item("鼠标穿透（从托盘取消）", () => { _settings.ClickThrough = !_settings.ClickThrough; ApplyExStyle(); _settings.Save(); },
            () => _settings.ClickThrough));
        _menu.Items.Add(new Separator());

        var style = new MenuItem { Header = "频谱样式" };
        foreach (var (name, v) in new[] { ("柱状 + 倒影", VisualStyle.Bars), ("镜像", VisualStyle.Mirror), ("波形", VisualStyle.Wave) })
            style.Items.Add(Item(name, () => SetStyle(v), () => _settings.Style == v));
        _menu.Items.Add(style);

        var bars = new MenuItem { Header = "频段数量" };
        foreach (int n in new[] { 32, 48, 64, 96 })
            bars.Items.Add(Item(n.ToString(), () => { _settings.BarCount = n; _bands = new float[n]; _settings.Save(); },
                () => _settings.BarCount == n));
        _menu.Items.Add(bars);

        var size = new MenuItem { Header = "大小" };
        foreach (double sc in new[] { 0.75, 0.9, 1.0, 1.15, 1.3, 1.5 })
            size.Items.Add(Item($"{sc * 100:0}%", () => { _settings.Scale = sc; ApplyScale(); _settings.Save(); },
                () => Math.Abs(_settings.Scale - sc) < 0.01));
        _menu.Items.Add(size);

        var op = new MenuItem { Header = "背景不透明度" };
        foreach (double o in new[] { 0.4, 0.6, 0.75, 0.9, 1.0 })
            op.Items.Add(Item($"{o * 100:0}%", () => { _settings.BackgroundOpacity = o; ApplyOpacity(); _settings.Save(); },
                () => Math.Abs(_settings.BackgroundOpacity - o) < 0.01));
        _menu.Items.Add(op);

        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("开机自启", ToggleAutostart, IsAutostart));
        _menu.Items.Add(Item("重置位置", () =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth; Top = wa.Bottom - ActualHeight;
            Show();
        }));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("退出", () => Close()));

        _menu.Opened += (_, _) =>
        {
            foreach (var (item, isChecked) in _checks) item.IsChecked = isChecked();
            // 从托盘打开时需要把菜单设为前台，否则点别处不会关闭
            if (PresentationSource.FromVisual(_menu) is HwndSource src) SetForegroundWindow(src.Handle);
        };
    }

    void ShowMenu(bool fromTray)
    {
        _menu.PlacementTarget = fromTray ? null : this;
        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    void SetStyle(VisualStyle v)
    {
        _settings.Style = v;
        Spectrum.Mode = v;
        Spectrum.Invalidate();
        _settings.Save();
    }

    void CycleStyle() => SetStyle((VisualStyle)(((int)_settings.Style + 1) % 3));

    void ApplyScale()
    {
        RootScale.ScaleX = RootScale.ScaleY = _settings.Scale;
    }

    void ApplyOpacity()
    {
        BackPlate.Opacity = _settings.BackgroundOpacity;
        BgLayer.Opacity = _settings.BackgroundOpacity;
    }

    static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) is string;
    }

    static void ToggleAutostart()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (k.GetValue(RunName) is string) k.DeleteValue(RunName);
        else k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
    }

    // ───────────── Win32 ─────────────

    const int GWL_EXSTYLE = -20;
    const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    void ApplyExStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW;           // 不出现在 Alt+Tab
        ex &= ~WS_EX_APPWINDOW;
        if (_settings.ClickThrough) ex |= WS_EX_TRANSPARENT; else ex &= ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    void Cleanup()
    {
        CompositionTarget.Rendering -= OnFrame;
        _pollTimer.Stop();
        if (Left > -5000) { _settings.Left = Left; _settings.Top = Top; }
        _settings.Save();
        _audio.Dispose();
        _tray?.Dispose();
        Application.Current.Shutdown();
    }
}
