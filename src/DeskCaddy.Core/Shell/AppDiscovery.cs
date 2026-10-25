namespace DeskCaddy.Core.Shell;

/// <summary>
/// P0 阶段的图标来源：扫描开始菜单里的 .lnk。
/// P1 会替换为桌面收纳库 + 真实收纳引擎，这里只是让 Dock 有真实数据可画。
/// </summary>
public static class AppDiscovery
{
    private static readonly string[] PreferredNames =
    [
        "Visual Studio Code",
        "Google Chrome",
        "Microsoft Edge",
        "WeChat",
        "微信",
        "QQ",
        "Steam",
        "Notepad++",
        "Windows Terminal",
        "PowerShell",
        "计算器",
        "Calculator",
        "记事本",
        "Notepad"
    ];

    private static readonly string[] NoiseKeywords =
    [
        "uninstall", "卸载", "readme", "帮助", "help", "文档", "documentation",
        "license", "许可", "website", "官网", "更新", "update"
    ];

    public static IReadOnlyList<string> FindStartMenuShortcuts(int max)
    {
        var candidates = new List<string>();

        foreach (var root in EnumerateRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    if (!IsNoise(Path.GetFileNameWithoutExtension(file)))
                    {
                        candidates.Add(file);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        var unique = candidates
            .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var ordered = new List<string>();

        foreach (var name in PreferredNames)
        {
            var hit = unique.FirstOrDefault(p =>
                Path.GetFileNameWithoutExtension(p).Contains(name, StringComparison.OrdinalIgnoreCase));

            if (hit is not null && !ordered.Contains(hit, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(hit);
            }
        }

        foreach (var path in unique.OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCulture))
        {
            if (ordered.Count >= max)
            {
                break;
            }

            if (!ordered.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(path);
            }
        }

        return ordered.Take(max).ToList();
    }

    private static IEnumerable<string> EnumerateRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    }

    private static bool IsNoise(string name) =>
        NoiseKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
}
