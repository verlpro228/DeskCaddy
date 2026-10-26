using DeskCaddy.Core.Library;
using DeskCaddy.Core.Safety;

namespace DeskCaddy.Cli;

/// <summary>
/// P1 验收集：在临时目录里造假桌面，跑「全收纳 → 全还原」，
/// 逐项比对哈希。全程不接触真实桌面。
/// </summary>
internal static class SelfTest
{
    public static int Run(int shortcutCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskCaddy.SelfTest", DateTime.Now.ToString("HHmmssfff"));
        var desktop = Path.Combine(root, "Desktop");
        var collected = Path.Combine(root, "collected");
        var stateFile = Path.Combine(root, "state.json");

        Directory.CreateDirectory(desktop);
        Directory.CreateDirectory(collected);

        MoveJournal.OverrideDirectory = Path.Combine(root, "logs");

        Console.WriteLine($"隔离测试目录：{root}");
        Console.WriteLine();

        var expectedHashes = CreateFixtures(desktop, shortcutCount);
        var service = new CollectionService(new StateStore(stateFile), desktop, collected);

        var failures = new List<string>();

        // ---------- 阶段 1：扫描 ----------
        var report = service.Scan();
        Console.WriteLine($"[扫描] 可收纳 {report.Collectable.Count}，跳过 {report.Skipped.Count}");

        Expect(report.Collectable.Count == shortcutCount,
            $"可收纳数量应为 {shortcutCount}，实际 {report.Collectable.Count}", failures);

        foreach (var skipped in report.Skipped)
        {
            Console.WriteLine($"       跳过 {skipped.FileName} —— {skipped.Reason}");
        }

        // ---------- 阶段 2：全部收纳 ----------
        var started = DateTime.UtcNow;
        var collectResults = report.Collectable
            .Select(e => service.Collect(e.Path, apply: true))
            .ToList();

        var collectFailed = collectResults.Count(r => !r.Success);
        Console.WriteLine($"[收纳] 成功 {collectResults.Count - collectFailed}，失败 {collectFailed}，耗时 {(DateTime.UtcNow - started).TotalMilliseconds:F0}ms");

        Expect(collectFailed == 0, $"收纳失败 {collectFailed} 项", failures);
        Expect(service.Items.Count == shortcutCount,
            $"索引条目应为 {shortcutCount}，实际 {service.Items.Count}", failures);
        Expect(Directory.GetFiles(collected).Length == shortcutCount,
            $"收纳库文件数应为 {shortcutCount}，实际 {Directory.GetFiles(collected).Length}", failures);

        foreach (var (name, hash) in expectedHashes)
        {
            var target = Path.Combine(collected, name);
            if (!File.Exists(target))
            {
                failures.Add($"收纳后缺失：{name}");
                continue;
            }

            if (!string.Equals(FileHash.ComputeSha256(target), hash, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"收纳后哈希不一致：{name}");
            }
        }

        // 干扰项必须原封不动留在桌面
        var leftovers = Directory.GetFileSystemEntries(desktop).Select(Path.GetFileName).ToList();
        foreach (var mustStay in new[] { "real-file.docx", "setup.exe", "SomeFolder", "desktop.ini", "system-attr.lnk" })
        {
            Expect(leftovers.Contains(mustStay), $"干扰项被误动：{mustStay}", failures);
        }

        Console.WriteLine($"[校验] 桌面剩余 {leftovers.Count} 项（应全为干扰项）：{string.Join("、", leftovers)}");

        // ---------- 阶段 3：全部还原 ----------
        var restoreStarted = DateTime.UtcNow;
        var restoreResults = service.RestoreAll(apply: true);
        Console.WriteLine($"[还原] 成功 {restoreResults.Succeeded}，失败 {restoreResults.Failed}，耗时 {(DateTime.UtcNow - restoreStarted).TotalMilliseconds:F0}ms");

        Expect(restoreResults.Failed == 0, $"还原失败 {restoreResults.Failed} 项", failures);
        Expect(service.Items.Count == 0, $"还原后索引应为空，实际 {service.Items.Count}", failures);
        Expect(Directory.GetFiles(collected).Length == 0,
            $"还原后收纳库应为空，实际 {Directory.GetFiles(collected).Length}", failures);

        foreach (var (name, hash) in expectedHashes)
        {
            var target = Path.Combine(desktop, name);
            if (!File.Exists(target))
            {
                failures.Add($"还原后缺失：{name}");
                continue;
            }

            if (!string.Equals(FileHash.ComputeSha256(target), hash, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"还原后哈希不一致：{name}");
            }
        }

        // ---------- 阶段 4：索引重建 ----------
        // 用全新的状态文件模拟「索引丢失」，避免与上一阶段残留句柄纠缠。
        var orphanStateFile = Path.Combine(root, "state-orphan.json");
        File.WriteAllText(Path.Combine(collected, "orphan.lnk"), "orphan");

        // 拖入模式允许收纳真实文件，Reconcile 必须把它们一起恢复，
        // 否则文件会留在收纳库目录却在 Dock / 菜单栏里消失。
        File.WriteAllText(Path.Combine(collected, "orphan-report.docx"), "real file");
        Directory.CreateDirectory(Path.Combine(collected, "orphan-folder"));

        var rebuilt = new CollectionService(new StateStore(orphanStateFile), desktop, collected);
        var reconcile = rebuilt.Reconcile(apply: true);

        Console.WriteLine($"[重建] 补入 {reconcile.Added.Count}，移除 {reconcile.Removed.Count}");
        Expect(reconcile.Added.Count == 3 && rebuilt.Items.Count == 3,
            "索引重建未能从目录恢复全部条目（快捷方式 / 普通文件 / 文件夹）", failures);

        // ---------- 结论 ----------
        Console.WriteLine();

        if (failures.Count == 0)
        {
            Console.WriteLine($"全部通过：{shortcutCount} 个快捷方式收纳→还原后哈希一致，干扰项零误伤。");
            return 0;
        }

        Console.WriteLine($"失败 {failures.Count} 项：");
        foreach (var failure in failures.Take(20))
        {
            Console.WriteLine($"  ! {failure}");
        }

        return 2;
    }

    private static Dictionary<string, string> CreateFixtures(string desktop, int shortcutCount)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < shortcutCount; i++)
        {
            var path = Path.Combine(desktop, $"App-{i:D3}.lnk");
            File.WriteAllText(path, $"shortcut-payload-{i}-{Guid.NewGuid():N}");
            hashes[Path.GetFileName(path)] = FileHash.ComputeSha256(path);
        }

        // 干扰项：引擎绝不允许碰它们
        File.WriteAllText(Path.Combine(desktop, "real-file.docx"), "docx content");
        File.WriteAllText(Path.Combine(desktop, "setup.exe"), "MZ fake");
        Directory.CreateDirectory(Path.Combine(desktop, "SomeFolder"));

        var ini = Path.Combine(desktop, "desktop.ini");
        File.WriteAllText(ini, "[.ShellClassInfo]");
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);

        var systemLink = Path.Combine(desktop, "system-attr.lnk");
        File.WriteAllText(systemLink, "system attributed");
        File.SetAttributes(systemLink, FileAttributes.System);

        Console.WriteLine($"[准备] 生成 {shortcutCount} 个 .lnk + 4 个干扰项");
        Console.WriteLine();

        return hashes;
    }

    private static void Expect(bool condition, string message, List<string> failures)
    {
        if (!condition)
        {
            failures.Add(message);
        }
    }
}
