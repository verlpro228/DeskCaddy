using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DeskCaddy.Core.Library;
using DeskCaddy.Core.Settings;
using DeskCaddy.Core.Shell;
using DeskCaddy.Diagnostics;
using DeskCaddy.Models;
using DeskCaddy.Rendering;

namespace DeskCaddy.Windows;

public partial class DockWindow : Window
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_ACTIVATE = 1;
    private const int MA_NOACTIVATE = 3;
    private const int VK_RBUTTON = 0x02;

    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_CONTEXTMENU = 0x007B;

    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;

    /// <summary>
    /// 向 Shell 请求的图标边长。不要用 256：没有 256px 资源的程序会被 Shell
    /// 塞进一张白色画布中央补白，看起来就是「白色外框 + 图标变小」。
    /// </summary>
    private const int IconRequestSize = 96;

    private HwndSource? _source;
    private LauncherWindow? _launcher;
    private SettingsWindow? _settingsWindow;
    private AppSettings _appSettings = SettingsStore.Default.Load();
    private TrayIcon? _tray;
    private DispatcherTimer? _desktopWatcher;
    private bool _desktopWasForeground;
    private bool _backdropReady;
    private bool _backdropIsDesktop;
    private string _lastWallpaperMark = string.Empty;
    private DateTime _lastBackdropCaptureUtc = DateTime.MinValue;
    private IntPtr _targetMonitor;

    public DockWindow()
    {
        InitializeComponent();

        // 首次截屏时必须确保自己还没被绘制。
        Opacity = 0;

        // 以启动时读到的值为基准，启动过程本身不算「数量变化」，
        // 避免 OnLoaded 里再触发一次多余的重载。
        _lastDockMaxItems = _appSettings.DockMaxItems;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;

        Strip.ItemActivated += OnItemActivated;
        Strip.ContextRequested += OnContextRequested;
        Strip.ItemDragStarted += OnItemDragStarted;

        // 菜单栏或其它窗口改动了收纳库时同步刷新，避免两个窗口状态不一致。
        LibraryEvents.Changed += OnLibraryChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        _tray = new TrayIcon(hwnd, "DeskCaddy");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 分辨率切换、缩放调整、插拔显示器后重新贴回主屏底部。
        // Dock 不允许被拖动，位置完全由代码决定，这里就是唯一入口。
        if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED)
        {
            ScheduleReposition();
            return IntPtr.Zero;
        }

        if (msg == TrayIcon.CallbackMessage)
        {
            var mouseMessage = lParam.ToInt32();

            if (mouseMessage is WM_RBUTTONUP or WM_CONTEXTMENU)
            {
                ShowTrayMenu();
                handled = true;
                return IntPtr.Zero;
            }

            if (mouseMessage == WM_LBUTTONDBLCLK)
            {
                ToggleLauncher();
                handled = true;
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        if (msg != WM_MOUSEACTIVATE)
        {
            return IntPtr.Zero;
        }

        // 左键点击不激活窗口，避免抢走当前前台应用的焦点；
        // 右键需要激活，否则 WPF 的 ContextMenu 无法正常弹出。
        var rightButtonDown = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

        handled = true;
        return new IntPtr(rightButtonDown ? MA_ACTIVATE : MA_NOACTIVATE);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplySettings(_appSettings);

        // 固定在主屏。之前用「鼠标所在屏」会把 Dock 甩到副屏上，用户以为程序没起来。
        _targetMonitor = MonitorLayout.PrimaryMonitor();

        Height = Strip.MeasureHeight();
        PositionAtBottom();
        PlaceAboveDesktop();
        StartDesktopWatcher();
        _ = LoadIconsAsync();

        // 启动序列（图标 + 底图 + 首帧渲染）结束后修剪一次工作集，
        // 把 WPF 预热产生的驻留页交还给系统。
        Dispatcher.BeginInvoke(
            () => MemoryTrim.Trim(),
            DispatcherPriority.ApplicationIdle);
    }

    /// <summary>把设置应用到 Dock 并重新排布。</summary>
    /// <summary>上一次应用到的显示数量。不能拿 _appSettings 自身比较，见下方说明。</summary>
    private int _lastDockMaxItems = -1;

    private void ApplySettings(AppSettings settings)
    {
        // 显示数量变化需要重新挑选图标，不能只重算尺寸。
        //
        // 注意不能写 settings.DockMaxItems != _appSettings.DockMaxItems：
        // 第一次应用后 _appSettings 与设置窗口持有的是同一个对象，
        // 之后再改就是「自己和自己比」，永远相等，Dock 便不会刷新。
        var reloadNeeded = settings.DockMaxItems != _lastDockMaxItems;
        _lastDockMaxItems = settings.DockMaxItems;

        _appSettings = settings;

        Strip.IconSize = settings.IconSize;
        Strip.HoverScale = settings.HoverScale;

        if (reloadNeeded)
        {
            _ = LoadIconsAsync();
            return;
        }

        if (Strip.Items.Count > 0)
        {
            Width = Strip.MeasureWidth(Strip.Items.Count);
            Height = Strip.MeasureHeight();
            PositionAtBottom();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 面板复用后被 Hide 起来，退出时要显式关掉，让它走完整的清理路径。
        _launcher?.Close();
        _launcher = null;

        _tray?.Dispose();
        _tray = null;

        _desktopWatcher?.Stop();
        _desktopWatcher = null;

        _source?.RemoveHook(WndProc);
        _source = null;

        Strip.ItemActivated -= OnItemActivated;
        Strip.ContextRequested -= OnContextRequested;
        Strip.ItemDragStarted -= OnItemDragStarted;

        LibraryEvents.Changed -= OnLibraryChanged;
    }

    private void OnLibraryChanged()
    {
        Dispatcher.BeginInvoke(() => _ = LoadIconsAsync(refreshBackdrop: false));
    }

    private void PositionAtBottom()
    {
        // 用目标屏幕的工作区而不是 SystemParameters.WorkArea（那只代表主屏）。
        var workArea = MonitorLayout.GetWorkArea(this, _targetMonitor);

        var top = workArea.Bottom - Height - 10;
        var left = workArea.Left + ((workArea.Width - Width) / 2);

        // 离屏保护：算出来的位置必须真的落在某块屏幕上，
        // 否则退回主屏工作区，绝不能把 Dock 放到看不见的地方。
        var target = new Rect(left, top, Width, Height);
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        if (!virtualScreen.IntersectsWith(target))
        {
            var fallback = SystemParameters.WorkArea;

            left = fallback.Left + ((fallback.Width - Width) / 2);
            top = fallback.Bottom - Height - 10;

            AppLog.Info($"position off-screen, fallback to primary at ({left:F0},{top:F0})");
        }

        Left = left;
        Top = top;
    }

    // ---------- 图标加载 ----------

    private async Task LoadIconsAsync(bool refreshBackdrop = true)
    {
        // 在 UI 线程先取快照，避免后台线程读到被替换的设置对象。
        var maxItems = Math.Max(1, _appSettings.DockMaxItems);

        var (items, source, total) = await Task.Run(() =>
        {
            var service = CollectionService.CreateDefault();
            var libraryItems = service.Items.OrderBy(i => i.Sort).ToList();

            if (libraryItems.Count > 0)
            {
                // 优先显示用户勾选过的条目；一个都没勾时退化为「前 N 个」，
                // 保证老用户升级上来 Dock 不会突然变空。
                var pinned = libraryItems.Where(i => i.Pinned).ToList();
                var selection = pinned.Count > 0 ? pinned : libraryItems.Take(maxItems).ToList();

                var loaded = selection
                    .Select(item =>
                    {
                        var path = Path.Combine(service.CollectedDirectory, item.FileName);

                        return new DockItem
                        {
                            Path = path,
                            DisplayName = Path.GetFileNameWithoutExtension(item.FileName),
                            Icon = IconLoader.LoadCached(path, IconRequestSize)
                        };
                    })
                    .ToList();

                return (loaded, "library", libraryItems.Count);
            }

            var fallback = AppDiscovery.FindStartMenuShortcuts(maxItems)
                .Select(path => new DockItem
                {
                    Path = path,
                    DisplayName = Path.GetFileNameWithoutExtension(path),
                    Icon = IconLoader.LoadCached(path, IconRequestSize)
                })
                .ToList();

            return (fallback, "startmenu", 0);
        });

        // 后台预热菜单栏的图标缓存：首次打开菜单栏就不用现场提取几十个图标
        WarmUpLauncherIcons();

        var dockItems = new List<DockItem>
        {
            new() { Path = string.Empty, DisplayName = "更多", Kind = DockItemKind.Menu }
        };

        dockItems.AddRange(items);

        Strip.SetItems(dockItems);

        Width = Strip.MeasureWidth(dockItems.Count);
        Height = Strip.MeasureHeight();
        PositionAtBottom();

        AppLog.Info(
            $"icons loaded: {items.Count}/{total} source={source} " +
            $"window={Width:F0}x{Height:F0} at ({Left:F0},{Top:F0})");

        if (refreshBackdrop)
        {
            RefreshBackdrop();
        }
    }

    // ---------- 毛玻璃底图 ----------

    private async void RefreshBackdrop()
    {
        try
        {
            Opacity = 0;

            // Opacity=0 只是改了属性，分层窗口的 alpha 要等一次渲染提交才生效。
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(25);
            DwmFlush();

            var backdrop = ScreenBackdrop.Capture(this);
            Strip.SetBackdrop(backdrop);

            AppLog.Info(backdrop is null
                ? "backdrop: capture returned null"
                : $"backdrop: {backdrop.PixelWidth}x{backdrop.PixelHeight}px at ({Left:F0},{Top:F0})");
        }
        catch (Exception ex)
        {
            AppLog.Error("backdrop capture failed", ex);
            Strip.SetBackdrop(null);
        }
        finally
        {
            Opacity = 1;
            _backdropReady = true;

            // 记录这次底图是不是在「桌面为前台」时截的（即确定是壁纸），
            // 是的话切回桌面就不需要再重截。同时记录壁纸指纹供变更检测。
            _backdropIsDesktop = IsDesktopWindow(GetForegroundWindow());
            _lastWallpaperMark = ScreenBackdrop.CurrentWallpaperMark();
            _lastBackdropCaptureUtc = DateTime.UtcNow;
        }
    }

    private void StartDesktopWatcher()
    {
        _desktopWatcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };

        _desktopWatcher.Tick += (_, _) =>
        {
            var isDesktop = IsDesktopWindow(GetForegroundWindow());
            var wallpaperMark = ScreenBackdrop.CurrentWallpaperMark();

            // 底图重截只在必要时发生：
            // ① 上次截屏被应用挡住，切回桌面后第一次见到壁纸
            // ② 壁纸变了（换图 / 轮播 / 原图替换）
            // ③ 动态壁纸源（Wallpaper Engine）：指纹不变，节流为 45 秒一次
            // 其他情况一律不重截，避免每次切回桌面毛玻璃都闪一下。
            var isDynamicWallpaper = ScreenBackdrop.IsDynamicWallpaperSource();
            var dynamicDue = isDynamicWallpaper
                && (DateTime.UtcNow - _lastBackdropCaptureUtc).TotalSeconds >= 45;

            var needsRefresh = (!_backdropIsDesktop && !_desktopWasForeground)
                || wallpaperMark != _lastWallpaperMark
                || dynamicDue;

            if (isDesktop && _backdropReady && needsRefresh)
            {
                RefreshBackdrop();
            }

            _desktopWasForeground = isDesktop;

            // 这里刻意不做「全屏自动隐藏」。
            // Dock 位于桌面层（所有应用窗口之下），全屏应用本来就会把它盖住，
            // 额外的判定只会引入误判：最大化窗口的窗口矩形比屏幕大 8px，
            // IDE / 浏览器这类无边框应用的客户区也铺满整屏。
            // 一旦判错，Dock 就会莫名消失，用户完全无从理解。
        };

        _desktopWatcher.Start();
    }

    private DateTime _lastRepositionUtc = DateTime.MinValue;

    private void ScheduleReposition()
    {
        // 一次显示切换可能连发多条消息，节流一下。
        if ((DateTime.UtcNow - _lastRepositionUtc).TotalMilliseconds < 500)
        {
            return;
        }

        _lastRepositionUtc = DateTime.UtcNow;

        Dispatcher.BeginInvoke(() =>
        {
            _targetMonitor = MonitorLayout.PrimaryMonitor();

            Width = Strip.MeasureWidth(Strip.Items.Count);
            Height = Strip.MeasureHeight();
            PositionAtBottom();

            AppLog.Info($"reposition after display change: ({Left:F0},{Top:F0})");
        });
    }

    // ---------- 交互 ----------

    private void OnItemActivated(object? sender, DockItem item)
    {
        if (item.Kind == DockItemKind.Menu)
        {
            ToggleLauncher();
            return;
        }

        // 点任何应用图标都先收起启动台，避免面板一直盖在桌面上。
        CloseLauncher();
        Launch(item.Path);
    }

    private void OnContextRequested(object? sender, DockContextEventArgs e)
    {
        var menu = e.Item is { Kind: DockItemKind.App } item
            ? BuildItemMenu(item)
            : BuildAppMenu();

        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private ContextMenu BuildItemMenu(DockItem item)
    {
        var menu = new ContextMenu();

        menu.Items.Add(CreateMenuItem("打开", () => Launch(item.Path)));
        menu.Items.Add(CreateMenuItem("在文件夹中显示", () => RevealInExplorer(item.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("还原到桌面", () => RestoreItem(item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("打开收纳库文件夹", OpenLibraryFolder));

        return menu;
    }

    private ContextMenu BuildAppMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(CreateMenuItem("设置…", ShowSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("打开 / 关闭菜单栏", ToggleLauncher));
        menu.Items.Add(CreateMenuItem("刷新毛玻璃背景", RefreshBackdrop));
        menu.Items.Add(CreateMenuItem("重新加载图标", () => _ = LoadIconsAsync()));
        menu.Items.Add(CreateMenuItem("打开收纳库文件夹", OpenLibraryFolder));
        menu.Items.Add(new Separator());

        // 开机自启只在控制台里提供，右键菜单保持精简。
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("退出 DeskCaddy", Close));

        return menu;
    }

    private void ShowTrayMenu()
    {
        var menu = BuildAppMenu();
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();

        return item;
    }

    /// <summary>
    /// 第二个实例启动时调用：把菜单栏带到前台。
    /// 已经打开就只激活，不再重复创建。
    /// </summary>
    public void ShowLauncherFromSecondInstance()
    {
        if (_launcher is { IsVisible: true })
        {
            _launcher.Activate();
            return;
        }

        ToggleLauncher();
    }

    /// <summary>打开 / 关闭菜单栏。再点一次同一个入口应该收起。</summary>
    private void ToggleLauncher()
    {
        if (_launcher is { IsVisible: true })
        {
            CloseLauncher();
            return;
        }

        // 面板只创建一次，关闭走 Hide，反复开关不会累积窗口树与底图。
        if (_launcher is null)
        {
            var launcher = new LauncherWindow();

            // 真的被关掉（Alt+F4 / 程序退出）时才丢弃实例；
            // 不能用字段直接置空，否则下一次打开会连新窗口一起丢掉。
            launcher.Closed += (sender, _) =>
            {
                if (ReferenceEquals(_launcher, sender))
                {
                    _launcher = null;
                }
            };

            _launcher = launcher;
        }

        _launcher.ShowLauncher();
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow();
        _settingsWindow.SettingsApplied += (_, settings) => ApplySettings(settings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void CloseLauncher()
    {
        if (_launcher is { IsVisible: true })
        {
            _launcher.CloseWithAnimation();
        }
    }

    private static void Launch(string path)
    {
        AppLog.Info($"activate: {path}");

        if (!ShellLauncher.TryLaunch(path, out var shellError, out var lastError))
        {
            AppLog.Error($"启动失败：{path}（SE_ERR {shellError}，GetLastError {lastError}）");

            // 目标真的消失才会提供移除；临时性失败只报告错误
            _ = DeadShortcutHandler.OfferRemovalAsync(path, shellError, lastError);
        }
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("reveal failed", ex);
        }
    }

    private static void OpenLibraryFolder()
    {
        LibraryPaths.EnsureCreated();

        Process.Start(new ProcessStartInfo
        {
            FileName = LibraryPaths.CollectedDirectory,
            UseShellExecute = true
        });
    }

    private async void RestoreItem(DockItem item)
    {
        var fileName = Path.GetFileName(item.Path);

        var result = await Task.Run(() =>
        {
            var service = CollectionService.CreateDefault();
            return service.Restore(fileName, apply: true);
        });

        AppLog.Info($"restore {fileName}: {result.Message}");

        await LoadIconsAsync();
    }

    // ---------- 拖入收纳 ----------

    /// <summary>
    /// 自己发起的拖拽。用 WPF 的 DoDragDrop —— 它就是 OLE 拖放，
    /// 资源管理器能直接接收 FileDrop 并完成移动，不需要手写 COM。
    /// </summary>
    private void OnItemDragStarted(object? sender, DockItem item)
    {
        // 文件夹拖入收纳后也要能排序/移出，存在性检查必须包含目录
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
        {
            return;
        }

        var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });

        DragGhost.Source = item.Icon;
        Strip.BeginDragReorder(item);

        try
        {
            DragDrop.DoDragDrop(Strip, data, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            AppLog.Error("drag failed", ex);
        }

        _ = SyncAfterDragAsync();
    }

    /// <summary>拖拽结束后让索引跟上：文件可能已被拖到桌面，也可能只是内部排序。</summary>
    private async Task SyncAfterDragAsync()
    {
        HideDragGhost();
        Strip.EndDragReorder();

        await Task.Run(() =>
        {
            var service = CollectionService.CreateDefault();
            service.Reconcile(apply: true);
        });

        await LoadIconsAsync();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            HideDragGhost();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (IsFromLibrary(e.Data))
        {
            // 拖回自己身上 = 排序：实时让位 + 浮动图标跟随光标。
            ShowDragGhost(e.GetPosition(DragGhostHost));
            Strip.UpdateDragTarget(e.GetPosition(Strip).X);
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            // 外部拖入 = 收纳，撤销排序预览。
            HideDragGhost();
            Strip.EndDragReorder();
            e.Effects = DragDropEffects.Copy;
        }

        e.Handled = true;
    }

    /// <summary>拖拽时的浮动图标：跟随光标，离开 Dock 即隐藏。</summary>
    private void ShowDragGhost(Point position)
    {
        if (DragGhost.Source is null)
        {
            return;
        }

        Canvas.SetLeft(DragGhost, position.X - 20);
        Canvas.SetTop(DragGhost, position.Y - 20);
        DragGhostHost.Visibility = Visibility.Visible;
    }

    private void HideDragGhost()
    {
        DragGhostHost.Visibility = Visibility.Collapsed;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        HideDragGhost();
    }

    private static bool _iconWarmupStarted;

    /// <summary>
    /// 后台提取收纳库全部图标到全局缓存。
    /// 菜单栏每次打开都会加载全部条目，预热后它的 LoadItems 就是纯字典查找。
    /// </summary>
    private static void WarmUpLauncherIcons()
    {
        if (_iconWarmupStarted)
        {
            return;
        }

        _iconWarmupStarted = true;

        _ = Task.Run(() =>
        {
            try
            {
                var service = CollectionService.CreateDefault();

                foreach (var item in service.Items.OrderBy(i => i.Sort))
                {
                    IconLoader.LoadCached(
                        Path.Combine(service.CollectedDirectory, item.FileName),
                        IconRequestSize);
                }
            }
            catch (Exception)
            {
                // 预热失败无所谓，菜单栏打开时会再提取。
            }
        });
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        HideDragGhost();

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        // 拖回自己身上 = 调整顺序，不动文件。
        if (files.Length == 1 && IsCollectedFile(files[0]))
        {
            var dragged = Strip.Items.FirstOrDefault(i =>
                string.Equals(i.Path, files[0], StringComparison.OrdinalIgnoreCase));

            if (dragged is null)
            {
                Strip.EndDragReorder();
                return;
            }

            // 预览过程中图标已经被移动到目标位，直接取它当前的位置即可。
            var targetIndex = Strip.GetAppIndex(dragged);
            Strip.EndDragReorder();

            await ReorderAsync(dragged, targetIndex);
            return;
        }

        var results = await Task.Run(() =>
        {
            var service = CollectionService.CreateDefault();

            // 拖入模式：快捷方式之外也接受文件与文件夹
            return files
                .Select(file => service.Collect(file, apply: true, allowRealItems: true))
                .ToList();
        });

        foreach (var result in results)
        {
            AppLog.Info($"drop: {result.Message}");
        }

        await LoadIconsAsync();

        var failed = results.Where(r => !r.Success).Select(r => r.Message).ToList();

        if (failed.Count > 0)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, failed),
                "DeskCaddy 收纳",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static bool IsCollectedFile(string path) =>
        path.StartsWith(LibraryPaths.CollectedDirectory, StringComparison.OrdinalIgnoreCase);

    private static bool IsFromLibrary(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] files
        && files.Length == 1
        && IsCollectedFile(files[0]);

    private async Task ReorderAsync(DockItem item, int targetIndex)
    {
        var fileName = Path.GetFileName(item.Path);

        var result = await Task.Run(() =>
        {
            var service = CollectionService.CreateDefault();
            return service.Reorder(fileName, targetIndex);
        });

        AppLog.Info($"reorder: {result.Message}");

        await LoadIconsAsync();
    }

    // ---------- Win32 ----------

    private void PlaceAboveDesktop() => DesktopLayer.PlaceAboveDesktop(this);

    private static bool IsDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var buffer = new char[64];
        var length = GetClassNameW(hwnd, buffer, buffer.Length);
        if (length <= 0)
        {
            return false;
        }

        var className = new string(buffer, 0, length);

        return className is "Progman" or "WorkerW";
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, [Out] char[] className, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
