using System.Text;

namespace DeskCaddy.Core.Shell;

/// <summary>
/// 解析「图标真正所在的位置」。
///
/// 三类条目直接对 .lnk / .url 提取图标会拿到通用占位图：
/// 1. 打包应用（UWP / MSIX）：目标在 WindowsApps 下且受 ACL 保护，
///    IShellItemImageFactory 拿到的是空白页。改用 AUMID 走
///    shell:AppsFolder\&lt;AUMID&gt;，Shell 会返回应用的磁贴图标。
/// 2. Internet 快捷方式（.url）：图标在文件里的 IconFile= 字段
///    （浏览器下载的 favicon .ico），直接对该文件提取即可。
/// 3. 普通应用：原样返回。
/// </summary>
public static class AppIconResolver
{
    /// <summary>返回实际应该用来提取图标的路径；解析不出就原样返回。</summary>
    public static string ResolveIconSource(string path)
    {
        try
        {
            if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                var iconFile = ReadUrlIconFile(path);
                if (iconFile is not null)
                {
                    return iconFile;
                }
            }

            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                var aumid = FindAumid(File.ReadAllBytes(path));
                if (aumid is not null)
                {
                    return $"shell:AppsFolder\\{aumid}";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 解析失败不影响主流程，回退到原路径。
        }

        return path;
    }

    /// <summary>从 .url 文本里读 IconFile= 字段，文件存在才返回。</summary>
    private static string? ReadUrlIconFile(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var index = line.IndexOf("IconFile=", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var file = line[(index + "IconFile=".Length)..].Trim();

            if (file.Length > 0 && File.Exists(file))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>
    /// 在 .lnk 的原始字节里找打包应用的 AUMID（UTF-16LE，形如 "Family!App"）。
    /// 不引入 IShellLink 互操作 —— 之前手写 IShellFolder 虚表崩过进程，
    /// 字节扫描虽然朴素但零 COM 风险。
    /// </summary>
    private static string? FindAumid(byte[] bytes)
    {
        for (var i = 0; i + 6 <= bytes.Length; i += 2)
        {
            if (bytes[i] != 'A' || bytes[i + 1] != 0
                || bytes[i + 2] != 'p' || bytes[i + 3] != 0
                || bytes[i + 4] != 'p' || bytes[i + 5] != 0)
            {
                continue;
            }

            // 从 "App" 的结尾向前回溯，直到遇到不可打印字符（IDList 的字符串定界）
            var start = i;

            while (start - 2 >= 0)
            {
                var ch = bytes[start - 2] | (bytes[start - 1] << 8);

                if (ch is < 0x20 or >= 0x7F)
                {
                    break;
                }

                start -= 2;
            }

            var text = Encoding.Unicode.GetString(bytes, start, i + 6 - start);

            if (text.Length is >= 8 and <= 200
                && text.Contains('.')
                && text.Contains('!'))
            {
                return text;
            }
        }

        return null;
    }
}
