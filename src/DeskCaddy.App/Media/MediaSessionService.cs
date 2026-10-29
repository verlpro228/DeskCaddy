using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskCaddy.Diagnostics;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DeskCaddy.Media;

/// <summary>
/// 某一刻的系统媒体状态。封面可能为空（部分播放器不提供缩略图）。
/// </summary>
public sealed record MediaSnapshot(
    string SourceApp,
    string Title,
    string Artist,
    bool IsPlaying,
    BitmapSource? Cover,
    TimeSpan Position,
    TimeSpan Duration);

/// <summary>
/// 读取 Windows 的系统媒体会话（GSMTC）。
///
/// 这是唯一不需要给每个播放器做适配的办法：只要播放器把媒体信息注册给系统
/// （QQ 音乐、网易云都会），就能统一拿到歌名/歌手/封面/进度，并通过同一套
/// 接口控制播放。实测 QQMusic.exe 返回的字段完整。
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    private const int CoverDecodeWidth = 96;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _ticker;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private BitmapSource? _cover;

    /// <summary>当前封面属于哪一首（歌名|歌手|专辑）。换歌即作废旧封面。</summary>
    private string _trackKey = string.Empty;

    /// <summary>封面连续取不到时最多重试几次、每次间隔多久。</summary>
    private const int MaxCoverAttempts = 8;

    private static readonly TimeSpan CoverRetryInterval = TimeSpan.FromSeconds(1.8);

    private int _coverAttempts;
    private DateTime _lastCoverAttemptUtc = DateTime.MinValue;

    private bool _refreshing;
    private bool _disposed;

    public MediaSessionService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        // 播放器的进度事件并不总会推送，用 1 秒轮询兜底。
        _ticker = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _ticker.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>系统媒体状态变化。null 表示当前没有任何媒体会话。</summary>
    public event EventHandler<MediaSnapshot?>? Changed;

    public MediaSnapshot? Current { get; private set; }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

            _manager.SessionsChanged += OnSessionsChanged;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;

            _ticker.Start();

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            // 未安装媒体功能包 / 老系统上拿不到管理器，灵动岛退化为不可用。
            AppLog.Error("media session manager unavailable", ex);
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            if (IsPlaying(session))
            {
                await session.TryPauseAsync();
            }
            else
            {
                await session.TryPlayAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("media toggle failed", ex);
        }

        await RefreshAsync();
    }

    public async Task NextAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            await session.TrySkipNextAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("media next failed", ex);
        }

        await RefreshAsync();
    }

    public async Task PreviousAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            await session.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("media previous failed", ex);
        }

        await RefreshAsync();
    }

    public void Dispose()
    {
        _disposed = true;

        _ticker.Stop();

        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }

        UnbindSession();

        _manager = null;
        _cover = null;
    }

    // ---------- 会话选择 ----------

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args) => _dispatcher.BeginInvoke(() => _ = RefreshAsync());

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args) => _dispatcher.BeginInvoke(() => _ = RefreshAsync());

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => _dispatcher.BeginInvoke(() => _ = RefreshAsync());

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => _dispatcher.BeginInvoke(() => _ = RefreshAsync());

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args) => _dispatcher.BeginInvoke(() => _ = RefreshAsync());

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 同时开着两个播放器时：正在播放的优先；都在播或都停着时沿用当前会话，
    /// 避免在两个会话之间来回跳。
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        var sessions = _manager?.GetSessions();
        if (sessions is null || sessions.Count == 0)
        {
            return null;
        }

        if (_session is not null && Contains(sessions, _session))
        {
            if (IsPlaying(_session))
            {
                return _session;
            }

            foreach (var candidate in sessions)
            {
                if (IsPlaying(candidate))
                {
                    return candidate;
                }
            }

            return _session;
        }

        foreach (var candidate in sessions)
        {
            if (IsPlaying(candidate))
            {
                return candidate;
            }
        }

        return sessions[0];
    }

    private static bool Contains(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
        GlobalSystemMediaTransportControlsSession session)
    {
        foreach (var candidate in sessions)
        {
            if (ReferenceEquals(candidate, session))
            {
                return true;
            }
        }

        return false;
    }

    private void BindSession(GlobalSystemMediaTransportControlsSession? next)
    {
        if (ReferenceEquals(_session, next))
        {
            return;
        }

        UnbindSession();

        _session = next;

        if (_session is null)
        {
            return;
        }

        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
    }

    private void UnbindSession()
    {
        if (_session is null)
        {
            return;
        }

        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;

        _session = null;
    }

    // ---------- 状态读取 ----------

    private async Task RefreshAsync()
    {
        if (_disposed || _refreshing)
        {
            return;
        }

        _refreshing = true;

        try
        {
            var session = PickSession();
            BindSession(session);

            if (session is null)
            {
                _cover = null;
                _trackKey = string.Empty;
                _coverAttempts = 0;
                _lastCoverAttemptUtc = DateTime.MinValue;

                // 没有会话就停掉 1 秒轮询，不为空转唤醒 UI 线程。
                _ticker.Stop();

                Publish(null);
                return;
            }

            if (!_ticker.IsEnabled)
            {
                _ticker.Start();
            }

            var playing = IsPlaying(session);

            var properties = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();

            var title = properties?.Title ?? string.Empty;
            var artist = properties?.Artist ?? string.Empty;

            var trackKey = $"{title}|{artist}|{properties?.AlbumTitle}";

            if (!string.Equals(trackKey, _trackKey, StringComparison.Ordinal))
            {
                // 换歌：旧封面立刻作废，避免短暂显示上一首的封面。
                _trackKey = trackKey;
                _cover = null;
                _coverAttempts = 0;
                _lastCoverAttemptUtc = DateTime.MinValue;
            }

            // 有的播放器不是一次就能给出封面：SMTC 里 Thumbnail 引用存在，
            // 但第一次打开流是空的，过一两秒再取才有数据（QQ 音乐实测）。
            // 所以取不到时不能就此放弃，按间隔重试若干次。
            if (_cover is null
                && properties?.Thumbnail is not null
                && _coverAttempts < MaxCoverAttempts
                && (DateTime.UtcNow - _lastCoverAttemptUtc) >= CoverRetryInterval)
            {
                _lastCoverAttemptUtc = DateTime.UtcNow;
                _coverAttempts++;

                _cover = await LoadCoverAsync(properties.Thumbnail, trackKey, _coverAttempts);
            }

            var start = timeline.StartTime;
            var position = timeline.Position > start ? timeline.Position - start : TimeSpan.Zero;
            var duration = timeline.EndTime > start ? timeline.EndTime - start : TimeSpan.Zero;

            Publish(new MediaSnapshot(
                session.SourceAppUserModelId,
                title,
                artist,
                playing,
                _cover,
                position,
                duration));
        }
        catch (Exception ex)
        {
            AppLog.Error("media refresh failed", ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// 把封面流读成字节再解码。直接拿 IRandomAccessStream 交给 WPF 会牵扯
    /// WinRT 与 .NET 流的互操作层，读成 byte[] 最稳，也方便以后做缓存。
    /// 读失败会写日志，便于判断是播放器没给图还是我们解码失败。
    /// </summary>
    private static async Task<BitmapSource?> LoadCoverAsync(
        IRandomAccessStreamReference reference,
        string trackKey,
        int attempt)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();

            var size = (uint)stream.Size;
            if (size == 0)
            {
                AppLog.Info($"cover: empty stream (attempt {attempt}) {trackKey}");
                return null;
            }

            // 一次 LoadAsync 不保证把整个流读满，循环补齐。
            var bytes = new byte[size];
            uint total = 0;

            using (var reader = new DataReader(stream))
            {
                while (total < size)
                {
                    var loaded = await reader.LoadAsync(size - total);
                    if (loaded == 0)
                    {
                        break;
                    }

                    var chunk = new byte[loaded];
                    reader.ReadBytes(chunk);
                    System.Buffer.BlockCopy(chunk, 0, bytes, (int)total, (int)loaded);
                    total += loaded;
                }
            }

            if (total < size)
            {
                AppLog.Info($"cover: short read {total}/{size} (attempt {attempt}) {trackKey}");
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();

            // 部分封面内嵌 ICC 色彩配置，WPF 默认会回读颜色上下文，
            // 遇到就抛 WIC 0x88982F72（无法读取流），表现为「个别歌曲没有封面」。
            // 这里只用来当 36px 的缩略图，忽略色彩配置即可。
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = CoverDecodeWidth;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();

            AppLog.Info($"cover: ok {total} bytes (attempt {attempt}) {trackKey}");

            return bitmap;
        }
        catch (Exception ex)
        {
            AppLog.Error($"cover: load failed (attempt {attempt}) {trackKey}", ex);
            return null;
        }
    }

    private void Publish(MediaSnapshot? snapshot)
    {
        if (_dispatcher.CheckAccess())
        {
            Current = snapshot;
            Changed?.Invoke(this, snapshot);
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            Current = snapshot;
            Changed?.Invoke(this, snapshot);
        });
    }
}
