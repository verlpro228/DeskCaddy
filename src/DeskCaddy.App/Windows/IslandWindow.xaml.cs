using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskCaddy.Core.Settings;
using DeskCaddy.Diagnostics;
using DeskCaddy.Media;
using DeskCaddy.Rendering;

namespace DeskCaddy.Windows;

/// <summary>
/// 屏幕顶部的音乐灵动岛。
///
/// 与 Dock 同属桌面层：不抢焦点、不覆盖应用窗口。只在系统里真有音乐在播时
/// 才显示，暂停后自动退场 —— 常驻的置顶条会挡住用户操作，也毫无必要。
///
/// 宽度做在内部胶囊上而不是窗口上：分层窗口改尺寸会整层重绘掉帧，
/// 动内容则只重排这一小块。
/// </summary>
public partial class IslandWindow : Window
{
    private const double CollapsedWidth = 220;
    private const double ProgressInset = 40;

    private static readonly Brush FallbackCover = CreateFrozenBrush(
        new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));

    /// <summary>没有可用壁纸时的底，与 XAML 里的默认渐变保持一致。</summary>
    private static readonly Brush FallbackPill = CreateFrozenBrush(new LinearGradientBrush(
        Color.FromArgb(0xCC, 0x1A, 0x1A, 0x20),
        Color.FromArgb(0xCC, 0x0E, 0x0E, 0x12),
        90));

    private readonly MediaSessionService _media;

    private double _expandedWidth = AppSettings.DefaultIslandWidth;

    /// <summary>截屏时的窗口宽度（DIP）。胶囊收窄时按它把底图裁成中间那段。</summary>
    private double _capturedWidth;

    private double _progress;
    private bool _expanded;
    private bool _closing;
    private bool _hiding;
    private bool _showing;
    private bool _capturing;

    /// <summary>宽度动画期间不要重算进度条：那会让一帧内多跑一次布局。</summary>
    private bool _animatingWidth;

    private DispatcherTimer? _hideTimer;
    private DispatcherTimer? _glassTimer;
    private DispatcherTimer? _desktopWatcher;

    /// <summary>当前底图是不是在桌面上截的（应用遮挡时截到的会是应用画面）。</summary>
    private bool _glassOnDesktop;

    private string _lastWallpaperMark = string.Empty;
    private DateTime _lastCaptureUtc = DateTime.MinValue;
    private DateTime _lastTrimUtc = DateTime.MinValue;
    private int _glassFailures;

    /// <summary>下一次截屏是否绕开缓存（壁纸可能换了）。</summary>
    private bool _glassForce;

    public IslandWindow()
    {
        InitializeComponent();

        _media = new MediaSessionService(Dispatcher);
        _media.Changed += OnMediaChanged;

        Pill.SizeChanged += (_, _) =>
        {
            UpdateGlassViewbox();

            if (!_animatingWidth)
            {
                UpdateProgressWidth();
            }
        };

        Pill.MouseEnter += (_, _) => Expand();
        Pill.MouseLeave += (_, _) => Collapse();
    }

    /// <summary>开始监听系统媒体会话。失败只记日志，不影响 Dock。</summary>
    public Task StartAsync() => _media.StartAsync();

    /// <summary>应用设置里的开关与宽度。宽度变化会立刻反映到胶囊上。</summary>
    public void ApplySettings(AppSettings settings)
    {
        _expandedWidth = Math.Clamp(
            settings.IslandWidth,
            AppSettings.MinIslandWidth,
            AppSettings.MaxIslandWidth);

        var windowWidth = Math.Max(CollapsedWidth, _expandedWidth);
        var widthChanged = Math.Abs(Width - windowWidth) > 0.5;

        Width = windowWidth;

        if (_expanded)
        {
            AnimatePillWidth(_expandedWidth);
        }
        else
        {
            SetPillWidth(CollapsedWidth);
        }

        if (widthChanged)
        {
            PositionOnScreen();

            if (IsVisible)
            {
                ScheduleGlassRefresh();
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;

        CancelHide();
        StopGlassTimer();
        StopDesktopWatcher();

        _media.Changed -= OnMediaChanged;
        _media.Dispose();

        base.OnClosed(e);
    }

    // ---------- 媒体状态 ----------

    private void OnMediaChanged(object? sender, MediaSnapshot? snapshot)
    {
        if (snapshot is null || !snapshot.IsPlaying)
        {
            ScheduleHide();
            return;
        }

        CancelHide();
        CancelHideAnimation();
        ApplySnapshot(snapshot);

        if (!IsVisible)
        {
            ShowIsland();
        }
    }

    private void ApplySnapshot(MediaSnapshot snapshot)
    {
        TitleText.Text = string.IsNullOrWhiteSpace(snapshot.Title) ? "正在播放" : snapshot.Title;

        ArtistText.Text = string.IsNullOrWhiteSpace(snapshot.Artist)
            ? FriendlyAppName(snapshot.SourceApp)
            : snapshot.Artist;

        if (snapshot.Cover is not null)
        {
            CoverHost.Background = new ImageBrush(snapshot.Cover)
            {
                Stretch = Stretch.UniformToFill
            };

            CoverGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            CoverHost.Background = FallbackCover;
            CoverGlyph.Visibility = Visibility.Visible;
        }

        PlayButton.Content = snapshot.IsPlaying ? "\uE769" : "\uE768";

        _progress = snapshot.Duration > TimeSpan.Zero
            ? Math.Clamp(snapshot.Position.TotalMilliseconds / snapshot.Duration.TotalMilliseconds, 0, 1)
            : 0;

        UpdateProgressWidth();
    }

    private static string FriendlyAppName(string sourceApp)
    {
        if (string.IsNullOrWhiteSpace(sourceApp))
        {
            return string.Empty;
        }

        var name = Path.GetFileNameWithoutExtension(sourceApp);

        return name.Length > 0 ? name : sourceApp;
    }

    // ---------- 显示与退场 ----------

    private async void ShowIsland()
    {
        if (_closing || _showing || IsVisible)
        {
            return;
        }

        _showing = true;

        try
        {
            PositionOnScreen();

            // 先把整窗透明：截壁纸时不能把岛自己拍进去。
            Opacity = 0;

            // 胶囊内容也先透明，避免第一帧闪出完整形状。
            Pill.Opacity = 0;

            var slide = new TranslateTransform(0, -6);
            Pill.RenderTransform = slide;

            Show();

            DesktopLayer.PlaceAboveDesktop(this);

            await CaptureBackdropAsync();

            StartDesktopWatcher();

            // 入场：胶囊从上方滑下并淡入。只动内容，不动窗口。
            Pill.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

            slide.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
        finally
        {
            _showing = false;

            if (_media.Current is not { IsPlaying: true })
            {
                ScheduleHide();
            }
        }
    }

    private void ScheduleHide()
    {
        if (!IsVisible || _hideTimer is not null)
        {
            return;
        }

        // 暂停后延迟退场：用户短暂暂停或者切歌时岛不该闪一下。
        _hideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };

        _hideTimer.Tick += (_, _) =>
        {
            CancelHide();
            HideIsland();
        };

        _hideTimer.Start();
    }

    private void CancelHide()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
    }

    /// <summary>
    /// 退场动画还没播完又开始播放时，必须把淡出取消掉，
    /// 否则动画照旧走到结尾把窗口 Hide 掉，音乐在放但岛没了。
    /// </summary>
    private void CancelHideAnimation()
    {
        if (!_hiding)
        {
            return;
        }

        _hiding = false;

        Pill.BeginAnimation(OpacityProperty, null);
        Pill.Opacity = 1;
    }

    private void HideIsland()
    {
        if (!IsVisible)
        {
            return;
        }

        _hiding = true;

        var fade = new DoubleAnimation(Pill.Opacity, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        fade.Completed += (_, _) =>
        {
            _hiding = false;

            Hide();

            StopGlassTimer();
            StopDesktopWatcher();
            ResetGlass();

            _expanded = false;
            SetPillWidth(CollapsedWidth);
            Pill.Opacity = 1;
            Pill.RenderTransform = Transform.Identity;

            FadeControls(0);

            _progress = 0;
            UpdateProgressWidth();

            // 岛退场后 WPF 仍会留着渲染缓存。按分钟级节流修剪一次，
            // 避免暂停/恢复几次之后任务管理器里的占用一直往上爬。
            if ((DateTime.UtcNow - _lastTrimUtc).TotalSeconds >= 60)
            {
                _lastTrimUtc = DateTime.UtcNow;
                MemoryTrim.Trim();
            }
        };

        Pill.BeginAnimation(OpacityProperty, fade);
    }

    // ---------- 悬停展开 ----------

    private void Expand()
    {
        if (_expanded)
        {
            return;
        }

        _expanded = true;

        AnimatePillWidth(Math.Max(CollapsedWidth, _expandedWidth));
        FadeControls(1);
    }

    private void Collapse()
    {
        if (!_expanded)
        {
            return;
        }

        _expanded = false;

        AnimatePillWidth(CollapsedWidth);
        FadeControls(0);
    }

    private void AnimatePillWidth(double target)
    {
        var from = Pill.ActualWidth > 1 ? Pill.ActualWidth : CollapsedWidth;

        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };

        _animatingWidth = true;

        animation.Completed += (_, _) =>
        {
            _animatingWidth = false;
            UpdateProgressWidth();
        };

        Pill.BeginAnimation(FrameworkElement.WidthProperty, animation);
    }

    /// <summary>立即设宽。必须先清掉正在进行的宽度动画，否则新值会被旧动画压住。</summary>
    private void SetPillWidth(double width)
    {
        Pill.BeginAnimation(FrameworkElement.WidthProperty, null);
        _animatingWidth = false;
        Pill.Width = width;
    }

    private void FadeControls(double target)
    {
        Controls.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(Controls.Opacity, target, TimeSpan.FromMilliseconds(150)));

        Controls.IsHitTestVisible = target > 0.5;
    }

    // ---------- 毛玻璃（与 Dock 同一套：截壁纸 + 模糊 + 轻罩层） ----------

    /// <summary>拖动宽度滑块会连续触发重截。等数值稳定 260ms 只截一次，避免连着闪。</summary>
    private void ScheduleGlassRefresh(bool force = false)
    {
        _glassForce |= force;

        StopGlassTimer();

        _glassTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(260)
        };

        _glassTimer.Tick += (_, _) =>
        {
            StopGlassTimer();
            _ = CaptureBackdropAsync();
        };

        _glassTimer.Start();
    }

    private void StopGlassTimer()
    {
        _glassTimer?.Stop();
        _glassTimer = null;
    }

    /// <summary>
    /// 岛可见期间的低频检查：桌面上来、壁纸换了、动态壁纸到点，都补截一次。
    /// 900ms 一次，只有岛在播音乐时才运行。
    /// </summary>
    private void StartDesktopWatcher()
    {
        if (_desktopWatcher is not null)
        {
            return;
        }

        _desktopWatcher = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };

        _desktopWatcher.Tick += OnDesktopTick;
        _desktopWatcher.Start();
    }

    private void StopDesktopWatcher()
    {
        if (_desktopWatcher is null)
        {
            return;
        }

        _desktopWatcher.Stop();
        _desktopWatcher.Tick -= OnDesktopTick;
        _desktopWatcher = null;
    }

    private void OnDesktopTick(object? sender, EventArgs e)
    {
        if (_closing || _capturing || !IsVisible)
        {
            return;
        }

        if (!IsDesktopAtIsland())
        {
            return;
        }

        var secondsSinceCapture = (DateTime.UtcNow - _lastCaptureUtc).TotalSeconds;

        var wallpaperMark = ScreenBackdrop.CurrentWallpaperMark();
        var wallpaperChanged = wallpaperMark.Length > 0
            && !string.Equals(wallpaperMark, _lastWallpaperMark, StringComparison.Ordinal);

        var retryDelay = _glassFailures switch
        {
            0 => 2.0,
            1 => 3.0,
            2 => 5.0,
            _ => 15.0
        };

        var stale = !_glassOnDesktop && secondsSinceCapture >= retryDelay;
        var dynamicDue = ScreenBackdrop.IsDynamicWallpaperSource() && secondsSinceCapture >= 45;

        if (wallpaperChanged || stale || dynamicDue)
        {
            ScheduleGlassRefresh(force: true);
        }
    }

    /// <summary>
    /// 岛所在位置此刻是不是"桌面/壁纸"。
    ///
    /// 不能用 WindowFromPoint 直接比进程号：桌面图标列表（SysListView32）
    /// 盖在岛上面，命中的是 explorer 的窗口，永远会被判成"被应用遮挡"。
    /// 正确做法是看命中窗口的根祖先是不是 Progman / WorkerW —— 那代表桌面。
    /// </summary>
    private bool IsDesktopAtIsland()
    {
        if (!IsVisible || ActualWidth < 1 || ActualHeight < 1)
        {
            return false;
        }

        var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));

        var point = new Point32
        {
            X = (int)Math.Round(center.X),
            Y = (int)Math.Round(center.Y)
        };

        var hit = WindowFromPoint(point);
        if (hit == IntPtr.Zero)
        {
            return true;
        }

        // 命中的是岛自己（或本进程的其它窗口）：截屏前岛会先透明，不算遮挡。
        GetWindowThreadProcessId(hit, out var processId);
        if (processId == Environment.ProcessId)
        {
            return true;
        }

        var root = GetAncestor(hit, GaRoot);
        if (root == IntPtr.Zero)
        {
            return false;
        }

        var buffer = new char[64];
        var length = GetClassNameW(root, buffer, buffer.Length);
        if (length <= 0)
        {
            return false;
        }

        var className = new string(buffer, 0, length);

        return className is "Progman" or "WorkerW";
    }

    private async Task CaptureBackdropAsync()
    {
        if (!IsVisible || _capturing)
        {
            return;
        }

        if (!IsDesktopAtIsland())
        {
            // 被应用盖着时截到的就是那个应用，干脆不截，等桌面露出来再补。
            _glassOnDesktop = false;
            _lastCaptureUtc = DateTime.UtcNow;
            _glassFailures++;
            AppLog.Info("island backdrop: skipped (covered by an app)");
            return;
        }

        _capturing = true;

        try
        {
            Opacity = 0;

            // Opacity=0 只是改了属性，分层窗口的 alpha 要等一次渲染提交才生效。
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(25);
            DwmFlush();

            // 强制刷新时绕开缓存：缓存键只看窗口矩形与壁纸指纹，
            // Wallpaper Engine 换壁纸时两者都不变，走缓存就永远拿到旧图。
            var force = _glassForce;
            _glassForce = false;

            var backdrop = force
                ? ScreenBackdrop.Capture(this, ScreenBackdrop.DockBlurRadius)
                : ScreenBackdrop.CaptureCached(this, ScreenBackdrop.DockBlurRadius);

            if (backdrop is null)
            {
                ResetGlass();
                return;
            }

            Pill.Background = new ImageBrush(backdrop)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox
            };

            _capturedWidth = ActualWidth;

            UpdateGlassViewbox();

            AppLog.Info(
                $"island backdrop: {backdrop.PixelWidth}x{backdrop.PixelHeight}px " +
                $"at ({Left:F0},{Top:F0})");
        }
        catch (Exception ex)
        {
            AppLog.Error("island backdrop failed", ex);
            ResetGlass();
        }
        finally
        {
            Opacity = 1;
            _capturing = false;

            _lastCaptureUtc = DateTime.UtcNow;
            _lastWallpaperMark = ScreenBackdrop.CurrentWallpaperMark();
            _glassOnDesktop = _capturedWidth > 0;
            _glassFailures = _glassOnDesktop ? 0 : _glassFailures + 1;
        }
    }

    /// <summary>底图按展开宽度截，胶囊收窄时只取中间那段，避免壁纸被横向压扁。</summary>
    private void UpdateGlassViewbox()
    {
        if (Pill.Background is not ImageBrush brush || _capturedWidth < 1)
        {
            return;
        }

        var current = Pill.ActualWidth;
        if (current < 1)
        {
            return;
        }

        var captured = Math.Max(_capturedWidth, current);

        var x = Math.Max(0, (captured - current) / 2) / captured;
        var width = Math.Min(1, current / captured);

        brush.Viewbox = new Rect(x, 0, width, 1);
    }

    private void ResetGlass()
    {
        Pill.Background = FallbackPill;
        _capturedWidth = 0;
    }

    private void UpdateProgressWidth()
    {
        var available = Pill.ActualWidth - ProgressInset;

        ProgressFill.Width = available > 0
            ? Math.Max(0, available * _progress)
            : 0;
    }

    private static Brush CreateFrozenBrush(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    // ---------- 位置与交互 ----------

    private void PositionOnScreen()
    {
        var workArea = SystemParameters.WorkArea;

        Left = workArea.Left + ((workArea.Width - Width) / 2);
        Top = workArea.Top + 6;
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = _media.PreviousAsync();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = _media.NextAsync();

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _ = _media.TogglePlayPauseAsync();

    // ---------- Win32 ----------

    private const uint GaRoot = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point32 point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, [Out] char[] className, int maxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }
}
