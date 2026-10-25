using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskCaddy.Diagnostics;

namespace DeskCaddy.Windows;

/// <summary>
/// 把窗口插到「桌面层」：位于桌面图标之上、所有应用窗口之下。
///
/// Dock 与菜单栏都归属桌面层，不能覆盖用户正在使用的窗口。
///
/// 踩过的坑（都不要再用）：
/// - HWND_BOTTOM：会插到壁纸层下面，窗口直接消失
/// - Progman 作锚点：它在图标层之下，插上去后窗口看得见
///   却收不到任何鼠标事件（图标层铺满全屏吃掉了点击）
/// </summary>
internal static class DesktopLayer
{
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>把窗口插到「桌面图标层」之上。失败时保持原 z 序并写日志。</summary>
    public static void PlaceAboveDesktop(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var desktop = FindDesktopHost();
        if (desktop == IntPtr.Zero)
        {
            AppLog.Info("未找到桌面宿主窗口，保持默认 z 序");
            return;
        }

        var moved = SetWindowPos(hwnd, desktop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        AppLog.Info($"place above desktop: {moved} host=0x{desktop.ToInt64():X}");
    }

    /// <summary>
    /// 桌面层最上面的窗口：优先是承载 SHELLDLL_DefView（桌面图标宿主）的
    /// WorkerW；找不到再退化为 Progman 或任意 WorkerW。
    /// </summary>
    private static IntPtr FindDesktopHost()
    {
        var host = IntPtr.Zero;
        var current = IntPtr.Zero;

        while ((current = FindWindowExW(IntPtr.Zero, current, "WorkerW", null)) != IntPtr.Zero)
        {
            if (FindWindowExW(current, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                host = current;
            }
        }

        if (host != IntPtr.Zero)
        {
            return host;
        }

        var progman = FindWindowW("Progman", null);
        if (progman != IntPtr.Zero)
        {
            return progman;
        }

        return FindWindowExW(IntPtr.Zero, IntPtr.Zero, "WorkerW", null);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
