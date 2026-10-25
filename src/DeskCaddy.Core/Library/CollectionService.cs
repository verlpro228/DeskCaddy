using DeskCaddy.Core.Safety;

namespace DeskCaddy.Core.Library;

/// <summary>
/// 收纳引擎。所有写操作都必须带 apply 参数：
/// apply=false 时只计算计划并返回，绝不触碰磁盘（预演模式）。
/// </summary>
public sealed class CollectionService
{
    private const long SortStep = 1024;

    private readonly StateStore _store;
    private readonly string _desktopDirectory;
    private readonly string _collectedDirectory;
    private CollectionState _state;

    public CollectionService(StateStore store, string desktopDirectory, string collectedDirectory)
    {
        _store = store;
        _desktopDirectory = Path.GetFullPath(desktopDirectory);
        _collectedDirectory = Path.GetFullPath(collectedDirectory);

        _state = _store.Load(out var warning);
        LoadWarning = warning;
    }

    public static CollectionService CreateDefault()
    {
        LibraryPaths.EnsureCreated();

        return new CollectionService(
            StateStore.Default,
            KnownFolders.Desktop,
            LibraryPaths.CollectedDirectory);
    }

    public string DesktopDirectory => _desktopDirectory;

    public string CollectedDirectory => _collectedDirectory;

    public string? LoadWarning { get; }

    public IReadOnlyList<CollectedItem> Items => _state.Items;

    public ScanReport Scan() => DesktopScanner.Scan(_desktopDirectory);

    /// <summary>
    /// 收纳一个桌面项。默认只认快捷方式；拖入模式（allowRealItems）下
    /// 也接受真实文件与文件夹，但重解析点 / 系统属性 / 保留名防线不变。
    /// </summary>
    public OperationResult Collect(string sourcePath, bool apply, bool allowRealItems = false)
    {
        var guard = ShortcutGuard.Inspect(sourcePath, _desktopDirectory, allowRealItems);
        if (!guard.Allowed)
        {
            return OperationResult.Fail($"拒绝收纳：{guard.Reason}");
        }

        var fileName = Path.GetFileName(sourcePath);

        if (FindItem(fileName) is not null)
        {
            return OperationResult.Fail($"收纳库中已存在同名条目：{fileName}");
        }

        var targetName = MakeUniqueName(_collectedDirectory, fileName);
        var targetPath = Path.Combine(_collectedDirectory, targetName);

        if (!apply)
        {
            return OperationResult.Ok($"[预演] {sourcePath}  →  {targetPath}", targetPath);
        }

        try
        {
            FileMover.Move(sourcePath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Fail($"移动失败，源文件保留在原位：{ex.Message}");
        }

        var journalWarning = Journal(sourcePath, targetPath);

        _state.Items.Add(new CollectedItem
        {
            FileName = targetName,
            OriginDirectory = Path.GetDirectoryName(sourcePath) ?? _desktopDirectory,
            CollectedAt = DateTimeOffset.Now,
            Sort = NextSort()
        });

        Save();

        return OperationResult.Ok($"已收纳 {fileName} → {targetName}{journalWarning}", targetPath);
    }

    /// <summary>把收纳库中的一条还原回原目录（不存在则回桌面）。</summary>
    public OperationResult Restore(string fileName, bool apply)
    {
        var item = FindItem(fileName);
        if (item is null)
        {
            return OperationResult.Fail($"收纳库中没有该条目：{fileName}");
        }

        var sourcePath = Path.Combine(_collectedDirectory, item.FileName);
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            return OperationResult.Fail($"收纳库文件缺失：{sourcePath}（可尝试 reconcile 修正索引）");
        }

        var targetDirectory = ResolveRestoreDirectory(item);
        var targetName = MakeUniqueName(targetDirectory, item.FileName);
        var targetPath = Path.Combine(targetDirectory, targetName);

        if (!apply)
        {
            return OperationResult.Ok($"[预演] {sourcePath}  →  {targetPath}", targetPath);
        }

        try
        {
            FileMover.Move(sourcePath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Fail($"还原失败，收纳库文件保留：{ex.Message}");
        }

        var journalWarning = Journal(sourcePath, targetPath);

        _state.Items.Remove(item);
        Save();

        return OperationResult.Ok($"已还原到 {targetPath}{journalWarning}", targetPath);
    }

    public BatchResult RestoreAll(bool apply)
    {
        var results = new List<OperationResult>();

        // 复制一份快照：Restore 会修改 _state.Items
        foreach (var item in _state.Items.ToList())
        {
            results.Add(Restore(item.FileName, apply));
        }

        return new BatchResult(results);
    }

    /// <summary>
    /// 让索引与收纳库目录对齐：多出的文件补条目，缺失的条目删除。
    /// 索引损坏时靠这个从零重建。
    /// </summary>
    public ReconcileResult Reconcile(bool apply)
    {
        Directory.CreateDirectory(_collectedDirectory);

        // 文件只认快捷方式（手动放进来的其他文件不算），文件夹全部纳入——
        // 否则拖入的文件夹会被 Reconcile 当成「索引缺失」清掉。
        var files = Directory.EnumerateFiles(_collectedDirectory)
            .Where(IsLibraryFile)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Concat(Directory.EnumerateDirectories(_collectedDirectory)
                .Select(Path.GetFileName)
                .OfType<string>())
            .ToList();

        var added = files
            .Where(file => FindItem(file) is null)
            .ToList();

        var removed = _state.Items
            .Where(item => !files.Contains(item.FileName, StringComparer.OrdinalIgnoreCase))
            .Select(item => item.FileName)
            .ToList();

        if (!apply || (added.Count == 0 && removed.Count == 0))
        {
            return new ReconcileResult(added, removed);
        }

        foreach (var fileName in added)
        {
            _state.Items.Add(new CollectedItem
            {
                FileName = fileName,
                OriginDirectory = _desktopDirectory,
                CollectedAt = DateTimeOffset.Now,
                Sort = NextSort()
            });
        }

        foreach (var fileName in removed)
        {
            _state.Items.RemoveAll(i => string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        }

        Save();

        return new ReconcileResult(added, removed);
    }

    private static bool IsLibraryFile(string path)
    {
        var extension = Path.GetExtension(path);

        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    private CollectedItem? FindItem(string fileName) =>
        _state.Items.FirstOrDefault(i => string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    private string ResolveRestoreDirectory(CollectedItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.OriginDirectory) && Directory.Exists(item.OriginDirectory))
        {
            return item.OriginDirectory;
        }

        return _desktopDirectory;
    }

    /// <summary>
    /// 从收纳库中删除某个条目（连 .lnk 一起删）。
    /// 仅用于目标程序已卸载、快捷方式彻底失效的场景。
    /// </summary>
    public OperationResult Remove(string fileName, bool apply)
    {
        var item = _state.Items.FirstOrDefault(i =>
            string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            return OperationResult.Fail($"收纳库中没有 {fileName}");
        }

        var path = Path.Combine(_collectedDirectory, item.FileName);

        if (!File.Exists(path))
        {
            return OperationResult.Fail($"文件不存在：{path}");
        }

        if (apply)
        {
            // 不做硬删除：移入隔离区。万一仍然误判，用户可以手工找回。
            var quarantine = Path.Combine(LibraryPaths.Root, "quarantine");
            Directory.CreateDirectory(quarantine);

            var quarantined = Path.Combine(quarantine, item.FileName);

            if (File.Exists(quarantined))
            {
                quarantined = Path.Combine(
                    quarantine,
                    $"{Path.GetFileNameWithoutExtension(item.FileName)}_{DateTime.Now:HHmmss}{Path.GetExtension(item.FileName)}");
            }

            File.Move(path, quarantined);

            _state.Items.Remove(item);
            _store.Save(_state);
        }

        return OperationResult.Ok($"已移除 {fileName}（移入隔离区，可手工找回）");
    }

    /// <summary>
    /// 设置某个条目是否显示在 Dock 上。只改索引，不动任何文件。
    /// </summary>
    public OperationResult SetPinned(string fileName, bool pinned)
    {
        var item = _state.Items.FirstOrDefault(i =>
            string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            return OperationResult.Fail($"收纳库中没有 {fileName}");
        }

        if (item.Pinned == pinned)
        {
            return OperationResult.Ok($"{fileName} 状态未变");
        }

        item.Pinned = pinned;
        _store.Save(_state);

        return OperationResult.Ok($"{fileName} {(pinned ? "已加入" : "已移出")} Dock");
    }

    /// <summary>
    /// 把条目移到指定位置（从 0 开始）。只改索引顺序，不动任何文件。
    /// 重排全部 Sort 值，避免间隔耗尽。
    /// </summary>
    public OperationResult Reorder(string fileName, int newIndex)
    {
        var ordered = _state.Items.OrderBy(i => i.Sort).ToList();
        var item = ordered.FirstOrDefault(i =>
            string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            return OperationResult.Fail($"收纳库中没有 {fileName}");
        }

        ordered.Remove(item);

        var index = Math.Clamp(newIndex, 0, ordered.Count);
        ordered.Insert(index, item);

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Sort = (i + 1) * SortStep;
        }

        _state.Items.Clear();
        _state.Items.AddRange(ordered);
        _store.Save(_state);

        return OperationResult.Ok($"{fileName} → 第 {index + 1} 位");
    }

    private long NextSort() =>
        _state.Items.Count == 0 ? SortStep : _state.Items.Max(i => i.Sort) + SortStep;

    private static string MakeUniqueName(string directory, string fileName)
    {
        var candidate = fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var index = 2;

        while (File.Exists(Path.Combine(directory, candidate)) || Directory.Exists(Path.Combine(directory, candidate)))
        {
            candidate = $"{stem} ({index}){extension}";
            index++;
        }

        return candidate;
    }

    private static string Journal(string source, string destination)
    {
        try
        {
            var isDirectory = Directory.Exists(destination);

            MoveJournal.Append(new MoveRecord(
                source,
                destination,
                isDirectory ? string.Empty : FileHash.ComputeSha256(destination),
                isDirectory ? 0 : new FileInfo(destination).Length,
                DateTimeOffset.Now));

            return string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 流水账失败不阻塞操作，但要明确告知：还原将退化为「回桌面」。
            return $"（警告：操作日志写入失败，还原时会退回桌面：{ex.Message}）";
        }
    }

    private void Save() => _store.Save(_state);
}
