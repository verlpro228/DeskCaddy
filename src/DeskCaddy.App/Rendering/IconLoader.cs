using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskCaddy.Core.Shell;

namespace DeskCaddy.Rendering;

internal static class IconLoader
{
    /// <summary>
    /// 图标缓存：文件名 → (最后写入时间, 图标)。
    /// 必须 static——Dock 和菜单栏是两个窗口实例，实例级缓存会让菜单栏
    /// 每次打开都重新提取几十个 Shell 图标（每个 30~80ms），
    /// 表现为「点菜单按钮后卡一秒才出面板」。
    /// </summary>
    private static readonly Dictionary<string, (long Stamp, BitmapSource? Icon)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object CacheLock = new();

    /// <summary>
    /// 带缓存的提取：文件没被改过就直接复用上次结果。
    /// 线程安全；可在后台线程调用（结果已 Freeze，可跨线程交给 UI）。
    /// </summary>
    public static BitmapSource? LoadCached(string path, string fileName, int size)
    {
        long stamp;

        try
        {
            stamp = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (IOException)
        {
            return Load(path, size);
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(fileName, out var cached) && cached.Stamp == stamp)
            {
                return cached.Icon;
            }
        }

        var icon = Load(path, size);

        lock (CacheLock)
        {
            Cache[fileName] = (stamp, icon);
        }

        return icon;
    }

    /// <summary>
    /// 在后台线程调用：结果已 Freeze，可跨线程交给 UI 使用。
    /// </summary>
    public static BitmapSource? Load(string path, int size)
    {
        // 打包应用 / .url 的图标不在 .lnk 文件本身，先解析出真正的来源；
        // 解析失败或提取失败都回退到原路径，行为与修复前一致。
        var iconSource = AppIconResolver.ResolveIconSource(path);

        var image = ShellIconExtractor.TryExtract(iconSource, size)
            ?? ShellIconExtractor.TryExtract(path, size);

        if (image is null)
        {
            return null;
        }

        var source = BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            // Shell 返回的是直通 alpha（实测：白边像素为 R255 G255 B255 A48，
            // 预乘格式下颜色不会大于 alpha）。按 Bgra32 解释才是对的，
            // 否则半透明边缘会被当成预乘值渲染成白色发光。
            PixelFormats.Bgra32,
            null,
            image.Pixels,
            image.Width * 4);

        source.Freeze();
        DumpIcon(source, Path.GetFileNameWithoutExtension(path));
        return source;
    }

    /// <summary>诊断用：把提取到的图标写到 logs/icons 下。确认图标质量问题后可删。</summary>
    private static void DumpIcon(BitmapSource source, string name)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskCaddy",
                "logs",
                "icons");

            Directory.CreateDirectory(directory);

            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var stream = File.Create(Path.Combine(directory, $"{name}.png"));
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
