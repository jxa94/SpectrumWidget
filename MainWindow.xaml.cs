using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    readonly DispatcherTimer _visTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    // 显示 / 隐藏状态
    // 完全隐藏（只用于全屏 / 游戏，以及托盘单击）
    bool _shown = true;          // 当前目标状态（动画结束后的样子）
    bool _manualHidden;          // 托盘单击隐藏
    bool _autoSuppressed;        // 全屏时用户又手动叫出来：直到全屏状态变化前不再自动隐藏
    bool _lastAutoHide;
    // 收起成小圆片（隐藏按钮 / Ctrl+Alt+M / 空闲自动收起）
    bool _collapsed;             // 目标状态
    bool _visualCollapsed;       // 当前界面实际状态
    bool _collapsedManual;
    bool _idleSuppressed;
    bool _lastIdleHide;
    int _swapVersion;
    readonly float[] _miniBands = new float[6];
    bool _wasIdle;
    DateTime _lastActive = DateTime.Now;
    const int HotkeyId = 0x5357;

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
            if (Left < -5000 || _swapping) return;
            var p = _visualCollapsed ? ExpandedPositionFromMini() : new Point(Left, Top);
            _settings.Left = p.X; _settings.Top = p.Y; _settings.Save();
        };

        SourceInitialized += (_, _) =>
        {
            ApplyExStyle();
            RegisterToggleHotkey();
        };
        Card.MouseEnter += (_, _) => FadeHideButton(1);
        Card.MouseLeave += (_, _) => FadeHideButton(0);
        _visTimer.Tick += (_, _) => EvaluateVisibility();
        MiniSpectrum.Mode = VisualStyle.Mirror;
        MiniSpectrum.Compact = true;
        _collapsedManual = settings.Collapsed;
        Loaded += OnLoaded;
        Closed += (_, _) => Cleanup();

        _tray = new TrayIcon(ToggleVisible, () => ShowMenu(fromTray: true));
        SetIdle();
    }

    // ───────────── 启动 / 位置 ─────────────

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceWindow();
        _expandedSize = new Size(ActualWidth, ActualHeight);
        if (_collapsedManual) SetCollapsed(true);

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
        _visTimer.Start();
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
        double x0 = Left, y0 = Top;
        if (!_settings.Locked)
            try { DragMove(); } catch { }
        if (_visualCollapsed && Math.Abs(Left - x0) < 3 && Math.Abs(Top - y0) < 3)
            ExpandByUser();
    }

    // ───────────── 显示 / 隐藏 ─────────────

    /// <summary>托盘单击：完全隐藏 / 显示。</summary>
    void ToggleVisible()
    {
        if (_shown) _manualHidden = true;
        else
        {
            _manualHidden = false;
            if (_lastAutoHide) _autoSuppressed = true;
        }
        EvaluateVisibility();
    }

    /// <summary>Ctrl+Alt+M：收起 / 展开；如果当前完全隐藏着，就直接叫出来并展开。</summary>
    void ToggleCollapse()
    {
        if (!_shown)
        {
            _manualHidden = false;
            if (_lastAutoHide) _autoSuppressed = true;
            ExpandByUser();
            return;
        }
        if (_collapsed) ExpandByUser(); else CollapseByUser();
    }

    void CollapseByUser()
    {
        _collapsedManual = true;
        _settings.Collapsed = true;
        _settings.Save();
        EvaluateVisibility();
    }

    void ExpandByUser()
    {
        _collapsedManual = false;
        if (_lastIdleHide) _idleSuppressed = true;
        _settings.Collapsed = false;
        _settings.Save();
        EvaluateVisibility();
    }

    void Hide_Click(object sender, RoutedEventArgs e)
    {
        CollapseByUser();
        if (!_settings.HideTipShown)
        {
            _settings.HideTipShown = true;
            _settings.Save();
            _tray?.ShowTip("小部件已收起", "单击小圆片或按 Ctrl+Alt+M 展开");
        }
    }

    void EvaluateVisibility()
    {
        var now = DateTime.Now;
        if (_playing || _audio.IsSounding) _lastActive = now;
        bool idle = (now - _lastActive).TotalSeconds >= Math.Max(1, _settings.AutoHideDelay);
        // 开了“没在播放时收起”：空闲之后又开始播放，连手动收起也一并展开
        if (_settings.AutoHideIdle && _wasIdle && !idle && _collapsedManual)
        {
            _collapsedManual = false;
            _settings.Collapsed = false;
            _settings.Save();
        }
        _wasIdle = idle;

        bool idleHide = _settings.AutoHideIdle && idle;
        if (idleHide != _lastIdleHide) { _idleSuppressed = false; _lastIdleHide = idleHide; }
        SetCollapsed(_collapsedManual || (idleHide && !_idleSuppressed));

        bool fsHide = _settings.AutoHideFullscreen && IsFullscreenAppOnMyMonitor();
        if (fsHide != _lastAutoHide) { _autoSuppressed = false; _lastAutoHide = fsHide; }
        SetShown(!_manualHidden && !(fsHide && !_autoSuppressed));
    }

    void SetShown(bool show)
    {
        if (show == _shown) return;
        _shown = show;
        var dur = new Duration(TimeSpan.FromMilliseconds(show ? 260 : 200));
        if (show)
        {
            Opacity = 0;
            Show();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            var anim = new DoubleAnimation(0, dur);
            anim.Completed += (_, _) => { if (!_shown) Hide(); };
            BeginAnimation(OpacityProperty, anim);
        }
    }

    // ───────────── 收起 / 展开 ─────────────

    Size _expandedSize;
    bool _swapping;

    void SetCollapsed(bool collapsed)
    {
        if (collapsed == _collapsed) return;
        _collapsed = collapsed;
        int ver = ++_swapVersion;

        if (!IsVisible)
        {
            ApplyCollapsedLayout();
            return;
        }
        var fadeOut = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(130)));
        fadeOut.Completed += (_, _) =>
        {
            if (ver != _swapVersion) return;
            ApplyCollapsedLayout();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = ease });
            var pop = new DoubleAnimation(0.9, 1, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
            PopScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            PopScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        };
        Root.BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>切换布局，并让靠近屏幕角落的那个角保持不动。</summary>
    void ApplyCollapsedLayout()
    {
        if (_collapsed == _visualCollapsed) return;
        var (right, bottom) = NearestCorner();
        double ax = right ? Left + ActualWidth : Left;
        double ay = bottom ? Top + ActualHeight : Top;
        if (_collapsed) _expandedSize = new Size(ActualWidth, ActualHeight);

        _swapping = true;
        _visualCollapsed = _collapsed;
        Card.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        Mini.Visibility = _collapsed ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout();
        Left = right ? ax - ActualWidth : ax;
        Top = bottom ? ay - ActualHeight : ay;
        if (!_collapsed) ClampToWorkArea();
        PopScale.CenterX = right ? Root.ActualWidth : 0;
        PopScale.CenterY = bottom ? Root.ActualHeight : 0;
        _swapping = false;
    }

    Point ExpandedPositionFromMini()
    {
        var (right, bottom) = NearestCorner();
        double x = right ? Left + ActualWidth - _expandedSize.Width : Left;
        double y = bottom ? Top + ActualHeight - _expandedSize.Height : Top;
        return new Point(x, y);
    }

    Rect WorkAreaDip()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY, wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
    }

    (bool right, bool bottom) NearestCorner()
    {
        var wa = WorkAreaDip();
        double cx = Left + ActualWidth / 2, cy = Top + ActualHeight / 2;
        return (cx > wa.Left + wa.Width / 2, cy > wa.Top + wa.Height / 2);
    }

    void ClampToWorkArea()
    {
        var wa = WorkAreaDip();
        if (Left + ActualWidth > wa.Right) Left = wa.Right - ActualWidth;
        if (Top + ActualHeight > wa.Bottom) Top = wa.Bottom - ActualHeight;
        if (Left < wa.Left) Left = wa.Left;
        if (Top < wa.Top) Top = wa.Top;
    }

    void FadeHideButton(double to) =>
        HideBtn.BeginAnimation(OpacityProperty, new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(150))));

    bool IsFullscreenAppOnMyMonitor()
    {
        var me = new WindowInteropHelper(this).Handle;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == me) return false;

        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        string c = cls.ToString();
        if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        var mon = MonitorFromWindow(fg, 2);
        if (me != IntPtr.Zero && MonitorFromWindow(me, 2) != mon) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi) || !GetWindowRect(fg, out RECT r)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
               && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    void RegisterToggleHotkey()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0312 && w.ToInt32() == HotkeyId) { ToggleCollapse(); handled = true; }
            return IntPtr.Zero;
        });
        // Ctrl + Alt + M，MOD_NOREPEAT 防止按住时连发
        if (!RegisterHotKey(hwnd, HotkeyId, 0x0001 | 0x0002 | 0x4000, 0x4D))
            App.Log(new InvalidOperationException("Ctrl+Alt+M 已被其他程序占用"));
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
        if (_visualCollapsed)
        {
            int n = _bands.Length, m = _miniBands.Length;
            for (int i = 0; i < m; i++)
            {
                float mx = 0;
                for (int k = i * n / m; k < (i + 1) * n / m; k++) mx = Math.Max(mx, _bands[k]);
                _miniBands[i] = mx;
            }
            MiniSpectrum.Update(_miniBands, dt);
            if (_playing) MiniRotate.Angle = (MiniRotate.Angle + dt * 30) % 360;
        }
        else
        {
            Spectrum.Update(_bands, dt);
            UpdateProgress();
        }
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
        ArtistText.ToolTip = null;
        ArtistIcon.Visibility = Visibility.Collapsed;
        _tlEnd = TimeSpan.Zero; _tlPos = TimeSpan.Zero; _playing = false;
        PosText.Text = DurText.Text = "0:00";
        _lastPosSec = _lastDurSec = -1;
        PlayBtn.Content = "";
        PrevBtn.IsEnabled = PlayBtn.IsEnabled = NextBtn.IsEnabled = false;
        SetCover(null, null);
    }

    static readonly string[] TitleSeparators = { " - ", " – ", " — ", " | " };

    /// <summary>
    /// 艺术家依次取：艺术家 → 专辑艺术家 → 副标题。都没有时，
    /// 如果标题是“歌手 - 歌名”这种格式（视频网站常见），就拆开。
    /// </summary>
    internal static (string title, string artist) ResolveTitleArtist(string? title, string? artist, string? albumArtist, string? subtitle)
    {
        title = title?.Trim() ?? "";
        string a = new[] { artist, albumArtist, subtitle }
            .Select(x => x?.Trim() ?? "").FirstOrDefault(x => x.Length > 0) ?? "";

        if (a.Length == 0)
        {
            foreach (var sep in TitleSeparators)
            {
                int i = title.IndexOf(sep, StringComparison.Ordinal);
                if (i <= 0 || i + sep.Length >= title.Length) continue;
                string left = title[..i].Trim(), right = title[(i + sep.Length)..].Trim();
                if (left.Length == 0 || right.Length == 0 || left.Length > 60) continue;
                a = left;
                title = right;
                break;
            }
        }
        return (title.Length > 0 ? title : "未知曲目", a.Length > 0 ? a : "未知艺术家");
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

            var (title, artist) = ResolveTitleArtist(p.Title, p.Artist, p.AlbumArtist, p.Subtitle);
            string app = MediaWatcher.PrettyAppName(s.SourceAppUserModelId);
            string sub = string.IsNullOrWhiteSpace(p.AlbumTitle) ? app : $"{p.AlbumTitle}  ·  {app}";

            TitleText.Text = title;
            TitleText.ToolTip = title;
            ArtistText.Text = artist;
            ArtistText.ToolTip = artist;
            ArtistIcon.Visibility = Visibility.Visible;
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
        catch (Exception ex) when (IsSessionGone(ex))
        {
            _media.Pick();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>播放器刚关掉 / 标签页刚关闭时，读它的会话会抛这些异常，属于正常情况。</summary>
    static bool IsSessionGone(Exception ex) =>
        ex is System.IO.FileNotFoundException or COMException or NullReferenceException or ObjectDisposedException;

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
            MiniCover.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fade));
            MiniHole.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fade));
            BgCover.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fade));
            ApplyAccent(ColorUtil.DefaultA, ColorUtil.DefaultB);
            return;
        }

        CoverBrush.ImageSource = bmp;
        MiniCoverBrush.ImageSource = bmp;
        MiniCover.BeginAnimation(OpacityProperty, new DoubleAnimation(1, fade));
        MiniHole.BeginAnimation(OpacityProperty, new DoubleAnimation(1, fade));
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
        MiniSpectrum.SetAccent(a, b);
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
            if (info?.Controls == null) { _media.Pick(); return; }
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
        catch (Exception ex) when (IsSessionGone(ex)) { _media.Pick(); }
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

        _menu.Items.Add(Item("收起 / 展开　Ctrl+Alt+M", ToggleCollapse));
        _menu.Items.Add(Item("完全隐藏 / 显示（单击托盘）", ToggleVisible));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("始终置顶", () => { _settings.Topmost = !_settings.Topmost; Topmost = _settings.Topmost; _settings.Save(); },
            () => _settings.Topmost));
        _menu.Items.Add(Item("锁定位置", () => { _settings.Locked = !_settings.Locked; _settings.Save(); },
            () => _settings.Locked));
        _menu.Items.Add(Item("鼠标穿透（从托盘取消）", () => { _settings.ClickThrough = !_settings.ClickThrough; ApplyExStyle(); _settings.Save(); },
            () => _settings.ClickThrough));

        var auto = new MenuItem { Header = "自动收起 / 隐藏" };
        auto.Items.Add(Item("没在播放时收起（播放时展开）", () => { _settings.AutoHideIdle = !_settings.AutoHideIdle; _settings.Save(); EvaluateVisibility(); },
            () => _settings.AutoHideIdle));
        auto.Items.Add(Item("有全屏程序 / 游戏时完全隐藏", () => { _settings.AutoHideFullscreen = !_settings.AutoHideFullscreen; _settings.Save(); EvaluateVisibility(); },
            () => _settings.AutoHideFullscreen));
        auto.Items.Add(new Separator());
        foreach (var (label, sec) in new[] { ("停止 3 秒后", 3), ("停止 10 秒后", 10), ("停止 30 秒后", 30), ("停止 1 分钟后", 60) })
            auto.Items.Add(Item(label, () => { _settings.AutoHideDelay = sec; _settings.Save(); },
                () => _settings.AutoHideDelay == sec));
        _menu.Items.Add(auto);
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
            _manualHidden = false; _autoSuppressed = _lastAutoHide;
            EvaluateVisibility();
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
        MiniPlate.Opacity = Math.Max(0.85, _settings.BackgroundOpacity); // 小圆片面积小，太透会看不清
        BgLayer.Opacity = _settings.BackgroundOpacity;
    }

    static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) is string;
    }

    static string AutostartCommand => $"\"{Environment.ProcessPath}\" --autostart";

    static void ToggleAutostart()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (k.GetValue(RunName) is string) k.DeleteValue(RunName);
        else k.SetValue(RunName, AutostartCommand);
        App.LogLine($"开机自启 {(k.GetValue(RunName) is string ? "开启" : "关闭")}");
    }

    /// <summary>自启项还在但程序挪过位置 / 是旧格式时，改成当前路径。</summary>
    internal static void RefreshAutostartPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue(RunName) is string v && v != AutostartCommand)
            {
                k.SetValue(RunName, AutostartCommand);
                App.LogLine($"更新开机自启路径：{v} → {AutostartCommand}");
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // ───────────── Win32 ─────────────

    const int GWL_EXSTYLE = -20;
    const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

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
        _visTimer.Stop();
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
        if (Left > -5000)
        {
            // 收起状态下存的是展开后卡片该在的位置，不是小圆片的位置
            var p = _visualCollapsed ? ExpandedPositionFromMini() : new Point(Left, Top);
            _settings.Left = p.X; _settings.Top = p.Y;
        }
        _settings.Save();
        _audio.Dispose();
        _tray?.Dispose();
        Application.Current.Shutdown();
    }
}
