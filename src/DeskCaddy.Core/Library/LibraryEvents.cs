namespace DeskCaddy.Core.Library;

/// <summary>
/// 收纳库内容变化通知。
///
/// Dock 与菜单栏是两个独立窗口，各自持有图标和顺序。任何一方改动了收纳库
/// （拖出、右键删除/重命名、还原），都必须通知另一方，否则会出现
/// 「菜单栏里已经没了，Dock 上还挂着」这种不一致。
/// </summary>
public static class LibraryEvents
{
    public static event Action? Changed;

    public static void RaiseChanged() => Changed?.Invoke();
}
