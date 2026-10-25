using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DeskCaddy.Rendering;

/// <summary>
/// 截取窗口背后的屏幕区域并做高斯模糊，用来伪造真正的 Acrylic 毛玻璃。
///
/// WPF 的窗口拿不到 DWM 的 Acrylic 材质（已实测证伪，见 docs/HANDOFF.md §3），
/// 所以只能自己截屏。截屏范围会比窗口四周各外扩一圈，模糊后把外扩部分裁掉，
/// 避免边缘出现半透明渐隐。
/// </summary>
internal static class ScreenBackdrop
{
    private const int PaddingPixels = 40;

    /// <summary>
    /// 模糊半径要相对截图尺寸来定。Dock 只有 80~90px 高，半径过大会把整块
    /// 平均成一个灰均值；启动台面板很大，需要更强的模糊才有毛玻璃感。
    /// </summary>
    public const double DockBlurRadius = 12;

    public const double PanelBlurRadius = 38;

    private static BitmapSource? _cachedBackdrop;
    private static (double X, double Y, double W, double H, double Radius, string Wallpaper) _cachedKey;

    /// <summary>
    /// 当前壁纸指纹：注册表里的壁纸路径 + 图片文件修改时间。
    /// 壁纸一变（换图 / 轮播切换 / 原图替换）指纹就变，底图缓存随之失效。
    /// </summary>
    public static string CurrentWallpaperMark()
    {
        try
        {
            var path = Registry.GetValue(
                @"HKEY_CURRENT_USER\Control Panel\Desktop",
                "Wallpaper",
                null) as string ?? string.Empty;

            if (path.Length > 0 && File.Exists(path))
            {
                return path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            }

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 是否为动态壁纸源（Wallpaper Engine 等）。这类壁纸不更新注册表指纹，
    /// 唯一的同步手段是周期性重截。
    /// </summary>
    public static bool IsDynamicWallpaperSource()
    {
        return CurrentWallpaperMark().Contains(
            "wallpaperengine",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 带缓存的截取：面板每次打开位置和尺寸都一样，重复截屏+模糊
    /// 是纯浪费，还会在打开动画里插进一段「窗口透明」的掉帧。
    /// 缓存键包含壁纸指纹；动态壁纸源（Wallpaper Engine）传 forceRefresh 跳过缓存。
    /// </summary>
    public static BitmapSource? CaptureCached(Window window, double blurRadius, bool forceRefresh = false)
    {
        var wallpaper = CurrentWallpaperMark();
        var key = (window.Left, window.Top, window.ActualWidth, window.ActualHeight, blurRadius, wallpaper);

        if (!forceRefresh && _cachedBackdrop is not null && _cachedKey.Equals(key))
        {
            return _cachedBackdrop;
        }

        _cachedBackdrop = Capture(window, blurRadius);
        _cachedKey = key;

        return _cachedBackdrop;
    }

    public static BitmapSource? Capture(Window window, double blurRadius = DockBlurRadius)
    {
        if (window.ActualWidth < 1 || window.ActualHeight < 1)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(window);

        var left = (int)Math.Round(window.Left * dpi.DpiScaleX);
        var top = (int)Math.Round(window.Top * dpi.DpiScaleY);
        var width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var captureLeft = Math.Max(0, left - PaddingPixels);
        var captureTop = Math.Max(0, top - PaddingPixels);
        var captureWidth = width + PaddingPixels * 2;
        var captureHeight = height + PaddingPixels * 2;

        var raw = CaptureRegion(captureLeft, captureTop, captureWidth, captureHeight);
        if (raw is null)
        {
            return null;
        }

        var blurred = Blur(raw, blurRadius);

        // 外扩部分可能被屏幕边界截断，这里按实际偏移裁剪。
        var offsetX = left - captureLeft;
        var offsetY = top - captureTop;

        var cropWidth = Math.Min(width, blurred.PixelWidth - offsetX);
        var cropHeight = Math.Min(height, blurred.PixelHeight - offsetY);

        if (cropWidth <= 0 || cropHeight <= 0)
        {
            return null;
        }

        try
        {
            var cropped = new CroppedBitmap(blurred, new Int32Rect(offsetX, offsetY, cropWidth, cropHeight));
            cropped.Freeze();

            return cropped;
        }
        catch (ArgumentException)
        {
            // 窗口被推到屏幕外、或多屏坐标异常时会走到这里。
            // 没有底图只是退化成半透明纯色，不该让整个刷新失败。
            return null;
        }
    }

    private static BitmapSource Blur(BitmapSource source, double radius)
    {
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }

        visual.Effect = new BlurEffect
        {
            Radius = radius,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Quality
        };

        var target = new RenderTargetBitmap(
            source.PixelWidth,
            source.PixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);

        target.Render(visual);
        target.Freeze();

        return target;
    }

    private static BitmapSource? CaptureRegion(int x, int y, int width, int height)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        var previous = SelectObject(memoryDc, bitmap);

        try
        {
            const int SrcCopy = 0x00CC0020;
            const int CaptureBlt = 0x40000000;

            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SrcCopy | CaptureBlt))
            {
                return null;
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                }
            };

            var pixels = new byte[width * height * 4];
            if (GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref info, 0) == 0)
            {
                return null;
            }

            // 屏幕截图没有有效 alpha，用 Bgr32 避免被当成全透明。
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
            result.Freeze();

            return result;
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public int Colors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr destDc,
        int x,
        int y,
        int width,
        int height,
        IntPtr sourceDc,
        int sourceX,
        int sourceY,
        int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc,
        IntPtr bitmap,
        uint start,
        uint lines,
        [Out] byte[] bits,
        ref BitmapInfo info,
        uint usage);
}
