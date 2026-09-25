using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;
using GSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using GManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace SpectrumWidget;

enum MediaChange { Session, Properties, Playback, Timeline }

/// <summary>
/// 通过系统媒体传输控件（SMTC，就是音量浮层里那个媒体卡片）读取任意播放器的正在播放信息。
/// 优先跟随正在播放的会话；多个同时播放时不来回跳。
/// </summary>
sealed class MediaWatcher
{
    GManager? _mgr;
    GSession? _session;
    readonly object _lock = new();

    public GSession? Session => _session;
    public event Action<MediaChange>? Changed;

    public async Task InitAsync()
    {
        _mgr = await GManager.RequestAsync();
        _mgr.CurrentSessionChanged += (_, _) => Pick();
        _mgr.SessionsChanged += (_, _) => Pick();
        Pick();
    }

    static bool IsPlaying(GSession s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    static string? Id(GSession? s)
    {
        try { return s?.SourceAppUserModelId; } catch { return null; }
    }

    public void Pick()
    {
        if (_mgr == null) return;
        GSession? best;
        try
        {
            var all = _mgr.GetSessions().ToList();
            var cur = _mgr.GetCurrentSession();
            var mine = all.FirstOrDefault(s => Id(s) == Id(_session) && Id(s) != null);

            if (mine != null && IsPlaying(mine)) best = mine;
            else if (cur != null && IsPlaying(cur)) best = cur;
            else best = all.FirstOrDefault(IsPlaying) ?? mine ?? cur ?? all.FirstOrDefault();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return;
        }

        MediaChange kind;
        lock (_lock)
        {
            if (ReferenceEquals(best, _session)) return;
            kind = best != null && Id(best) == Id(_session) ? MediaChange.Properties : MediaChange.Session;
            Unhook(_session);
            _session = best;
            Hook(_session);
        }
        Changed?.Invoke(kind);
    }

    void Hook(GSession? s)
    {
        if (s == null) return;
        try
        {
            s.MediaPropertiesChanged += OnProps;
            s.PlaybackInfoChanged += OnPlayback;
            s.TimelinePropertiesChanged += OnTimeline;
        }
        catch { }
    }

    void Unhook(GSession? s)
    {
        if (s == null) return;
        try
        {
            s.MediaPropertiesChanged -= OnProps;
            s.PlaybackInfoChanged -= OnPlayback;
            s.TimelinePropertiesChanged -= OnTimeline;
        }
        catch { }
    }

    void OnProps(GSession s, MediaPropertiesChangedEventArgs e) => Changed?.Invoke(MediaChange.Properties);
    void OnPlayback(GSession s, PlaybackInfoChangedEventArgs e) => Changed?.Invoke(MediaChange.Playback);
    void OnTimeline(GSession s, TimelinePropertiesChangedEventArgs e) => Changed?.Invoke(MediaChange.Timeline);

    public static string PrettyAppName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        string l = id.ToLowerInvariant();
        (string key, string name)[] known =
        {
            ("cloudmusic", "网易云音乐"), ("spotify", "Spotify"), ("qqmusic", "QQ音乐"), ("kugou", "酷狗音乐"),
            ("kwmusic", "酷我音乐"), ("kuwo", "酷我音乐"), ("applemusic", "Apple Music"), ("zunemusic", "媒体播放器"),
            ("microsoft.media.player", "媒体播放器"), ("foobar2000", "foobar2000"), ("musicbee", "MusicBee"),
            ("aimp", "AIMP"), ("potplayer", "PotPlayer"), ("vlc", "VLC"), ("msedge", "Edge"), ("chrome", "Chrome"),
            ("firefox", "Firefox"), ("308046b0af4a39cb", "Firefox"), ("tidal", "TIDAL"), ("youtube", "YouTube Music"),
            ("bilibili", "哔哩哔哩"), ("lx-music", "洛雪音乐"), ("listen1", "Listen1"),
        };
        foreach (var (key, name) in known)
            if (l.Contains(key)) return name;
        string s = id;
        int bang = s.LastIndexOf('!');
        if (bang >= 0 && bang < s.Length - 1) s = s[(bang + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s;
    }
}
