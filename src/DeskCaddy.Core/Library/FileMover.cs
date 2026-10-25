using DeskCaddy.Core.Safety;

namespace DeskCaddy.Core.Library;

/// <summary>
/// 同卷用重命名（原子、零拷贝）；跨卷走「复制 → 校验 → 删源」。
/// 任何一步失败都不删除源文件，保证最坏情况下用户仍能在桌面找到原快捷方式。
/// </summary>
internal static class FileMover
{
    public static void Move(string source, string destination)
    {
        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(source)) ?? string.Empty;
        var destinationRoot = Path.GetPathRoot(Path.GetFullPath(destination)) ?? string.Empty;

        var isDirectory = Directory.Exists(source);

        if (string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (isDirectory)
            {
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination);
            }

            return;
        }

        // 跨卷移动文件夹不做递归复制：数据量不可控、逐文件校验复杂，
        // 失败时的回滚成本太高。明确拒绝比静默出错好。
        if (isDirectory)
        {
            throw new IOException("暂不支持跨盘移动文件夹（收纳库与桌面不在同一磁盘）");
        }

        var expectedHash = FileHash.ComputeSha256(source);

        File.Copy(source, destination, overwrite: false);

        var actualHash = FileHash.ComputeSha256(destination);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(destination);
            throw new IOException("跨卷复制后哈希不一致，已回滚目标文件，源文件保留");
        }

        File.Delete(source);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 回滚失败也不抛出，避免掩盖原始错误。
        }
    }
}
