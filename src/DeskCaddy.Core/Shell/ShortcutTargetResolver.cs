using System.Text;

namespace DeskCaddy.Core.Shell;

/// <summary>
/// 从 .lnk 的原始字节里找出所有「本地目标路径」候选。
///
/// 用途：启动失败后判断目标是否真的消失，防止把临时性失败
/// （机械盘休眠、文件被占用、杀软短暂持有句柄）误判为「已卸载」。
///
/// 两条扫描线，互为补充：
/// - UTF-16LE：现代快捷方式（Win10/11）总是带 Unicode 路径
/// - ANSI（本地代码页，中文系统为 GBK）：兼容旧格式
///
/// 不引入 IShellLink COM 互操作——虚表手写崩过进程，字节扫描零风险。
/// 这是启发式解析：只用于「目标是否还存在」的粗判，不用于启动。
/// </summary>
public static class ShortcutTargetResolver
{
    public static IReadOnlyList<string> FindLocalPathCandidates(string lnkPath)
    {
        var results = new List<string>();

        try
        {
            if (!lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(lnkPath))
            {
                return results;
            }

            var bytes = File.ReadAllBytes(lnkPath);

            ScanUtf16Paths(bytes, results);
            ScanAnsiPaths(bytes, results);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 解析失败返回已有候选，调用方按「无法判断」处理。
        }

        return results;
    }

    /// <summary>UTF-16LE 路径：允许中文等多字节字符，遇到控制字符即结束。</summary>
    private static void ScanUtf16Paths(byte[] bytes, List<string> results)
    {
        for (var i = 0; i + 6 <= bytes.Length; i += 2)
        {
            var drive = bytes[i] | (bytes[i + 1] << 8);
            var colon = i + 4 <= bytes.Length ? bytes[i + 2] | (bytes[i + 3] << 8) : 0;
            var slash = i + 6 <= bytes.Length ? bytes[i + 4] | (bytes[i + 5] << 8) : 0;

            var isDriveLetter = drive is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

            if (!isDriveLetter || colon != ':' || slash != '\\')
            {
                continue;
            }

            var sb = new StringBuilder();
            var pos = i;

            while (pos + 1 < bytes.Length)
            {
                var ch = bytes[pos] | (bytes[pos + 1] << 8);

                // 允许 0x20 以上的一切（含中文），只截控制字符
                if (ch < 0x20 || ch == 0x7F)
                {
                    break;
                }

                sb.Append((char)ch);
                pos += 2;
            }

            if (sb.Length >= 4)
            {
                results.Add(sb.ToString());
            }

            i = pos;
        }
    }

    /// <summary>ANSI 路径：旧格式快捷方式用单字节（中文系统为 GBK）存储。</summary>
    private static void ScanAnsiPaths(byte[] bytes, List<string> results)
    {
        var encoding = GetAnsiEncoding();
        if (encoding is null)
        {
            return;
        }

        for (var i = 0; i + 3 < bytes.Length; i++)
        {
            var drive = bytes[i];

            if (!char.IsAsciiLetter((char)drive) || bytes[i + 1] != (byte)':' || bytes[i + 2] != (byte)'\\')
            {
                continue;
            }

            var end = i;

            while (end < bytes.Length && bytes[end] != 0)
            {
                end++;
            }

            if (end - i < 4 || end - i > 520)
            {
                i = Math.Max(end, i + 1);
                continue;
            }

            string candidate;

            try
            {
                candidate = encoding.GetString(bytes, i, end - i);
            }
            catch (Exception)
            {
                break;
            }

            // GBK 解码错位可能产生控制字符，过滤掉这种候选
            if (candidate.Length >= 4 && !candidate.Any(char.IsControl))
            {
                results.Add(candidate);
            }

            i = end;
        }
    }

    /// <summary>本地 ANSI 代码页（中文系统 = GBK）。拿不到就放弃 ANSI 扫描。</summary>
    private static Encoding? GetAnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            return Encoding.GetEncoding(0);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
