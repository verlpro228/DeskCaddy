using System.Windows.Media.Imaging;

namespace DeskCaddy.Models;

public enum DockItemKind
{
    /// <summary>收纳库里的应用快捷方式。</summary>
    App,

    /// <summary>固定在 Dock 最左侧的「更多」入口，点开启动台。</summary>
    Menu
}

public sealed class DockItem
{
    public required string Path { get; init; }

    public required string DisplayName { get; init; }

    public BitmapSource? Icon { get; init; }

    public DockItemKind Kind { get; init; } = DockItemKind.App;
}
