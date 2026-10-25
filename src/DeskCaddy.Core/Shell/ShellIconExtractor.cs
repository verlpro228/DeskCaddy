using System.Runtime.InteropServices;

namespace DeskCaddy.Core.Shell;

/// <summary>
/// 通过 IShellItemImageFactory 获取高清图标（256px 级别），比 SHGetFileInfo 的
/// 32x32 清晰得多，且能正确处理 UWP/打包应用与自定义图标。
/// </summary>
public static class ShellIconExtractor
{
    public static IconImage? TryExtract(string path, int size = 256)
    {
        // shell: 开头的是虚拟路径（如 shell:AppsFolder\<AUMID>），没有对应文件，
        // 但 SHCreateItemFromParsingName 能正常解析。文件夹也支持（取文件夹图标）。
        var isVirtual = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
        var exists = File.Exists(path) || Directory.Exists(path);

        if (string.IsNullOrWhiteSpace(path) || (!isVirtual && !exists))
        {
            return null;
        }

        IntPtr hBitmap = IntPtr.Zero;
        object? factoryObject = null;

        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var punk);
            if (hr != 0 || punk == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                factoryObject = Marshal.GetObjectForIUnknown(punk);
                if (factoryObject is not IShellItemImageFactory factory)
                {
                    return null;
                }

                hr = factory.GetImage(
                    new NativeSize(size, size),
                    SIIGBF.ICONONLY | SIIGBF.BIGGERSIZEOK,
                    out hBitmap);

                if (hr != 0 || hBitmap == IntPtr.Zero)
                {
                    return null;
                }

                var bitmap = ReadBitmap(hBitmap);

                return bitmap is null ? null : IconTrimmer.Trim(bitmap);
            }
            finally
            {
                Marshal.Release(punk);
            }
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }

            if (factoryObject is not null && Marshal.IsComObject(factoryObject))
            {
                Marshal.ReleaseComObject(factoryObject);
            }
        }
    }

    private static IconImage? ReadBitmap(IntPtr hBitmap)
    {
        var bitmap = new BITMAP();
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), ref bitmap) == 0)
        {
            return null;
        }

        var width = bitmap.bmWidth;
        var height = bitmap.bmHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var stride = width * 4;
        var pixels = new byte[stride * height];

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // 负数 = 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            }
        };

        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var lines = GetDIBits(hdc, hBitmap, 0, (uint)height, pixels, ref info, 0);
            if (lines == 0)
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        // 部分资源图标不带 alpha 通道（全 0），直接当透明会导致整张图消失。
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        return new IconImage(width, height, pixels);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, SIIGBF flags, out IntPtr phbm);
    }

    [Flags]
    private enum SIIGBF
    {
        RESIZETOFIT = 0x00,
        BIGGERSIZEOK = 0x01,
        ICONONLY = 0x04
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public NativeSize(int cx, int cy)
        {
            this.cx = cx;
            this.cy = cy;
        }

        public int cx;

        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint start,
        uint cLines,
        [Out] byte[] lpvBits,
        ref BITMAPINFO lpbmi,
        uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
