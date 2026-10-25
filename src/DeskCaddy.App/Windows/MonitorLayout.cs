using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace DeskCaddy.Windows;

/// <summary>
/// 多屏定位与全屏检测。
///
/// 注意：这里把物理像素直接除以窗口的 DPI 缩放折算成 DIP。多屏不同缩放时会有偏差
/// （WPF 的 DIP 空间本身不支持混合 DPI），单屏或统一缩放下是准确的。
/// </summary>
internal static class MonitorLayout
{
    private const uint MonitorDefaultToNearest = 2;

    private const uint MonitorDefaultToPrimary = 1;

    /// <summary>鼠标所在的那块屏幕。</summary>
    public static IntPtr MonitorUnderCursor()
    {
        GetCursorPos(out var point);

        return MonitorFromPoint(point, MonitorDefaultToNearest);
    }

    /// <summary>
    /// 主屏。Dock 默认放这里 —— 跟随鼠标所在屏会让 Dock「消失」在副屏上，
    /// 用户会以为程序没启动。
    /// </summary>
    public static IntPtr PrimaryMonitor()
    {
        var origin = new Point32 { X = 0, Y = 0 };

        return MonitorFromPoint(origin, MonitorDefaultToPrimary);
    }

    /// <summary>某块屏的工作区（已扣掉任务栏），DIP。</summary>
    public static Rect GetWorkArea(Window window, IntPtr monitor)
    {
        var info = GetMonitor(window, monitor);

        return ToDip(window, info.WorkLeft, info.WorkTop, info.WorkRight, info.WorkBottom);
    }

    /// <summary>某块屏的完整区域，DIP。用于判断窗口是否全屏。</summary>
    public static Rect GetBounds(Window window, IntPtr monitor)
    {
        var info = GetMonitor(window, monitor);

        return ToDip(window, info.Left, info.Top, info.Right, info.Bottom);
    }

    public static IntPtr MonitorOf(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;

        return hwnd == IntPtr.Zero
            ? MonitorUnderCursor()
            : MonitorFromWindow(hwnd, MonitorDefaultToNearest);
    }

    /// <summary>
    /// 前台窗口是否铺满了整块屏幕（全屏视频、游戏、演示）。
    ///
    /// 不能用 GetWindowRect 判断：最大化窗口的窗口矩形比屏幕还大 8px，
    /// 会被误判成全屏，导致 Dock 在普通最大化窗口下就消失。
    /// 改用「客户区」——最大化窗口有标题栏，客户区盖不满整屏。
    /// </summary>
    public static bool IsForegroundFullscreen(Window window)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        // 已最大化 / 已最小化的窗口一律不算全屏。
        // 很多应用（IDE、浏览器）是无边框 + 自定义标题栏，客户区同样铺满整屏，
        // 只靠矩形判断分不出来，必须先用窗口状态排除。
        if (IsZoomed(foreground) || IsIconic(foreground))
        {
            return false;
        }

        if (!GetClientRect(foreground, out var client))
        {
            return false;
        }

        var origin = new Point32 { X = 0, Y = 0 };
        if (!ClientToScreen(foreground, ref origin))
        {
            return false;
        }

        var bounds = GetBounds(window, MonitorFromWindow(foreground, MonitorDefaultToNearest));

        const double Tolerance = 2;

        var clientLeft = (double)origin.X;
        var clientTop = (double)origin.Y;
        var clientRight = clientLeft + client.Right;
        var clientBottom = clientTop + client.Bottom;

        return clientLeft <= bounds.Left + Tolerance
            && clientTop <= bounds.Top + Tolerance
            && clientRight >= bounds.Right - Tolerance
            && clientBottom >= bounds.Bottom - Tolerance;
    }

    private static Rect ToDip(Window window, int left, int top, int right, int bottom)
    {
        var dpi = VisualTreeHelper.GetDpi(window);

        var x = left / dpi.DpiScaleX;
        var y = top / dpi.DpiScaleY;

        return new Rect(x, y, (right - left) / dpi.DpiScaleX, (bottom - top) / dpi.DpiScaleY);
    }

    private static MonitorInfoEx GetMonitor(Window window, IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
        {
            monitor = MonitorUnderCursor();
        }

        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };

        if (!GetMonitorInfoW(monitor, ref info))
        {
            // 拿不到就退回主屏工作区，绝不能让调用方拿到全零坐标。
            var fallback = SystemParameters.WorkArea;

            info.WorkLeft = (int)Math.Round(fallback.Left);
            info.WorkTop = (int)Math.Round(fallback.Top);
            info.WorkRight = (int)Math.Round(fallback.Right);
            info.WorkBottom = (int)Math.Round(fallback.Bottom);
            info.Left = info.WorkLeft;
            info.Top = info.WorkTop;
            info.Right = info.WorkRight;
            info.Bottom = info.WorkBottom;
        }

        return info;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    // 必须声明 Unicode：struct 里有 ByValTStr，默认 Ansi 会让整个结构体尺寸对不上，
    // GetMonitorInfo 直接失败，返回全零工作区，窗口会被推到屏幕外。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int WorkLeft;
        public int WorkTop;
        public int WorkRight;
        public int WorkBottom;
        public int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point32 point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point32 point);
}
