using System.Text;
using DeskCaddy.Core.Library;

namespace DeskCaddy.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        TrySetUtf8();

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var apply = args.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var positional = args
            .Skip(1)
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .ToList();

        try
        {
            // selftest 完全在临时目录里跑，不读写真实桌面与收纳库。
            if (command == "selftest")
            {
                var count = positional.Count > 0 && int.TryParse(positional[0], out var parsed) ? parsed : 100;
                return SelfTest.Run(count);
            }

            var service = CollectionService.CreateDefault();

            if (service.LoadWarning is not null)
            {
                Console.WriteLine($"注意：{service.LoadWarning}");
                Console.WriteLine();
            }

            return command switch
            {
                "scan" => Scan(service),
                "status" => Status(service),
                "collect" => Collect(service, positional, apply),
                "collect-all" => CollectAll(service, apply),
                "restore" => Restore(service, positional, apply),
                "restore-all" => RestoreAll(service, apply),
                "reconcile" => Reconcile(service, apply),
                "groups" => Groups(service),
                "group" => SetGroup(service, positional),
                "ungroup" => Ungroup(service, positional),
                "group-create" => CreateGroup(service, positional),
                "group-delete" => DeleteGroup(service, positional),
                "group-rename" => RenameGroup(service, positional),
                "ctxmenu" => CtxMenuProbe(positional),
                "icon" => IconProbe(positional),
                "target" => TargetProbe(positional),
                _ => Unknown(command)
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"执行失败：{ex.Message}");
            return 3;
        }
    }

    private static int Scan(CollectionService service)
    {
        var report = service.Scan();

        Console.WriteLine($"桌面目录：{report.DesktopDirectory}");
        Console.WriteLine($"收纳库  ：{service.CollectedDirectory}（现有 {service.Items.Count} 条）");
        Console.WriteLine();

        Console.WriteLine($"可收纳 {report.Collectable.Count} 个：");
        foreach (var entry in report.Collectable)
        {
            Console.WriteLine($"  + {entry.FileName}");
        }

        Console.WriteLine();
        Console.WriteLine($"跳过 {report.Skipped.Count} 个：");

        foreach (var group in report.Skipped.GroupBy(e => e.Reason).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  [{group.Key}] {group.Count()} 项");
        }

        Console.WriteLine();
        Console.WriteLine("提示：以上仅为只读扫描，未修改任何文件。");

        return 0;
    }

    private static int Status(CollectionService service)
    {
        Console.WriteLine($"桌面目录：{service.DesktopDirectory}");
        Console.WriteLine($"收纳库  ：{service.CollectedDirectory}");
        Console.WriteLine($"条目数  ：{service.Items.Count}");

        foreach (var item in service.Items.OrderBy(i => i.Sort))
        {
            Console.WriteLine($"  · {item.FileName}  来源={item.OriginDirectory}  收纳于 {item.CollectedAt:yyyy-MM-dd HH:mm}");
        }

        return 0;
    }

    private static int Collect(CollectionService service, IReadOnlyList<string> positional, bool apply)
    {
        if (positional.Count == 0)
        {
            Console.WriteLine("用法：collect <文件路径> [--apply]");
            return 1;
        }

        // 单条收纳是显式指定路径，放行真实文件与文件夹
        var result = service.Collect(positional[0], apply, allowRealItems: true);
        Console.WriteLine(result.Message);

        if (!apply && result.Success)
        {
            Console.WriteLine();
            Console.WriteLine("这是预演，未改动磁盘。加 --apply 才会真正移动。");
        }

        return result.Success ? 0 : 2;
    }

    /// <summary>诊断：验证原生右键菜单的 COM 调用链，不弹菜单。</summary>
    private static int CtxMenuProbe(IReadOnlyList<string> positional)
    {
        if (positional.Count == 0)
        {
            Console.WriteLine("用法：ctxmenu <文件路径>");
            return 1;
        }

        var ok = DeskCaddy.Core.Shell.ShellContextMenu.Probe(positional[0], out var detail);
        Console.WriteLine($"{(ok ? "OK" : "FAIL")}  {detail}");

        return ok ? 0 : 2;
    }

    /// <summary>诊断：打印解析出的图标来源与提取结果。</summary>
    private static int IconProbe(IReadOnlyList<string> positional)
    {
        if (positional.Count == 0)
        {
            Console.WriteLine("用法：icon <快捷方式路径>");
            return 1;
        }

        var source = DeskCaddy.Core.Shell.AppIconResolver.ResolveIconSource(positional[0]);
        Console.WriteLine($"来源：{source}");

        var image = DeskCaddy.Core.Shell.ShellIconExtractor.TryExtract(source, 96);

        if (image is null)
        {
            Console.WriteLine("提取：失败（将回退为空白图标）");
            return 2;
        }

        Console.WriteLine($"提取：成功 {image.Width}x{image.Height}");
        return 0;
    }

    /// <summary>诊断：打印从 .lnk 里解析出的目标路径候选及存在性。</summary>
    private static int TargetProbe(IReadOnlyList<string> positional)
    {
        if (positional.Count == 0)
        {
            Console.WriteLine("用法：target <快捷方式路径>");
            return 1;
        }

        var candidates = DeskCaddy.Core.Shell.ShortcutTargetResolver.FindLocalPathCandidates(positional[0]);

        Console.WriteLine($"候选 {candidates.Count} 个：");

        foreach (var candidate in candidates)
        {
            Console.WriteLine($"  [{(File.Exists(candidate) ? "存在" : "缺失")}] {candidate}");
        }

        return 0;
    }

    private static int CollectAll(CollectionService service, bool apply)
    {
        var collectable = service.Scan().Collectable;
        if (collectable.Count == 0)
        {
            Console.WriteLine("桌面没有可收纳的快捷方式。");
            return 0;
        }

        var results = new List<OperationResult>();

        foreach (var entry in collectable)
        {
            var result = service.Collect(entry.Path, apply);
            results.Add(result);

            if (!result.Success || !apply)
            {
                Console.WriteLine(result.Message);
            }
        }

        if (apply)
        {
            foreach (var item in results.Where(r => r.Success).Take(5))
            {
                Console.WriteLine(item.Message);
            }

            if (results.Count(r => r.Success) > 5)
            {
                Console.WriteLine($"…… 其余 {results.Count(r => r.Success) - 5} 项已收纳");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"成功 {results.Count(r => r.Success)}，失败 {results.Count(r => !r.Success)}");

        foreach (var failure in results.Where(r => !r.Success))
        {
            Console.WriteLine($"  ! {failure.Message}");
        }

        if (!apply)
        {
            Console.WriteLine("这是预演，未改动磁盘。加 --apply 才会真正移动。");
        }

        return results.Any(r => !r.Success) ? 2 : 0;
    }

    private static int Restore(CollectionService service, IReadOnlyList<string> positional, bool apply)
    {
        if (positional.Count == 0)
        {
            Console.WriteLine("用法：restore <收纳库中的文件名> [--apply]");
            return 1;
        }

        var result = service.Restore(positional[0], apply);
        Console.WriteLine(result.Message);

        if (!apply && result.Success)
        {
            Console.WriteLine();
            Console.WriteLine("这是预演，未改动磁盘。加 --apply 才会真正移动。");
        }

        return result.Success ? 0 : 2;
    }

    private static int RestoreAll(CollectionService service, bool apply)
    {
        var result = service.RestoreAll(apply);

        foreach (var item in result.Results)
        {
            Console.WriteLine(item.Message);
        }

        Console.WriteLine();
        Console.WriteLine($"成功 {result.Succeeded}，失败 {result.Failed}");

        if (!apply && result.Results.Count > 0)
        {
            Console.WriteLine("这是预演，未改动磁盘。加 --apply 才会真正移动。");
        }

        return result.Failed == 0 ? 0 : 2;
    }

    private static int Reconcile(CollectionService service, bool apply)
    {
        var result = service.Reconcile(apply);

        if (!result.HasChanges)
        {
            Console.WriteLine("索引与收纳库一致，无需处理。");
            return 0;
        }

        foreach (var fileName in result.Added)
        {
            Console.WriteLine($"  + 补入索引：{fileName}");
        }

        foreach (var fileName in result.Removed)
        {
            Console.WriteLine($"  - 移除索引：{fileName}（文件已不存在）");
        }

        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("这是预演，未改动磁盘。加 --apply 才会写入索引。");
        }

        return 0;
    }

    private static int Groups(CollectionService service)
    {
        Console.WriteLine($"分组数：{service.Groups.Count}");

        foreach (var group in service.Groups)
        {
            var count = service.Items.Count(i =>
                string.Equals(i.Group, group, StringComparison.OrdinalIgnoreCase));

            Console.WriteLine($"  · {group}（{count} 项）");
        }

        var ungrouped = service.Items.Count(i => string.IsNullOrWhiteSpace(i.Group));
        Console.WriteLine($"未分组：{ungrouped} 项");

        return 0;
    }

    private static int SetGroup(CollectionService service, IReadOnlyList<string> positional)
    {
        if (positional.Count < 2)
        {
            Console.WriteLine("用法：group <收纳库文件名> <分组名>");
            return 1;
        }

        var result = service.SetGroup(positional[0], positional[1]);
        Console.WriteLine(result.Message);

        return result.Success ? 0 : 2;
    }

    private static int Ungroup(CollectionService service, IReadOnlyList<string> positional)
    {
        if (positional.Count == 0)
        {
            Console.WriteLine("用法：ungroup <收纳库文件名>");
            return 1;
        }

        var result = service.SetGroup(positional[0], null);
        Console.WriteLine(result.Message);

        return result.Success ? 0 : 2;
    }

    private static int CreateGroup(CollectionService service, IReadOnlyList<string> positional)
    {
        if (positional.Count == 0)
        {
            Console.WriteLine("用法：group-create <分组名>");
            return 1;
        }

        var result = service.CreateGroup(positional[0]);
        Console.WriteLine(result.Message);

        return result.Success ? 0 : 2;
    }

    private static int DeleteGroup(CollectionService service, IReadOnlyList<string> positional)
    {
        if (positional.Count == 0)
        {
            Console.WriteLine("用法：group-delete <分组名>");
            return 1;
        }

        var result = service.DeleteGroup(positional[0]);
        Console.WriteLine(result.Message);

        return result.Success ? 0 : 2;
    }

    private static int RenameGroup(CollectionService service, IReadOnlyList<string> positional)
    {
        if (positional.Count < 2)
        {
            Console.WriteLine("用法：group-rename <旧分组名> <新分组名>");
            return 1;
        }

        var result = service.RenameGroup(positional[0], positional[1]);
        Console.WriteLine(result.Message);

        return result.Success ? 0 : 2;
    }

    private static int Unknown(string command)
    {
        Console.WriteLine($"未知命令：{command}");
        Console.WriteLine();
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("DeskCaddy 收纳引擎（命令行验证工具）");
        Console.WriteLine();
        Console.WriteLine("  scan                     只读扫描桌面，列出可收纳与跳过的项及原因");
        Console.WriteLine("  status                   查看收纳库现有条目");
        Console.WriteLine("  collect <路径> [--apply]  收纳单个快捷方式（默认预演）");
        Console.WriteLine("  collect-all [--apply]     收纳桌面上全部可收纳项（默认预演）");
        Console.WriteLine("  restore <文件名> [--apply] 还原单个条目（默认预演）");
        Console.WriteLine("  restore-all [--apply]     还原全部（默认预演）");
        Console.WriteLine("  reconcile [--apply]       对齐索引与收纳库目录（默认预演）");
        Console.WriteLine("  groups                    列出菜单栏分组与各分组条目数");
        Console.WriteLine("  group <文件名> <分组名>   把条目移动到分组（分组不存在会自动创建）");
        Console.WriteLine("  ungroup <文件名>          把条目移出分组");
        Console.WriteLine("  group-create <分组名>     新建空分组");
        Console.WriteLine("  group-delete <分组名>     删除分组，组内条目回到未分组");
        Console.WriteLine("  group-rename <旧> <新>    重命名分组");
        Console.WriteLine("  selftest [数量]           在临时目录跑「全收纳 → 全还原」验收（默认 100 个）");
        Console.WriteLine();
        Console.WriteLine("所有写操作默认都是预演，必须显式加 --apply。");
    }

    private static void TrySetUtf8()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
        }
    }
}
