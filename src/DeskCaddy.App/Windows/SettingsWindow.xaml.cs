using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskCaddy.Core.Library;
using DeskCaddy.Core.Settings;
using DeskCaddy.Diagnostics;
using DeskCaddy.Settings;

namespace DeskCaddy.Windows;

public partial class SettingsWindow : Window
{
    private static readonly FontFamily GlyphFont = new("Segoe MDL2 Assets");

    private static readonly Brush PrimaryText = Frozen(Color.FromRgb(0x1F, 0x1F, 0x23));
    private static readonly Brush SecondaryText = Frozen(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x2F, 0x6B, 0xFF));
    private static readonly Brush CardBrush = Frozen(Color.FromArgb(0x0F, 0x00, 0x00, 0x00));

    private const string SectionGeneral = "general";
    private const string SectionAppearance = "appearance";
    private const string SectionDock = "dock";
    private const string SectionLauncher = "launcher";
    private const string SectionIsland = "island";
    private const string SectionMaintenance = "maintenance";
    private const string SectionAbout = "about";

    private readonly SettingsStore _store = SettingsStore.Default;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, StackPanel> _sections = [];
    private readonly Dictionary<string, RadioButton> _navButtons = [];
    private readonly List<(Border Card, string Section)> _cards = [];

    private string _currentSection = SectionGeneral;

    public SettingsWindow()
    {
        InitializeComponent();

        LogoImage.Source = LoadLogo();

        Loaded += (_, _) => RootClip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        SizeChanged += (_, _) => RootClip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);

        _settings = _store.Load();

        BuildGeneral();
        BuildAppearance();
        BuildDockIcons();
        BuildLauncher();
        BuildIsland();
        BuildMaintenance();
        BuildAbout();

        SelectSection(SectionGeneral);
    }

    /// <summary>设置变化时通知宿主窗口（Dock）实时应用。</summary>
    public event EventHandler<AppSettings>? SettingsApplied;

    /// <summary>
    /// 直接从程序集读 Logo 流。用 EmbeddedResource 而不是 pack URI，
    /// 资源名是固定的，加载失败会明确记日志而不是界面空白一片。
    /// </summary>
    private static BitmapSource? LoadLogo()
    {
        try
        {
            var assembly = typeof(SettingsWindow).Assembly;

            using var stream = assembly.GetManifestResourceStream("DeskCaddy.assets.logo.png");
            if (stream is null)
            {
                AppLog.Error("Logo 资源缺失：DeskCaddy.assets.logo.png");
                return null;
            }

            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();

            return frame;
        }
        catch (Exception ex)
        {
            AppLog.Error("加载 Logo 失败", ex);
            return null;
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        return brush;
    }

    // ---------- 分区 ----------

    private void BuildGeneral()
    {
        var host = AddSection(SectionGeneral, "常规", "\uE713");

        var startup = new CheckBox
        {
            Style = (Style)FindResource("Switch"),
            IsChecked = StartupRegistration.IsEnabled
        };

        startup.Click += (_, _) =>
        {
            var wanted = startup.IsChecked == true;
            var ok = StartupRegistration.SetEnabled(wanted);

            if (!ok)
            {
                startup.IsChecked = StartupRegistration.IsEnabled;
            }

            AppLog.Info($"startup requested={wanted} ok={ok}");
        };

        AddCard(host, SectionGeneral, "\uE7E8", "开机自动启动",
            "登录 Windows 后自动在桌面底部启动 Dock", startup);

        AddCard(host, SectionGeneral, "\uE8B7", "收纳库文件夹",
            LibraryPaths.CollectedDirectory,
            TextButton("打开", OpenLibraryFolder));
    }

    private void BuildAppearance()
    {
        var host = AddSection(SectionAppearance, "外观", "\uE790");

        // 图标大小
        var iconValue = ValueLabel(_settings.IconSize);
        var iconSlider = NumberSlider(AppSettings.MinIconSize, AppSettings.MaxIconSize, _settings.IconSize);
        iconSlider.ValueChanged += (_, e) =>
        {
            _settings.IconSize = Math.Round(e.NewValue);
            iconValue.Text = $"{_settings.IconSize:F0}";
            Apply();
        };

        AddCard(host, SectionAppearance, "\uE8A9", "图标大小",
            "Dock 上每个图标的边长（像素）",
            Horizontal(iconSlider, iconValue));

        // 悬停放大
        var scaleValue = ValueLabel(_settings.HoverScale * 100);
        scaleValue.Text = $"{_settings.HoverScale * 100:F0}%";

        var scaleSlider = NumberSlider(AppSettings.MinHoverScale, AppSettings.MaxHoverScale, _settings.HoverScale);
        scaleSlider.TickFrequency = 0.01;
        scaleSlider.ValueChanged += (_, e) =>
        {
            _settings.HoverScale = Math.Round(e.NewValue, 2);
            scaleValue.Text = $"{_settings.HoverScale * 100:F0}%";
            Apply();
        };

        AddCard(host, SectionAppearance, "\uE8A3", "悬停放大",
            "鼠标滑过图标时放大的比例，100% 表示不放大",
            Horizontal(scaleSlider, scaleValue));

        // Dock 显示数量
        var countValue = ValueLabel(_settings.DockMaxItems);
        var countSlider = NumberSlider(
            AppSettings.MinDockItems,
            AppSettings.MaxDockItemsLimit,
            _settings.DockMaxItems);

        countSlider.ValueChanged += (_, e) =>
        {
            _settings.DockMaxItems = (int)Math.Round(e.NewValue);
            countValue.Text = $"{_settings.DockMaxItems}";
            Apply();
        };

        AddCard(host, SectionAppearance, "\uE71D", "Dock 显示数量",
            "桌面上这条 Dock 最多显示几个图标，宽度会随数量自动调整",
            Horizontal(countSlider, countValue));
    }

    private void BuildDockIcons()
    {
        var host = AddSection(SectionDock, "Dock 图标", "\uE71D");

        PopulateDockCards(host);
    }

    /// <summary>清空后重新填充 Dock 图标分区（添加/移除后调用）。</summary>
    private void RefreshDockSection()
    {
        if (!_sections.TryGetValue(SectionDock, out var host))
        {
            return;
        }

        // 第一个子元素是分区标题，保留；其余卡片全部丢弃。
        while (host.Children.Count > 1)
        {
            host.Children.RemoveAt(host.Children.Count - 1);
        }

        _cards.RemoveAll(c => c.Section == SectionDock);

        PopulateDockCards(host);
    }

    private void PopulateDockCards(StackPanel host)
    {
        var items = CollectionService.CreateDefault().Items
            .OrderBy(i => i.Sort)
            .ToList();

        var pinned = items.Where(i => i.Pinned).ToList();

        AddCard(host, SectionDock, "\uE710", "添加图标",
            pinned.Count == 0
                ? "从收纳库里挑选要显示在 Dock 上的应用"
                : $"当前 {pinned.Count} 个，点右侧「＋」继续添加",
            TextButton("＋", ShowDockIconPicker));

        if (pinned.Count == 0)
        {
            AddCard(host, SectionDock, "\uE7BA", "还没有添加图标",
                "没有添加时，Dock 会显示收纳库中的前几个（数量见「外观」）", null);

            return;
        }

        foreach (var item in pinned)
        {
            var entry = item;

            AddCard(
                host,
                SectionDock,
                "\uE71D",
                Path.GetFileNameWithoutExtension(entry.FileName),
                string.Empty,
                TextButton("移除", () => RemoveDockIcon(entry.FileName)));
        }
    }

    private void ShowDockIconPicker()
    {
        var picker = new DockIconPickerWindow { Owner = this };

        picker.ShowDialog();

        if (picker.Changed)
        {
            LibraryEvents.RaiseChanged();
        }

        // 无论是否添加，都重建一次：候选集合可能已经变化。
        RefreshDockSection();
    }

    private void RemoveDockIcon(string fileName)
    {
        var result = CollectionService.CreateDefault().SetPinned(fileName, false);

        if (!result.Success)
        {
            AppLog.Error($"移除 Dock 图标失败：{result.Message}");
            return;
        }

        AppLog.Info(result.Message);
        LibraryEvents.RaiseChanged();

        RefreshDockSection();
    }

    private void BuildLauncher()
    {
        var host = AddSection(SectionLauncher, "菜单栏", "\uE71D");

        // 双击打开
        var doubleClick = new CheckBox
        {
            Style = (Style)FindResource("Switch"),
            IsChecked = _settings.LauncherOpenOnDoubleClick
        };

        doubleClick.Click += (_, _) =>
        {
            _settings.LauncherOpenOnDoubleClick = doubleClick.IsChecked == true;
            Apply();
        };

        AddCard(host, SectionLauncher, "\uE962", "双击打开",
            "开启后单击只选中，双击才启动应用；关闭则单击直接打开",
            doubleClick);

        // 菜单栏图标大小：越小一行能放越多
        var iconValue = ValueLabel(_settings.LauncherIconSize);
        var iconSlider = NumberSlider(
            AppSettings.MinLauncherIconSize,
            AppSettings.MaxLauncherIconSize,
            _settings.LauncherIconSize);

        iconSlider.TickFrequency = 2;

        iconSlider.ValueChanged += (_, e) =>
        {
            _settings.LauncherIconSize = Math.Round(e.NewValue);
            iconValue.Text = $"{_settings.LauncherIconSize:F0}";
            Apply();
        };

        AddCard(host, SectionLauncher, "\uE8A9", "图标大小",
            "图标越小，一行能放下的应用越多",
            Horizontal(iconSlider, iconValue));

        var columnValue = ValueLabel(_settings.LauncherColumns);
        var columnSlider = NumberSlider(
            AppSettings.MinLauncherColumns,
            AppSettings.MaxLauncherColumns,
            _settings.LauncherColumns);

        columnSlider.ValueChanged += (_, e) =>
        {
            _settings.LauncherColumns = (int)Math.Round(e.NewValue);
            columnValue.Text = $"{_settings.LauncherColumns}";
            Apply();
        };

        AddCard(host, SectionLauncher, "\uE80A", "每行列数",
            "菜单栏面板一行最多放几个图标（上限固定 10）",
            Horizontal(columnSlider, columnValue));
    }

    private void BuildIsland()
    {
        var host = AddSection(SectionIsland, "灵动岛", "\uE8D6");

        var enabled = new CheckBox
        {
            Style = (Style)FindResource("Switch"),
            IsChecked = _settings.IslandEnabled
        };

        enabled.Click += (_, _) =>
        {
            _settings.IslandEnabled = enabled.IsChecked == true;
            Apply();
        };

        AddCard(host, SectionIsland, "\uE768", "音乐灵动岛",
            "屏幕顶部悬浮的音乐播放条，只在系统里有音乐播放时出现",
            enabled);

        var widthValue = ValueLabel(_settings.IslandWidth);
        var widthSlider = NumberSlider(
            AppSettings.MinIslandWidth,
            AppSettings.MaxIslandWidth,
            _settings.IslandWidth);

        widthSlider.TickFrequency = 20;

        widthSlider.ValueChanged += (_, e) =>
        {
            _settings.IslandWidth = Math.Round(e.NewValue);
            widthValue.Text = $"{_settings.IslandWidth:F0}";
            Apply();
        };

        AddCard(host, SectionIsland, "\uE8A9", "宽度",
            "鼠标悬停展开时的宽度（像素），收起状态固定为 220",
            Horizontal(widthSlider, widthValue));
    }

    private void BuildMaintenance()
    {
        var host = AddSection(SectionMaintenance, "维护", "\uE90F");

        AddCard(host, SectionMaintenance, "\uE8B7", "打开收纳库",
            "查看所有被收纳的快捷方式",
            TextButton("打开", OpenLibraryFolder));

        AddCard(host, SectionMaintenance, "\uE7C3", "打开日志",
            "排查问题时把这里的文件发出来",
            TextButton("打开", OpenLogFolder));

        AddCard(host, SectionMaintenance, "\uE777", "全部还原到桌面",
            "把收纳库里所有快捷方式移回桌面，此操作不可撤销",
            TextButton("还原", RestoreAllShortcuts));
    }

    private void BuildAbout()
    {
        var host = AddSection(SectionAbout, "关于", "\uE946");

        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "未知";

        AddCard(host, SectionAbout, "\uE7B8", "DeskCaddy", $"版本 {version}", null);
        AddCard(host, SectionAbout, "\uE8F1", "收纳条目",
            $"{CountCollected()} 个快捷方式", null);
        AddCard(host, SectionAbout, "\uE838", "数据目录", LibraryPaths.Root, null);
    }

    // ---------- 构建辅助 ----------

    private StackPanel AddSection(string key, string title, string glyph)
    {
        var host = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 12)
        };

        host.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryText,
            Margin = new Thickness(1, 6, 0, 16)
        });

        ContentHost.Children.Add(host);

        var nav = new RadioButton
        {
            Content = title,
            Tag = glyph,
            GroupName = "nav",
            Style = (Style)FindResource("NavButton")
        };

        nav.Checked += (_, _) => SelectSection(key);
        NavHost.Children.Add(nav);

        _sections[key] = host;
        _navButtons[key] = nav;

        return host;
    }

    private void AddCard(
        StackPanel host,
        string section,
        string glyph,
        string title,
        string description,
        UIElement? trailing,
        Action? onClick = null)
    {
        var grid = new Grid();

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = GlyphFont,
            FontSize = 17,
            Width = 22,
            TextAlignment = TextAlignment.Center,
            Foreground = AccentBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };

        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        texts.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = PrimaryText,
            FontSize = 13.5,
            FontWeight = FontWeights.Medium
        });

        if (!string.IsNullOrWhiteSpace(description))
        {
            texts.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = SecondaryText,
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }

        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        if (trailing is not null)
        {
            trailing.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }

        var card = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
            Tag = $"{title} {description}"
        };

        if (onClick is not null)
        {
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, _) => onClick();
        }

        host.Children.Add(card);
        _cards.Add((card, section));
    }

    private static TextBlock ValueLabel(double value) => new()
    {
        Text = $"{value:F0}",
        Foreground = PrimaryText,
        FontSize = 12,
        Width = 42,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };

    private Slider NumberSlider(double min, double max, double value) => new()
    {
        Style = (Style)FindResource("FlatSlider"),
        Minimum = min,
        Maximum = max,
        Value = value,
        Width = 150,
        IsSnapToTickEnabled = true,
        TickFrequency = 1,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0)
    };

    private static StackPanel Horizontal(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    private Button TextButton(string content, Action action)
    {
        var button = new Button
        {
            Content = content,
            Style = (Style)FindResource("GhostButton")
        };

        button.Click += (_, _) => action();

        return button;
    }

    // ---------- 行为 ----------

    private void SelectSection(string key)
    {
        _currentSection = key;

        if (_navButtons.TryGetValue(key, out var nav) && nav.IsChecked != true)
        {
            nav.IsChecked = true;
        }

        if (SearchBox.Text.Trim().Length > 0)
        {
            return;
        }

        foreach (var (sectionKey, host) in _sections)
        {
            host.Visibility = sectionKey == key ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter(SearchBox.Text.Trim());
    }

    private void ApplyFilter(string keyword)
    {
        if (keyword.Length == 0)
        {
            foreach (var (card, _) in _cards)
            {
                card.Visibility = Visibility.Visible;
            }

            SelectSection(_currentSection);
            return;
        }

        foreach (var (card, _) in _cards)
        {
            var text = card.Tag as string ?? string.Empty;

            card.Visibility = text.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        foreach (var (sectionKey, host) in _sections)
        {
            var hasMatch = _cards.Any(c => c.Section == sectionKey && c.Card.Visibility == Visibility.Visible);

            host.Visibility = hasMatch ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Apply()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("保存设置失败", ex);
        }

        SettingsApplied?.Invoke(this, _settings);
    }

    private static int CountCollected()
    {
        try
        {
            var state = StateStore.Default.Load(out _);
            return state.Items.Count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void OpenLibraryFolder()
    {
        LibraryPaths.EnsureCreated();
        OpenInExplorer(LibraryPaths.CollectedDirectory);
    }

    private static void OpenLogFolder()
    {
        var directory = Path.Combine(LibraryPaths.Root, "logs");
        Directory.CreateDirectory(directory);
        OpenInExplorer(directory);
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("打开目录失败", ex);
        }
    }

    private async void RestoreAllShortcuts()
    {
        var confirm = MessageBox.Show(
            "把收纳库里所有快捷方式移回桌面？\n\n只移动快捷方式本身，不会影响任何程序文件。",
            "DeskCaddy",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var result = await Task.Run(() =>
        {
            var service = CollectionService.CreateDefault();
            return service.RestoreAll(apply: true);
        });

        AppLog.Info($"restore-all: ok={result.Succeeded} failed={result.Failed}");

        MessageBox.Show(
            $"已还原 {result.Succeeded} 个，失败 {result.Failed} 个。",
            "DeskCaddy",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// 整窗可拖动：按下时如果落在空白处就拖窗口，落在按钮/滑块/输入框/可点击卡片上则交给它们。
    /// 用 Preview 而非普通事件，避免被子元素的默认行为吃掉。
    /// </summary>
    private void OnWindowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanStartDrag(e.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标在拖动真正开始前就已释放（快速点击），忽略即可。
        }

        e.Handled = true;
    }

    /// <summary>
    /// 从命中的元素往上找，只要途中遇到「需要自己处理点击」的控件就放弃拖动。
    /// </summary>
    private static bool CanStartDrag(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Window)
            {
                return true;
            }

            // 注意不要排除 ScrollViewer：它铺满整个内容区，排除掉就等于内容区完全拖不动。
            // 只排除真正需要鼠标操作的部件。
            if (node is ButtonBase or TextBoxBase or RangeBase or ScrollBar or Thumb)
            {
                return false;
            }

            // 可点击的卡片统一设了 Hand 光标，同样不抢它的点击。
            if (node is FrameworkElement element && element.Cursor == Cursors.Hand)
            {
                return false;
            }
        }

        return true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
