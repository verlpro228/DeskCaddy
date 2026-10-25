using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskCaddy.Core.Library;
using DeskCaddy.Diagnostics;

namespace DeskCaddy.Windows;

/// <summary>
/// 从收纳库里挑选要显示在 Dock 上的应用。
/// 点一行即添加，窗口保持打开，方便连续添加多个。
/// </summary>
public partial class DockIconPickerWindow : Window
{
    private static readonly Brush PrimaryText = Frozen(Color.FromRgb(0x1F, 0x1F, 0x23));
    private static readonly Brush SecondaryText = Frozen(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x2F, 0x6B, 0xFF));

    private readonly List<(string FileName, string DisplayName)> _candidates;

    public DockIconPickerWindow()
    {
        InitializeComponent();

        // 只列出「还没在 Dock 上」的条目
        _candidates = CollectionService.CreateDefault().Items
            .Where(i => !i.Pinned)
            .OrderBy(i => i.Sort)
            .Select(i => (i.FileName, Path.GetFileNameWithoutExtension(i.FileName)))
            .ToList();

        RebuildList(string.Empty);
    }

    /// <summary>添加过图标时置为 true，调用方据此决定是否刷新。</summary>
    public bool Changed { get; private set; }

    private void RebuildList(string keyword)
    {
        ListHost.Children.Clear();

        var matches = keyword.Length == 0
            ? _candidates
            : _candidates
                .Where(c => c.DisplayName.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

        if (matches.Count == 0)
        {
            ListHost.Children.Add(new TextBlock
            {
                Text = _candidates.Count == 0 ? "收纳库里的条目都已经在 Dock 上了" : "没有匹配的应用",
                Foreground = SecondaryText,
                FontSize = 12.5,
                Margin = new Thickness(10, 16, 10, 0)
            });

            return;
        }

        foreach (var candidate in matches)
        {
            ListHost.Children.Add(CreateRow(candidate.FileName, candidate.DisplayName));
        }
    }

    private Border CreateRow(string fileName, string displayName)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = displayName,
            Foreground = PrimaryText,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var add = new TextBlock
        {
            Text = "＋",
            Foreground = AccentBrush,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0)
        };

        Grid.SetColumn(add, 1);
        grid.Children.Add(add);

        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(4, 0, 4, 2),
            Child = grid,
            Cursor = Cursors.Hand
        };

        row.MouseLeftButtonUp += (_, _) => AddItem(row, fileName);
        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x0F, 0, 0, 0));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

        return row;
    }

    private void AddItem(Border row, string fileName)
    {
        var result = CollectionService.CreateDefault().SetPinned(fileName, true);

        if (!result.Success)
        {
            AppLog.Error($"添加 Dock 图标失败：{result.Message}");
            return;
        }

        AppLog.Info(result.Message);
        Changed = true;

        // 添加后从候选里移除，避免重复点。
        _candidates.RemoveAll(c => string.Equals(c.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        ListHost.Children.Remove(row);

        if (ListHost.Children.Count == 0)
        {
            RebuildList(SearchBox.Text.Trim());
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildList(SearchBox.Text.Trim());
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        return brush;
    }
}
