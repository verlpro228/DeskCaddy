using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DeskCaddy.Models;

namespace DeskCaddy.Rendering;

/// <summary>
/// 单个自绘元素承载整条 Dock。
/// 刻意不使用 Image / Border 等控件：每个 UIElement 都带布局、命中测试和渲染
/// 节点开销，几十个图标会白白多出十几 MB。
/// </summary>
public sealed class DockStripView : FrameworkElement
{
    private const double Gap = 8;

    /// <summary>
    /// 左右留白。必须 ≥ 悬停光晕的外扩量（图标 10% ≈ 5px）加余量，
    /// 否则第一个/最后一个图标的选中光晕会溢出胶囊边界（用户实测反馈）。
    /// </summary>
    private const double PadX = 14;

    private const double PadY = 7;
    private const double InfluenceFactor = 1.6;
    private const double ActivationDebounceMs = 400;

    private double _iconSize = 46;
    private double _hoverScale = 1.08;

    /// <summary>Dock 图标边长（DIP），由设置控制。</summary>
    public double IconSize
    {
        get => _iconSize;
        set
        {
            _iconSize = Math.Clamp(value, 24, 96);
            InvalidateVisual();
        }
    }

    /// <summary>悬停放大倍数。mac 的做法是点到为止，这里默认只做很轻微的鼓起。</summary>
    public double HoverScale
    {
        get => _hoverScale;
        set
        {
            _hoverScale = Math.Clamp(value, 1.0, 1.5);
            InvalidateVisual();
        }
    }

    private readonly List<DockItem> _items = [];

    /// <summary>
    /// 实际参与渲染的顺序。拖拽排序时它就是「预览后的顺序」，
    /// 松手前就能看到图标让位；未拖拽时与 _items 一致。
    /// 让位采用即时交换（与菜单栏一致）：按图标做水平插值时，
    /// 各图标到达新位置的速度不同，会出现短暂的左右重叠。
    /// </summary>
    private readonly List<DockItem> _display = [];

    private DockItem? _dragSource;
    private double _targetX = double.NaN;
    private double _currentX = double.NaN;
    private bool _frameHooked;
    private DateTime _lastActivationUtc = DateTime.MinValue;
    private ImageSource? _backdrop;
    private Point _pressPoint;
    private DockItem? _pressedItem;
    private bool _dragging;

    public DockStripView()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Focusable = false;
    }

    public event EventHandler<DockItem>? ItemActivated;

    public event EventHandler<DockContextEventArgs>? ContextRequested;

    /// <summary>按住图标拖出一定距离后触发，由宿主发起系统拖放。</summary>
    public event EventHandler<DockItem>? ItemDragStarted;

    public IReadOnlyList<DockItem> Items => _items;

    /// <summary>窗口宽度：图标总宽 + 两端留白 + 放大所需余量。</summary>
    public double MeasureWidth(int count)
    {
        if (count <= 0)
        {
            return PadX * 2;
        }

        return (PadX * 2)
            + (count * _iconSize)
            + ((count - 1) * Gap)
            + (_iconSize * (_hoverScale - 1));
    }

    public double MeasureHeight() =>
        _iconSize + (PadY * 2) + (_iconSize * (_hoverScale - 1));

    public void SetItems(IEnumerable<DockItem> items)
    {
        _items.Clear();
        _items.AddRange(items);

        _dragSource = null;
        RebuildDisplay();

        InvalidateVisual();
    }

    /// <summary>开始拖拽排序：之后 UpdateDragTarget 会实时调整显示顺序。</summary>
    public void BeginDragReorder(DockItem item)
    {
        _dragSource = item;
        RebuildDisplay();
        InvalidateVisual();
    }

    /// <summary>
    /// 拖拽过程中根据光标位置移动图标。以每个图标的中线为界，
    /// 跨过中线就交换位置，形成「让位」效果。
    /// </summary>
    public void UpdateDragTarget(double cursorX)
    {
        if (_dragSource is null)
        {
            return;
        }

        // OLE 拖拽期间 OnMouseMove 不会触发，悬停放大要用 DragOver 的光标位置。
        _targetX = cursorX;
        _currentX = cursorX;

        var sourceIndex = _display.IndexOf(_dragSource);
        if (sourceIndex < 0)
        {
            return;
        }

        var rects = ComputeLayout(cursorX);

        var target = _display.Count - 1;
        for (var i = 0; i < rects.Length; i++)
        {
            if (cursorX < rects[i].X + (rects[i].Width / 2))
            {
                target = i;
                break;
            }
        }

        if (target == sourceIndex)
        {
            return;
        }

        _display.RemoveAt(sourceIndex);
        _display.Insert(Math.Clamp(target, 0, _display.Count), _dragSource);

        InvalidateVisual();
        HookFrame();
    }

    /// <summary>结束拖拽排序，恢复为真实顺序（松手后由宿主重新加载列表）。</summary>
    public void EndDragReorder()
    {
        if (_dragSource is null)
        {
            return;
        }

        _dragSource = null;
        RebuildDisplay();

        InvalidateVisual();
        HookFrame();
    }

    /// <summary>某个图标在「收纳库顺序」里的序号（不含最左侧的「更多」入口）。</summary>
    public int GetAppIndex(DockItem item)
    {
        var index = _display.IndexOf(item);

        return index <= 0 ? 0 : index - 1;
    }

    private void RebuildDisplay()
    {
        _display.Clear();
        _display.AddRange(_items);
    }

    /// <summary>设置毛玻璃底图。传 null 时退化为纯半透明。</summary>
    public void SetBackdrop(ImageSource? backdrop)
    {
        _backdrop = backdrop;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);

        // Transparent 画刷参与命中测试，保证空白区域也能收到鼠标事件。
        dc.DrawRectangle(Brushes.Transparent, null, bounds);

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        // 胶囊圆角：半径取短边的一半。单行 Dock 这样最接近 macOS，
        // 图标多、宽度拉长时也会自然变成跑道形，不需要额外判断。
        var radius = Math.Min(bounds.Width, bounds.Height) / 2;
        var body = new Rect(0, 0, bounds.Width, bounds.Height);

        // 1) 毛玻璃底图（已模糊），裁剪成圆角
        if (_backdrop is not null)
        {
            dc.PushClip(new RectangleGeometry(body, radius, radius));
            dc.DrawImage(_backdrop, body);
            dc.Pop();
        }

        // 2) 半透明着色层，压暗并统一色调
        var background = _backdrop is null
            ? new LinearGradientBrush(
                Color.FromArgb(0xCC, 0x1A, 0x1A, 0x20),
                Color.FromArgb(0xCC, 0x0E, 0x0E, 0x12),
                90)
            : new LinearGradientBrush(
                Color.FromArgb(0x14, 0x06, 0x06, 0x0A),
                Color.FromArgb(0x2E, 0x06, 0x06, 0x0A),
                90);

        background.Freeze();

        var border = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
        border.Freeze();

        dc.DrawRoundedRectangle(background, border, body, radius, radius);

        // 3) 顶部高光，模拟玻璃上沿的反光。胶囊形状下只有中间一段是平的，
        //    宽度不够时画出来会是一小截杂线，直接跳过。
        if (_backdrop is not null && bounds.Width > radius * 3)
        {
            var highlight = new Pen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), 1);
            highlight.Freeze();

            var inset = radius * 0.8;

            dc.DrawLine(
                highlight,
                new Point(inset, 1.5),
                new Point(bounds.Width - inset, 1.5));
        }

        if (_display.Count == 0)
        {
            return;
        }

        var rects = ComputeLayout(_currentX);

        // 悬停项的浅色选中底：透明度跟随放大程度渐入渐出。
        // 「更多」入口不放大，悬停它时光晕恒定显示。
        if (!double.IsNaN(_currentX))
        {
            var hoverIndex = FindHoverIndex(rects, _currentX);
            var hoverIsMenu = hoverIndex == 0 && _display.Count > 0 && _display[0].Kind == DockItemKind.Menu;

            if (hoverIndex >= 0)
            {
                var hovered = rects[hoverIndex];
                double t;

                if (hoverIsMenu)
                {
                    t = 1.0;
                }
                else
                {
                    var factor = _iconSize > 0 ? hovered.Width / _iconSize : 1.0;
                    t = _hoverScale > 1.001
                        ? Math.Clamp((factor - 1) / (_hoverScale - 1), 0, 1)
                        : 1.0;
                }

                if (t > 0.02)
                {
                    DrawHoverHalo(dc, hovered, t);
                }
            }
        }

        for (var i = 0; i < _display.Count; i++)
        {
            var item = _display[i];
            var rect = rects[i];

            if (item.Kind == DockItemKind.Menu)
            {
                DrawMenuGlyph(dc, rect);
            }
            else if (item.Icon is not null)
            {
                dc.DrawImage(item.Icon, rect);
            }
            else
            {
                DrawFallbackIcon(dc, item, rect);
            }
        }
    }

    private static int FindHoverIndex(Rect[] rects, double x)
    {
        for (var i = 0; i < rects.Length; i++)
        {
            if (x >= rects[i].X && x <= rects[i].X + rects[i].Width)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>悬停高光：单层白色圆角，透明度随放大幅度渐入渐出。</summary>
    private void DrawHoverHalo(DrawingContext dc, Rect rect, double t)
    {
        var pad = rect.Width * 0.10;

        var halo = new Rect(
            rect.X - pad,
            rect.Y - pad,
            rect.Width + (pad * 2),
            rect.Height + (pad * 2));

        // 收进窗口边界：最左/最右图标的光晕不允许溢出胶囊
        var left = Math.Max(1, halo.X);
        var top = Math.Max(1, halo.Y);
        var right = Math.Min(ActualWidth - 1, halo.Right);
        var bottom = Math.Min(ActualHeight - 1, halo.Bottom);

        halo = new Rect(left, top, right - left, bottom - top);

        if (halo.Width <= 2 || halo.Height <= 2)
        {
            return;
        }

        var radius = Math.Min(halo.Width, halo.Height) * 0.24;

        dc.DrawRoundedRectangle(
            CreateFrozen(Color.FromArgb((byte)(0x2E * t), 0xFF, 0xFF, 0xFF)),
            null,
            halo,
            radius,
            radius);
    }

    /// <summary>「更多」入口：半透明白底 + 3×3 圆角方块，接近 Launchpad 的观感。</summary>
    private static void DrawMenuGlyph(DrawingContext dc, Rect rect)
    {
        var radius = rect.Width * 0.24;
        dc.DrawRoundedRectangle(MenuBackgroundBrush, null, rect, radius, radius);

        var cell = rect.Width * 0.155;
        var gap = rect.Width * 0.072;
        var total = (cell * 3) + (gap * 2);
        var startX = rect.X + ((rect.Width - total) / 2);
        var startY = rect.Y + ((rect.Height - total) / 2);
        var cellRadius = cell * 0.3;

        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var x = startX + (column * (cell + gap));
                var y = startY + (row * (cell + gap));

                dc.DrawRoundedRectangle(MenuGlyphBrush, null, new Rect(x, y, cell, cell), cellRadius, cellRadius);
            }
        }
    }

    private static readonly Brush MenuBackgroundBrush = CreateFrozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    private static readonly Brush MenuGlyphBrush = CreateFrozen(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));

    private static Brush CreateFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void DrawFallbackIcon(DrawingContext dc, DockItem item, Rect rect)
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xCC, 0x3A, 0x3A, 0x44));
        brush.Freeze();
        dc.DrawRoundedRectangle(brush, null, rect, rect.Width * 0.22, rect.Width * 0.22);

        if (string.IsNullOrEmpty(item.DisplayName))
        {
            return;
        }

        var text = item.DisplayName[..1].ToUpperInvariant();
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            rect.Height * 0.5,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(
            formatted,
            new Point(
                rect.X + ((rect.Width - formatted.Width) / 2),
                rect.Y + ((rect.Height - formatted.Height) / 2)));
    }

    private Rect[] ComputeLayout(double mouseX)
    {
        var count = _display.Count;
        var rects = new Rect[count];
        if (count == 0)
        {
            return rects;
        }

        var unit = _iconSize + Gap;
        var influenceRadius = InfluenceFactor * unit;

        // 「更多」入口是固定锚点：不放大、不随右侧图标晃动（用户明确要求）。
        var hasMenu = _display[0].Kind == DockItemKind.Menu;
        var firstApp = hasMenu ? 1 : 0;
        var appsStart = PadX + (hasMenu ? _iconSize + Gap : 0);

        var scales = new double[count];
        for (var i = firstApp; i < count; i++)
        {
            if (double.IsNaN(mouseX))
            {
                scales[i] = 1.0;
                continue;
            }

            var center = appsStart + ((i - firstApp) * unit) + (_iconSize / 2);
            var distance = Math.Abs(mouseX - center);
            var influence = distance >= influenceRadius
                ? 0
                : 0.5 * (1 + Math.Cos(Math.PI * distance / influenceRadius));

            scales[i] = 1 + ((_hoverScale - 1) * influence);
        }

        // 应用图标在「菜单按钮右侧的剩余空间」内居中
        var appsCount = count - firstApp;
        var total = Gap * Math.Max(0, appsCount - 1);
        for (var i = firstApp; i < count; i++)
        {
            total += _iconSize * scales[i];
        }

        var regionWidth = ActualWidth - appsStart - PadX;
        var x = appsStart + Math.Max(0, (regionWidth - total) / 2);

        if (hasMenu)
        {
            rects[0] = new Rect(PadX, (ActualHeight - _iconSize) / 2, _iconSize, _iconSize);
        }

        for (var i = firstApp; i < count; i++)
        {
            var size = _iconSize * scales[i];

            // 垂直居中。放大时向上下均匀溢出，容器高度已预留了放大余量，
            // 所以不会被裁掉。
            rects[i] = new Rect(x, (ActualHeight - size) / 2, size, size);
            x += size + Gap;
        }

        return rects;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        _dragging = false;
        _pressPoint = e.GetPosition(this);

        // 「更多」入口不参与拖拽，避免把整个菜单拖出去。
        _pressedItem = FindItemAt(_pressPoint) is { Kind: DockItemKind.App } item ? item : null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var position = e.GetPosition(this);
        _targetX = position.X;
        HookFrame();

        if (_pressedItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var dx = Math.Abs(position.X - _pressPoint.X);
        var dy = Math.Abs(position.Y - _pressPoint.Y);

        if (dx < SystemParameters.MinimumHorizontalDragDistance
            && dy < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _pressedItem;
        _pressedItem = null;
        _dragging = true;

        ItemDragStarted?.Invoke(this, item);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _targetX = double.NaN;
        HookFrame();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        // 刚结束一次拖拽，这次抬起不能当成点击。
        if (_dragging)
        {
            _dragging = false;
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(this);
        var rects = ComputeLayout(_currentX);

        for (var i = 0; i < rects.Length; i++)
        {
            if (!rects[i].Contains(position))
            {
                continue;
            }

            // 双击会产生两次 Up 事件；Dock 是单击启动，忽略紧随其后的第二次。
            var now = DateTime.UtcNow;
            if ((now - _lastActivationUtc).TotalMilliseconds < ActivationDebounceMs)
            {
                e.Handled = true;
                break;
            }

            _lastActivationUtc = now;

            ItemActivated?.Invoke(this, _display[i]);
            e.Handled = true;
            break;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);

        var position = e.GetPosition(this);
        var item = FindItemAt(position);

        ContextRequested?.Invoke(this, new DockContextEventArgs(item, PointToScreen(position)));
        e.Handled = true;
    }

    /// <summary>
    /// 根据落点计算插入位置（0 = 最左侧，含「更多」入口）。
    /// 以每个图标的中线为界，落在左半边就插到它前面。
    /// </summary>
    public int GetDropIndex(Point position)
    {
        var rects = ComputeLayout(_currentX);

        for (var i = 0; i < rects.Length; i++)
        {
            if (position.X < rects[i].X + (rects[i].Width / 2))
            {
                return i;
            }
        }

        return rects.Length;
    }

    private DockItem? FindItemAt(Point position)
    {
        var rects = ComputeLayout(_currentX);

        for (var i = 0; i < rects.Length && i < _display.Count; i++)
        {
            if (rects[i].Contains(position))
            {
                return _display[i];
            }
        }

        return null;
    }

    private void HookFrame()
    {
        if (_frameHooked)
        {
            return;
        }

        _frameHooked = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void UnhookFrame()
    {
        if (!_frameHooked)
        {
            return;
        }

        _frameHooked = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (double.IsNaN(_targetX))
        {
            _currentX = double.NaN;
            InvalidateVisual();
            UnhookFrame();
            return;
        }

        if (double.IsNaN(_currentX))
        {
            _currentX = _targetX;
        }

        var delta = _targetX - _currentX;
        if (Math.Abs(delta) < 0.35)
        {
            _currentX = _targetX;
            InvalidateVisual();
            UnhookFrame();
            return;
        }

        _currentX += delta * 0.38;
        InvalidateVisual();
    }
}
