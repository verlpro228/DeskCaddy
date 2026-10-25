using System.Windows;
using DeskCaddy.Models;

namespace DeskCaddy.Rendering;

/// <summary>
/// Dock 上的右键请求。<see cref="Item"/> 为 null 表示点在空白处。
/// </summary>
public sealed class DockContextEventArgs : EventArgs
{
    public DockContextEventArgs(DockItem? item, Point screenPoint)
    {
        Item = item;
        ScreenPoint = screenPoint;
    }

    public DockItem? Item { get; }

    public Point ScreenPoint { get; }
}
