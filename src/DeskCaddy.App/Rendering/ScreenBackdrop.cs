using System.Diagnostics;
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
    /// 截屏缩放比例。底图只用来做模糊，不需要原始分辨率；半分辨率能把
    /// 每次截屏的像素缓冲区与位图内存直接降到四分之一（实测每次打开面板
    /// 会因此少占用约 8MB），模糊本身也快一倍。
    /// </summary>
    private const double CaptureScale = 0.5;

    /// <summary>两次强制重截之间的最小间隔（动态壁纸源）。</summary>
    private static readonly TimeSpan ForceRefreshMinInterval = TimeSpan.FromSeconds(5);

    private static DateTime _lastCaptureUtc = DateTime.MinValue;

    /// <summary>
    /// 模糊半径要相对截图尺寸来定。Dock 只有 80~90px 高，半径过大会把整块
    /// 平均成一个灰均值；启动台面板很大，需要更强的模糊才有毛玻璃感。
    /// </summary>
    public const double DockBlurRadius = 12;

    public const double PanelBlurRadius = 38;

    private static BitmapSource? _cachedBackdrop;
    private static (double X, double Y, double W, double H, double Radius, string Wallpaper) _cachedKey;

    /// <summary>
    /// 当前壁纸指纹：注册表里的壁纸路径 + 图片修改时间，再加系统转码壁纸的修改时间。
    ///
    /// 只用注册表路径不够：轮播 / Windows 聚焦 / 部分第三方壁纸工具会复用同一个
    /// 文件（Wallpaper Engine 就是固定的 BackupWallpaper），路径和时间都不变。
    /// TranscodedWallpaper 是 Windows 真正拿去绘制的转码副本，静态壁纸一换它必更新。
    /// </summary>
    public static string CurrentWallpaperMark()
    {
        try
        {
            var path = Registry.GetValue(
                @"HKEY_CURRENT_USER\Control Panel\Desktop",
                "Wallpaper",
                null) as string ?? string.Empty;

            var mark = path;

            if (path.Length > 0 && File.Exists(path))
            {
                mark += "|" + File.GetLastWriteTimeUtc(path).Ticks;
            }

            mark += "|" + TranscodeMark();

            // Wallpaper Engine 换壁纸既不更新注册表、也不更新转码文件，
            // 但它会重写安装目录下的 config.json，用这个当信号最可靠。
            mark += "|" + WallpaperEngineConfigMark();

            return mark;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string? _wallpaperEngineConfigPath;
    private static DateTime _wallpaperEngineConfigCheckedUtc = DateTime.MinValue;

    /// <summary>
    /// Wallpaper Engine 的配置文件指纹。进程没在跑就返回 "-"，
    /// 解析进程路径本身也有开销，最多 30 秒查找一次。
    /// </summary>
    private static string WallpaperEngineConfigMark()
    {
        var path = ResolveWallpaperEngineConfig();
        if (path is null)
        {
            return "-";
        }

        var info = new FileInfo(path);

        if (!info.Exists)
        {
            return "-";
        }

        return $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
    }

    private static string? ResolveWallpaperEngineConfig()
    {
        if (_wallpaperEngineConfigPath is not null && File.Exists(_wallpaperEngineConfigPath))
        {
            return _wallpaperEngineConfigPath;
        }

        if ((DateTime.UtcNow - _wallpaperEngineConfigCheckedUtc).TotalSeconds < 30)
        {
            return _wallpaperEngineConfigPath;
        }

        _wallpaperEngineConfigCheckedUtc = DateTime.UtcNow;

        foreach (var processName in new[] { "wallpaper64", "wallpaper32" })
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        var executable = process.MainModule?.FileName;
                        if (string.IsNullOrEmpty(executable))
                        {
                            continue;
                        }

                        var directory = Path.GetDirectoryName(executable);
                        if (string.IsNullOrEmpty(directory))
                        {
                            continue;
                        }

                        var config = Path.Combine(directory, "config.json");

                        if (File.Exists(config))
                        {
                            _wallpaperEngineConfigPath = config;
                            return config;
                        }
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                    {
                        // 拿不到进程路径就算了，继续用转码文件指纹。
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }

        _wallpaperEngineConfigPath = null;
        return null;
    }

    private static string TranscodeMark()
    {
        var transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");

        if (!File.Exists(transcoded))
        {
            return "-";
        }

        var info = new FileInfo(transcoded);

        return $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
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

        // 动态壁纸源每打开一次都强制重截会在连续开关面板时堆出一串
        // 几 MB 的位图。5 秒内的重复请求直接复用缓存。
        var tooSoon = (DateTime.UtcNow - _lastCaptureUtc) < ForceRefreshMinInterval;

        if ((!forceRefresh || tooSoon) && _cachedBackdrop is not null && _cachedKey.Equals(key))
        {
            return _cachedBackdrop;
        }

        _cachedBackdrop = Capture(window, blurRadius);
        _cachedKey = key;
        _lastCaptureUtc = DateTime.UtcNow;

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

        var scaledWidth = Math.Max(1, (int)Math.Round(captureWidth * CaptureScale));
        var scaledHeight = Math.Max(1, (int)Math.Round(captureHeight * CaptureScale));

        var raw = CaptureRegion(
            captureLeft,
            captureTop,
            captureWidth,
            captureHeight,
            scaledWidth,
            scaledHeight);

        if (raw is null)
        {
            return null;
        }

        // 模糊半径与裁剪坐标都要跟着缩放比例走，否则半分辨率下会糊过头、
        // 裁剪也会偏离几十像素。
        var scaleX = (double)raw.PixelWidth / captureWidth;
        var scaleY = (double)raw.PixelHeight / captureHeight;

        var blurred = Blur(raw, blurRadius * scaleX);

        // 外扩部分可能被屏幕边界截断，这里按实际偏移裁剪。
        var offsetX = (int)Math.Round((left - captureLeft) * scaleX);
        var offsetY = (int)Math.Round((top - captureTop) * scaleY);

        var cropWidth = Math.Min((int)Math.Round(width * scaleX), blurred.PixelWidth - offsetX);
        var cropHeight = Math.Min((int)Math.Round(height * scaleY), blurred.PixelHeight - offsetY);

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

    private static BitmapSource? CaptureRegion(
        int x,
        int y,
        int width,
        int height,
        int targetWidth,
        int targetHeight)
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

        var bitmap = CreateCompatibleBitmap(screenDc, targetWidth, targetHeight);
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
            const int Halftone = 4;

            // 直接缩放到目标尺寸，避免先分配整屏原始位图再缩小。
            SetStretchBltMode(memoryDc, Halftone);

            if (!StretchBlt(
                memoryDc,
                0,
                0,
                targetWidth,
                targetHeight,
                screenDc,
                x,
                y,
                width,
                height,
                SrcCopy | CaptureBlt))
            {
                return null;
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = targetWidth,
                    Height = -targetHeight,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                }
            };

            var pixels = new byte[targetWidth * targetHeight * 4];
            if (GetDIBits(memoryDc, bitmap, 0, (uint)targetHeight, pixels, ref info, 0) == 0)
            {
                return null;
            }

            // 屏幕截图没有有效 alpha，用 Bgr32 避免被当成全透明。
            var result = BitmapSource.Create(
                targetWidth,
                targetHeight,
                96,
                96,
                PixelFormats.Bgr32,
                null,
                pixels,
                targetWidth * 4);

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
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(
        IntPtr destDc,
        int destX,
        int destY,
        int destWidth,
        int destHeight,
        IntPtr sourceDc,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
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
