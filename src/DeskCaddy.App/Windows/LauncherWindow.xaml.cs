using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskCaddy.Core.Library;
using DeskCaddy.Core.Settings;
using DeskCaddy.Core.Shell;
using DeskCaddy.Diagnostics;
using DeskCaddy.Models;
using DeskCaddy.Rendering;

namespace DeskCaddy.Windows;

public partial class LauncherWindow : Window
{
    private const int IconRequestSize = 96;

    private const int VK_LBUTTON = 0x01;

    private List<LauncherItem> _allItems = [];

    /// <summary>当前分组筛选；null = 全部，空字符串 = 未分组。</summary>
    private string? _groupFilter;

    private List<string> _groups = [];

    /// <summary>新建分组对话框打开期间，不要因为失活/点外把面板收起来。</summary>
    private bool _dialogOpen;

    private Button? _newGroupButton;

    /// <summary>拖拽结束后延迟复位 _draggingOut，吞掉 OLE 松手产生的误点击。</summary>
    private DispatcherTimer? _dragResetTimer;

    private Point _pressPoint;
    private LauncherItem? _pressedItem;
    private LauncherItem? _selectedItem;
    private LauncherItem? _dragItem;
    private bool _draggingOut;
    private bool _droppedOnPanel;
    private bool _closing;

    /// <summary>当前显示的列表（含筛选结果）。拖拽排序在它上面做 Move。</summary>
    private ObservableCollection<LauncherItem>? _viewItems;

    private double _cellWidth = 118;
    private double _cellHeight = 126;
    private double _iconSize = 54;
    private double _labelWidth = 106;
    private bool _openOnDoubleClick = true;

    public LauncherWindow()
    {
        InitializeComponent();

        // 截图前必须不可见，否则会把面板自身截进底图。
        Opacity = 0;

        SizeChanged += OnSizeChanged;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;

        // Dock / 菜单栏任何一方改动收纳库，另一方都要同步
        LibraryEvents.Changed += OnLibraryChanged;
    }

    /// <summary>
    /// 打开面板。窗口实例会被复用（关闭只是 Hide），不再每次新建：
    /// 反复开关面板曾让内存按每次 5~9MB 线性增长，窗口树和重截的底图
    /// 都要等很久才被回收。复用后无论开关多少次，常驻内存只有一份。
    /// </summary>
    public async void ShowLauncher()
    {
        if (_closing && IsVisible)
        {
            return;
        }

        _closing = false;

        // 上一次启动应用留下的「跳过这次失活」标记不能跨会话沿用，
        // 否则复用窗口后的第一次点外会被白白吞掉。
        _launchedFromPanel = false;
        _groupFilter = null;

        // 复用窗口时，上一轮拖拽的状态可能还留在字段里。若不重置，
        // 点外关闭会被 _draggingOut 拦掉（表现为「又关不掉了」）。
        _dragResetTimer?.Stop();
        _dragResetTimer = null;
        _draggingOut = false;
        _droppedOnPanel = false;
        _dialogOpen = false;

        // 先透明再显示，避免在旧位置/旧尺寸上闪一帧。
        Opacity = 0;

        if (!IsVisible)
        {
            Show();
        }

        SearchBox.Clear();

        ApplyLayout(SettingsStore.Default.Load());
        LoadItems();
        AnchorAboveDock();
        UpdateClip();

        try
        {
            await RefreshBackdropAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("launcher open failed", ex);
        }

        // 底图就绪后再播动画：截屏期间窗口透明、动画中途不再插帧。
        PlayOpenAnimation();

        // 归属桌面层：打开时不抢焦点，也不覆盖任何应用窗口。
        DesktopLayer.PlaceAboveDesktop(this);
        SearchBox.Focus();

        StartOutsideClickWatcher();
    }

    /// <summary>
    /// 点击面板外（切到其他窗口 / 点桌面）即关闭——与 Esc、菜单按钮并列的关闭方式。
    /// OLE 拖拽期间的失活要跳过：此时关闭面板会销毁拖放源，中断整个拖拽。
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_dialogOpen || _draggingOut)
        {
            return;
        }

        // 由「点击图标启动应用」引起的失活不算关闭理由：
        // 用户要连续开几个应用，面板得保持打开。
        if (_launchedFromPanel)
        {
            _launchedFromPanel = false;
            return;
        }

        _draggingOut = false;
        PlayCloseAnimation();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateClip();

    private void OnLibraryChanged()
    {
        // 隐藏时不用重建视觉树；下次打开一定会重新 LoadItems。
        if (!IsVisible)
        {
            return;
        }

        LoadItems();
    }

    // ---------- 面板外点击检测 ----------

    private DispatcherTimer? _outsideClickWatcher;
    private bool _launchedFromPanel;

    /// <summary>
    /// 启动应用后面板会失活，此后点击面板外不会再触发 Deactivated。
    /// 用轻量轮询兜底：面板非激活 + 左键按下 + 光标在面板外 → 关闭。
    /// </summary>
    private void StartOutsideClickWatcher()
    {
        if (_outsideClickWatcher is not null)
        {
            return;
        }

        _outsideClickWatcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _outsideClickWatcher.Tick += OnOutsideClickTick;
        _outsideClickWatcher.Start();
    }

    private void OnOutsideClickTick(object? sender, EventArgs e)
    {
        if (_closing || _draggingOut || _dialogOpen)
        {
            return;
        }

        // 必须用 Win32 的物理键状态：窗口复用后再次 Show 时会被激活，
        // 而 WPF 的 Mouse.LeftButton 只跟随本进程收到的鼠标消息，点桌面时
        // 可能一直是 Released，看起来就是「点外面关不掉」。
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
        {
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var topLeft = PointToScreen(new Point(0, 0));
        var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));

        var inside = cursor.X >= topLeft.X && cursor.X <= bottomRight.X
            && cursor.Y >= topLeft.Y && cursor.Y <= bottomRight.Y;

        if (inside)
        {
            return;
        }

        // 点在右键菜单 / 工具提示 / Dock 等本进程的弹出窗口上，不算点面板外。
        var hit = WindowFromPoint(cursor);

        if (hit != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hit, out var processId);

            if (processId == Environment.ProcessId)
            {
                return;
            }
        }

        PlayCloseAnimation();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point32 point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    protected override void OnClosed(EventArgs e)
    {
        StopOutsideClickWatcher();

        _dragResetTimer?.Stop();
        _dragResetTimer = null;
        _draggingOut = false;
        _droppedOnPanel = false;

        LibraryEvents.Changed -= OnLibraryChanged;

        // 真正关闭（退出程序 / Alt+F4）时才拆掉视觉树引用。
        Backdrop.Source = null;
        ItemsHost.ItemsSource = null;
        DragGhost.Source = null;
        GroupChips.Children.Clear();
        _viewItems = null;
        _allItems.Clear();
        _groups.Clear();
        _groupFilter = null;
        _dialogOpen = false;

        MemoryTrim.Trim();

        base.OnClosed(e);
    }

    /// <summary>拖拽结束后延迟复位，吞掉 OLE 松手产生的误点击。</summary>
    private void ScheduleDragStateReset()
    {
        _dragResetTimer?.Stop();
        _dragResetTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };

        _dragResetTimer.Tick += (_, _) =>
        {
            _dragResetTimer?.Stop();
            _dragResetTimer = null;

            _draggingOut = false;
            _droppedOnPanel = false;
        };

        _dragResetTimer.Start();
    }

    /// <summary>关闭动画结束后隐藏窗口，保留实例供下次复用。</summary>
    private void HideForReuse()
    {
        _dragResetTimer?.Stop();
        _dragResetTimer = null;
        _draggingOut = false;
        _droppedOnPanel = false;

        Hide();
        StopOutsideClickWatcher();

        // 保留窗口（省掉重建窗口的初始化开销），但拆掉内容：
        // 51 个格子的按钮、容器与底图是常驻内存的大头，下次打开重建
        // 只需几十毫秒（图标本身来自全局缓存）。
        ClearSelection();
        _viewItems = null;
        _allItems.Clear();
        ItemsHost.ItemsSource = null;
        DragGhost.Source = null;
        GroupChips.Children.Clear();
        _groups.Clear();
        _groupFilter = null;

        // 缓存在 ScreenBackdrop 里的位图仍是热的，下次打开同一位置会命中。
        Backdrop.Source = null;

        // 用户暂时不再交互：释放视觉树引用的位图并修剪工作集，
        // 否则任务管理器里的占用会一直停在打开面板时的高位。
        MemoryTrim.Trim();
    }

    private void StopOutsideClickWatcher()
    {
        _outsideClickWatcher?.Stop();
        _outsideClickWatcher = null;
    }

    private void UpdateClip() =>
        RootClip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);

    /// <summary>
    /// 网格单元尺寸由「启动台图标大小」决定，面板宽度由「每行列数」决定。
    /// 图标变小 → 单元变小 → 同样宽度下一行能放更多。
    /// </summary>
    private void ApplyLayout(AppSettings settings)
    {
        _openOnDoubleClick = settings.LauncherOpenOnDoubleClick;
        _iconSize = settings.LauncherIconSize;
        _cellWidth = _iconSize + 64;
        _cellHeight = _iconSize + 72;
        _labelWidth = _cellWidth - 12;

        var columns = Math.Clamp(
            settings.LauncherColumns,
            AppSettings.MinLauncherColumns,
            AppSettings.MaxLauncherColumns);

        // +2 是每个单元的左右 Margin
        Width = (columns * (_cellWidth + 2)) + 48;
        Height = 520;
    }

    /// <summary>
    /// 水平居中、底部贴在 Dock 上方。面板尺寸固定，图标从左上按顺序排列。
    /// </summary>
    private void AnchorAboveDock()
    {
        var workArea = SystemParameters.WorkArea;

        const double DockHeight = 65;
        const double DockGap = 10;
        const double PanelGap = 12;

        Left = workArea.Left + ((workArea.Width - Width) / 2);
        Top = workArea.Bottom - DockHeight - DockGap - PanelGap - Height;
    }

    private void LoadItems()
    {
        var service = CollectionService.CreateDefault();

        _allItems = service.Items
            .OrderBy(i => i.Sort)
            .Select(item =>
            {
                var path = Path.Combine(service.CollectedDirectory, item.FileName);

                return new LauncherItem
                {
                    Path = path,
                    Name = Path.GetFileNameWithoutExtension(item.FileName),
                    Group = item.Group ?? string.Empty,
                    Icon = IconLoader.LoadCached(path, IconRequestSize),
                    CellWidth = _cellWidth,
                    CellHeight = _cellHeight,
                    IconSize = _iconSize,
                    LabelWidth = _labelWidth
                };
            })
            .ToList();

        // 分组以 state.json 为准；顺手把「条目有分组但 Groups 里没有」的旧数据补上。
        _groups = service.Groups.ToList();

        foreach (var item in _allItems)
        {
            if (item.Group.Length > 0
                && !_groups.Any(g => string.Equals(g, item.Group, StringComparison.OrdinalIgnoreCase)))
            {
                _groups.Add(item.Group);
            }
        }

        if (_groupFilter is { Length: > 0 }
            && !_groups.Any(g => string.Equals(g, _groupFilter, StringComparison.OrdinalIgnoreCase)))
        {
            _groupFilter = null;
        }

        RefreshGroupChips();

        // 保留当前搜索词：右键菜单执行后重载时不该把用户的输入清掉。
        ApplyFilter(SearchBox.Text);

        AppLog.Info($"launcher opened: {_allItems.Count} items");
    }

    /// <summary>收纳库快照：文件名 → 时间戳与大小。用于判断右键菜单是否真的改动了文件。</summary>
    private static Dictionary<string, (long Ticks, long Length)> SnapshotLibrary()
    {
        var snapshot = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!Directory.Exists(LibraryPaths.CollectedDirectory))
            {
                return snapshot;
            }

            foreach (var file in Directory.EnumerateFiles(LibraryPaths.CollectedDirectory))
            {
                var info = new FileInfo(file);
                snapshot[info.Name] = (info.LastWriteTimeUtc.Ticks, info.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 快照失败就当作「有变化」，走一次完整刷新，宁可多刷新也不要漏更新。
            snapshot[string.Empty] = (0, 0);
        }

        return snapshot;
    }

    private static bool SameSnapshot(
        Dictionary<string, (long Ticks, long Length)> before,
        Dictionary<string, (long Ticks, long Length)> after)
    {
        if (before.Count != after.Count)
        {
            return false;
        }

        foreach (var pair in before)
        {
            if (!after.TryGetValue(pair.Key, out var other) || other != pair.Value)
            {
                return false;
            }
        }

        return true;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 筛选后原来的选中项可能已经不在列表里，直接清掉。
        ClearSelection();
        ApplyFilter(SearchBox.Text);
    }

    private void ApplyFilter(string query)
    {
        var keyword = query.Trim();

        IEnumerable<LauncherItem> scope = _allItems;

        if (_groupFilter is not null)
        {
            scope = _groupFilter.Length == 0
                ? _allItems.Where(i => i.Group.Length == 0)
                : _allItems.Where(i => string.Equals(i.Group, _groupFilter, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = keyword.Length == 0
            ? scope.ToList()
            : scope
                .Where(i => i.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

        // 用 ObservableCollection：拖拽排序时 Move 只移动一个容器，
        // 换成 List + 重新赋值会整表重建，拖动过程中会闪烁。
        _viewItems = new ObservableCollection<LauncherItem>(filtered);
        ItemsHost.ItemsSource = _viewItems;

        // 切换分组/搜索词后回到顶部：否则滚动位置还停在上一组的下方，
        // 新结果会落在视口外，看起来像「这个分组是空的」。
        ItemsScroll.ScrollToTop();

        if (filtered.Count == 0 && _allItems.Count > 0)
        {
            EmptyHint.Text = keyword.Length > 0
                ? "没有匹配的应用"
                : _groupFilter is null ? "没有可显示的应用" : "这个分组还没有条目";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- 分组 ----------

    private void RefreshGroupChips()
    {
        GroupChips.Children.Clear();

        AddGroupChip("全部", null);

        // 「未分组」不再作为标签：只显示已有分组。移出分组仍可通过右键菜单完成。
        foreach (var group in _groups)
        {
            AddGroupChip(group, group);
        }

        var add = new Button
        {
            Content = "＋",
            Style = (Style)FindResource("GroupChipButton"),
            ToolTip = "拖动图标到这里可新建分组并归入"
        };

        add.Click += (_, _) => CreateGroupPrompt();
        _newGroupButton = add;
        GroupChips.Children.Add(add);
    }

    private void AddGroupChip(string label, string? filter)
    {
        var chip = new RadioButton
        {
            Content = label,
            Style = (Style)FindResource("GroupChip"),
            GroupName = "LauncherGroups",
            Tag = filter,
            IsChecked = filter is null
                ? _groupFilter is null
                : string.Equals(filter, _groupFilter, StringComparison.Ordinal)
        };

        // 用 Checked 覆盖鼠标、键盘和辅助工具（UIA Select）三条路径；
        // 重建时设置 IsChecked 会多跑一次 ApplyFilter，但结果一致。
        chip.Checked += (sender, _) =>
        {
            _groupFilter = (sender as RadioButton)?.Tag as string;
            ApplyFilter(SearchBox.Text);
        };

        if (!string.IsNullOrEmpty(filter))
        {
            var menu = new ContextMenu();
            menu.Items.Add(CreateMenuItem("重命名…", () => RenameGroupPrompt(filter)));
            menu.Items.Add(CreateMenuItem("删除分组", () => DeleteGroupPrompt(filter)));
            chip.ContextMenu = menu;
        }

        // 「全部」不是存放目标；未分组与具名分组都接受拖入。
        if (filter is not null)
        {
            chip.ToolTip = filter.Length == 0
                ? "把图标拖到这里移出分组"
                : "把图标拖到这里";
        }

        GroupChips.Children.Add(chip);
    }

    /// <summary>
    /// 命中分组标签：返回 (标签控件, 目标分组, 是否新建分组)。
    /// Tag 为 null 的是「全部」（不可放）；空字符串是「未分组」。
    /// 用窗口级 DragOver/Drop + 命中测试统一处理，避免依赖子控件的 AllowDrop
    /// 在分层窗口/滚动容器里的命中差异。
    /// </summary>
    private (Control Chip, string? Group, bool IsNewGroup)? FindGroupDropTarget(Point position)
    {
        var hit = GroupChips.InputHitTest(position) as DependencyObject;

        while (hit is not null && hit is not RadioButton && hit is not Button)
        {
            hit = VisualTreeHelper.GetParent(hit);
        }

        if (hit is RadioButton radio)
        {
            return radio.Tag is string group ? (radio, group, false) : null;
        }

        if (hit is Button button && ReferenceEquals(button, _newGroupButton))
        {
            return (button, null, true);
        }

        return null;
    }

    private static bool TryGetDraggedLibraryFile(IDataObject data, out string path)
    {
        path = string.Empty;

        if (!data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (data.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1)
        {
            return false;
        }

        if (!files[0].StartsWith(LibraryPaths.CollectedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = files[0];

        return File.Exists(path) || Directory.Exists(path);
    }

    private void HighlightGroupDropTarget(Control target)
    {
        foreach (var child in GroupChips.Children)
        {
            if (child is not Control control)
            {
                continue;
            }

            var isTarget = ReferenceEquals(control, target);

            control.Opacity = isTarget ? 1.0 : 0.55;
            control.FontWeight = isTarget ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void ClearGroupDropHighlights()
    {
        foreach (var child in GroupChips.Children)
        {
            if (child is Control control)
            {
                control.Opacity = 1.0;
                control.FontWeight = FontWeights.Normal;
            }
        }
    }

    private async void CreateGroupPrompt(string? assignFileName = null)
    {
        if (_dialogOpen)
        {
            return;
        }

        _dialogOpen = true;

        try
        {
            var dialog = new GroupNameWindow { Owner = this };

            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.GroupName))
            {
                return;
            }

            var name = dialog.GroupName.Trim();

            var (result, canonical) = await Task.Run(() =>
            {
                var service = CollectionService.CreateDefault();
                var created = service.CreateGroup(name);

                var actual = service.Groups.FirstOrDefault(g =>
                    string.Equals(g, name, StringComparison.OrdinalIgnoreCase)) ?? name;

                if (assignFileName is not null)
                {
                    service.SetGroup(assignFileName, actual);
                }

                return (created, actual);
            });

            AppLog.Info(result.Message);

            if (assignFileName is null)
            {
                _groupFilter = canonical;
            }

            LoadItems();
        }
        catch (Exception ex)
        {
            AppLog.Error("create group failed", ex);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async void RenameGroupPrompt(string oldName)
    {
        if (_dialogOpen)
        {
            return;
        }

        _dialogOpen = true;

        try
        {
            var dialog = new GroupNameWindow("重命名分组", oldName) { Owner = this };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var newName = dialog.GroupName.Trim();

            if (string.Equals(newName, oldName, StringComparison.Ordinal))
            {
                return;
            }

            var result = await Task.Run(() =>
                CollectionService.CreateDefault().RenameGroup(oldName, newName));

            AppLog.Info(result.Message);

            if (!result.Success)
            {
                MessageBox.Show(result.Message, "重命名分组", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.Equals(_groupFilter, oldName, StringComparison.OrdinalIgnoreCase))
            {
                _groupFilter = newName;
            }

            LoadItems();
        }
        catch (Exception ex)
        {
            AppLog.Error("rename group failed", ex);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async void DeleteGroupPrompt(string groupName)
    {
        if (_dialogOpen)
        {
            return;
        }

        _dialogOpen = true;

        try
        {
            var count = _allItems.Count(i =>
                string.Equals(i.Group, groupName, StringComparison.OrdinalIgnoreCase));

            var answer = MessageBox.Show(
                $"删除分组「{groupName}」？{Environment.NewLine}{Environment.NewLine}" +
                $"组内 {count} 个条目会回到「未分组」。",
                "删除分组",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            var result = await Task.Run(() =>
                CollectionService.CreateDefault().DeleteGroup(groupName));

            AppLog.Info(result.Message);

            if (!result.Success)
            {
                MessageBox.Show(result.Message, "删除分组", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.Equals(_groupFilter, groupName, StringComparison.OrdinalIgnoreCase))
            {
                _groupFilter = null;
            }

            LoadItems();
        }
        catch (Exception ex)
        {
            AppLog.Error("delete group failed", ex);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async void AssignGroup(string fileName, string? group)
    {
        var result = await Task.Run(() =>
            CollectionService.CreateDefault().SetGroup(fileName, group));

        AppLog.Info(result.Message);
        LoadItems();
    }

    private MenuItem BuildGroupSubmenu(LauncherItem item)
    {
        var root = new MenuItem { Header = "移动到分组" };

        var fileName = Path.GetFileName(item.Path);

        foreach (var group in _groups)
        {
            var entry = CreateMenuItem(group, () => AssignGroup(fileName, group));
            entry.IsCheckable = true;
            entry.IsChecked = string.Equals(item.Group, group, StringComparison.OrdinalIgnoreCase);
            root.Items.Add(entry);
        }

        if (_groups.Count > 0)
        {
            root.Items.Add(new Separator());
        }

        var ungrouped = CreateMenuItem("未分组", () => AssignGroup(fileName, null));
        ungrouped.IsCheckable = true;
        ungrouped.IsChecked = item.Group.Length == 0;
        root.Items.Add(ungrouped);
        root.Items.Add(new Separator());
        root.Items.Add(CreateMenuItem("新建分组…", () => CreateGroupPrompt(fileName)));

        return root;
    }

    /// <summary>打开动画：面板从 Dock 方向滑入并淡入。</summary>
    private void PlayOpenAnimation()
    {
        // 先清掉关闭动画的残留值，再从 Dock 方向出发
        RootShift.BeginAnimation(TranslateTransform.YProperty, null);
        RootShift.Y = 84;

        Root.BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        var slide = new DoubleAnimation(84, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));

        RootShift.BeginAnimation(TranslateTransform.YProperty, slide);
        Root.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>关闭动画：向 Dock 方向滑出并淡出，结束后真正关闭窗口。</summary>
    public void CloseWithAnimation() => PlayCloseAnimation();

    private void PlayCloseAnimation()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;

        var slide = new DoubleAnimation(RootShift.Y, 70, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        slide.Completed += (_, _) => HideForReuse();

        RootShift.BeginAnimation(TranslateTransform.YProperty, slide);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100)));
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;

        if (ItemsHost.ItemsSource is not IEnumerable<LauncherItem> filtered)
        {
            return;
        }

        var first = filtered.FirstOrDefault();
        if (first is not null)
        {
            ActivateItem(first);
        }
    }

    private async Task RefreshBackdropAsync()
    {
        try
        {
            Opacity = 0;

            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(25);

            // 面板显示「面板正后方区域」的模糊（区域截取，内存占用最小）。
            // 动态壁纸源（Wallpaper Engine）指纹不变，每次打开强制重截。
            var forceRefresh = ScreenBackdrop.IsDynamicWallpaperSource();
            var backdrop = ScreenBackdrop.CaptureCached(this, ScreenBackdrop.PanelBlurRadius, forceRefresh);
            Backdrop.Source = backdrop;
        }
        catch (Exception ex)
        {
            AppLog.Error("launcher backdrop failed", ex);
        }
        finally
        {
            Opacity = 1;
        }
    }

    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        if (_draggingOut)
        {
            // 刚刚是一次拖拽，抬起不该当成点击。
            return;
        }

        if (sender is not FrameworkElement { Tag: LauncherItem item })
        {
            return;
        }

        // 双击模式：第一次点击只做选中反馈，启动交给 OnTileDoubleClick。
        if (_openOnDoubleClick)
        {
            SelectItem(item);
            return;
        }

        ActivateItem(item);
    }

    private void SelectItem(LauncherItem item)
    {
        if (!ReferenceEquals(_selectedItem, item) && _selectedItem is not null)
        {
            _selectedItem.IsSelected = false;
        }

        item.IsSelected = true;
        _selectedItem = item;
    }

    private void ClearSelection()
    {
        if (_selectedItem is null)
        {
            return;
        }

        _selectedItem.IsSelected = false;
        _selectedItem = null;
    }

    private void OnTileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_draggingOut || !_openOnDoubleClick)
        {
            return;
        }

        e.Handled = true;

        if (sender is FrameworkElement { Tag: LauncherItem item })
        {
            ActivateItem(item);
        }
    }

    /// <summary>
    /// 右键菜单：打开 / 移动到分组 / 定位。
    /// 文件的资源管理器原生菜单收在「系统菜单…」里；文件夹禁用原生菜单
    /// （会加载大量 shell 扩展，实测卡死并崩掉进程）。
    /// </summary>
    private void OnTileRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LauncherItem item)
        {
            return;
        }

        e.Handled = true;

        var menu = BuildTileMenu(item);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private ContextMenu BuildTileMenu(LauncherItem item)
    {
        var isFolder = Directory.Exists(item.Path);
        var menu = new ContextMenu();

        menu.Items.Add(CreateMenuItem("打开", () => ActivateItem(item)));
        menu.Items.Add(BuildGroupSubmenu(item));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("在文件夹中显示", () => RevealInExplorer(item.Path)));

        if (isFolder)
        {
            menu.Items.Add(CreateMenuItem("还原到桌面", () => RestoreItemToDesktop(item)));
        }
        else
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("系统菜单…", () => ShowNativeMenu(item)));
        }

        return menu;
    }

    /// <summary>文件的原生资源管理器菜单。只对文件开放，文件夹会崩。</summary>
    private void ShowNativeMenu(LauncherItem item)
    {
        var before = SnapshotLibrary();

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (!ShellContextMenu.Show(hwnd, item.Path, out var error))
        {
            AppLog.Error($"原生右键菜单失败：{error}");
        }

        // 大多数右键操作（打开、属性、剪切）并不改动收纳库。
        // 无条件重载会让所有图标重新提取一遍、重建全部按钮，看起来就是在「加载」。
        if (!SameSnapshot(before, SnapshotLibrary()))
        {
            _ = RefreshAfterContextMenuAsync();
        }
    }

    private async void RestoreItemToDesktop(LauncherItem item)
    {
        var result = await Task.Run(() =>
        {
            var service = CollectionService.CreateDefault();
            return service.Restore(Path.GetFileName(item.Path), apply: true);
        });

        AppLog.Info($"restore: {result.Message}");
        LoadItems();
        LibraryEvents.RaiseChanged();
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            AppLog.Error($"定位失败：{path}", ex);
        }
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var menuItem = new MenuItem { Header = header };
        menuItem.Click += (_, _) => action();

        return menuItem;
    }

    /// <summary>右键菜单可能删除/重命名/移动了条目，结束后重新对齐索引。</summary>
    private async Task RefreshAfterContextMenuAsync()
    {
        await Task.Run(() =>
        {
            var service = CollectionService.CreateDefault();
            service.Reconcile(apply: true);
        });

        LoadItems();
        LibraryEvents.RaiseChanged();
    }

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingOut = false;
        _pressPoint = e.GetPosition(this);
        _pressedItem = (sender as FrameworkElement)?.Tag as LauncherItem;
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(this);

        if (Math.Abs(position.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _pressedItem;
        _pressedItem = null;

        // 文件夹拖入收纳后也要能排序/移出，存在性检查必须包含目录
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
        {
            return;
        }

        _dragResetTimer?.Stop();
        _dragResetTimer = null;

        _draggingOut = true;
        _dragItem = item;
        _droppedOnPanel = false;

        // 每次拖拽都刷新浮动图标：之前只在 Source 为空时设置，
        // 导致第二次起永远显示第一次拖的那个（实测一直是 Steam）。
        DragGhost.Source = item.Icon;

        // 与 Dock 一致：交给 OLE。拖到桌面 / 资源管理器 = 移出收纳；
        // 松手落回面板上 = 排序（OnPanelDrop 处理）。
        var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });

        try
        {
            DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            AppLog.Error("launcher drag failed", ex);
        }
        finally
        {
            ClearGroupDropHighlights();
            HideDragGhost();
        }

        _dragItem = null;

        if (_droppedOnPanel)
        {
            // 排序/分组已完成；短暂保留 _draggingOut 吞掉 OLE 松手产生的误点击，
            // 随后必须复位，否则之后的点外/失活都会被它拦住。
            _draggingOut = true;
            ScheduleDragStateReset();
            return;
        }

        _ = FinishDragAsync();
    }

    private bool IsOwnDrag(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop)
        && data.GetData(DataFormats.FileDrop) is string[] files
        && files.Length == 1
        && files[0].StartsWith(LibraryPaths.CollectedDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>拖动经过面板时：悬停在分组标签上优先作为分组目标，否则实时排序。</summary>
    private void OnPanelDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            HideDragGhost();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (TryGetDraggedLibraryFile(e.Data, out _))
        {
            var target = FindGroupDropTarget(e.GetPosition(GroupChips));

            if (target is { } drop)
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;

                ShowDragGhost(e.GetPosition(DragGhostHost));
                HighlightGroupDropTarget(drop.Chip);
                return;
            }
        }

        ClearGroupDropHighlights();

        // 有搜索词时不排序：可见的是筛选结果，与落库顺序对不上。
        if (!IsOwnDrag(e.Data) || _dragItem is null || _viewItems is null
            || SearchBox.Text.Trim().Length > 0)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        ShowDragGhost(e.GetPosition(DragGhostHost));
        PreviewReorder(e.GetPosition(ItemsHost));
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>拖拽时的浮动图标：跟随光标，离开面板即隐藏。</summary>
    private void ShowDragGhost(Point position)
    {
        if (_dragItem is null || DragGhost.Source is null)
        {
            return;
        }

        Canvas.SetLeft(DragGhost, position.X - 22);
        Canvas.SetTop(DragGhost, position.Y - 22);
        DragGhostHost.Visibility = Visibility.Visible;
    }

    private void HideDragGhost()
    {
        DragGhostHost.Visibility = Visibility.Collapsed;
    }

    private void OnPanelDragLeave(object sender, DragEventArgs e)
    {
        HideDragGhost();
        ClearGroupDropHighlights();
    }

    private void PreviewReorder(Point position)
    {
        if (_dragItem is null || _viewItems is null)
        {
            return;
        }

        var sourceIndex = _viewItems.IndexOf(_dragItem);
        if (sourceIndex < 0)
        {
            return;
        }

        var target = FindSlotIndex(position, sourceIndex);
        if (target < 0 || target == sourceIndex)
        {
            return;
        }

        _viewItems.Move(sourceIndex, target);
    }

    private int FindSlotIndex(Point position, int fallback)
    {
        for (var i = 0; i < ItemsHost.Items.Count; i++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
            {
                continue;
            }

            var topLeft = container.TranslatePoint(new Point(0, 0), ItemsHost);
            var bounds = new Rect(topLeft, new Size(container.ActualWidth, container.ActualHeight));

            if (bounds.Contains(position))
            {
                return i;
            }
        }

        return fallback;
    }

    private void OnPanelDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (TryGetDraggedLibraryFile(e.Data, out var droppedPath))
        {
            var target = FindGroupDropTarget(e.GetPosition(GroupChips));

            if (target is { } drop)
            {
                e.Handled = true;
                e.Effects = DragDropEffects.Move;

                HideDragGhost();
                ClearGroupDropHighlights();

                // 面板自己发起的拖拽：只是改分组，不要走拖出/关闭流程。
                if (_dragItem is not null)
                {
                    _droppedOnPanel = true;
                }

                var droppedName = Path.GetFileName(droppedPath);

                AppLog.Info(drop.IsNewGroup
                    ? $"group drop: (new) file={droppedName}"
                    : $"group drop: {(drop.Group!.Length == 0 ? "(ungrouped)" : drop.Group)} file={droppedName}");

                // 在 OLE 拖拽循环结束后再改状态/弹对话框，避免在 Drop 回调里重建视觉树。
                if (drop.IsNewGroup)
                {
                    Dispatcher.BeginInvoke(
                        () => CreateGroupPrompt(droppedName),
                        DispatcherPriority.Background);
                }
                else
                {
                    Dispatcher.BeginInvoke(
                        () => AssignGroup(droppedName, drop.Group),
                        DispatcherPriority.Background);
                }

                return;
            }
        }

        if (!IsOwnDrag(e.Data) || _dragItem is null || _viewItems is null
            || SearchBox.Text.Trim().Length > 0)
        {
            return;
        }

        var finalIndex = _viewItems.IndexOf(_dragItem);
        var fileName = Path.GetFileName(_dragItem.Path);

        HideDragGhost();
        _droppedOnPanel = true;
        e.Handled = true;

        Task.Run(() => CollectionService.CreateDefault().Reorder(fileName, finalIndex))
            .ContinueWith(
                _ =>
                {
                    LibraryEvents.RaiseChanged();

                    // 重建列表，让显示顺序与 state.json 完全一致。
                    LoadItems();
                },
                TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task FinishDragAsync()
    {
        await Task.Run(() =>
        {
            var service = CollectionService.CreateDefault();
            service.Reconcile(apply: true);
        });

        // 拖出会真正改动收纳库（文件已移走），这里必须广播，
        // Dock 靠它同步；普通关闭（Esc / 按钮 / 点外部）不广播、Dock 不刷新。
        LibraryEvents.RaiseChanged();

        PlayCloseAnimation();
    }

    private void ActivateItem(LauncherItem item)
    {
        AppLog.Info($"launcher activate: {item.Path}");

        // 启动后面板保持打开：用户可能要连续开多个应用。
        // 启动会让新进程抢前台、面板失活，OnDeactivated 里据此跳过一次关闭。
        _launchedFromPanel = true;

        if (!ShellLauncher.TryLaunch(item.Path, out var shellError, out var lastError))
        {
            AppLog.Error($"启动失败：{item.Path}（SE_ERR {shellError}，GetLastError {lastError}）");
            _launchedFromPanel = false;

            // 目标真的消失才会提供移除；临时性失败只报告错误
            _ = DeadShortcutHandler.OfferRemovalAsync(item.Path, shellError, lastError);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;

            if (SearchBox.Text.Length > 0)
            {
                SearchBox.Clear();
                return;
            }

            PlayCloseAnimation();
        }
    }
}
