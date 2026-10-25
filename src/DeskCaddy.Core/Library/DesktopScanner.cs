using DeskCaddy.Core.Safety;

namespace DeskCaddy.Core.Library;

public enum ScanKind
{
    Collectable,
    Directory,
    Ignored
}

public sealed record ScanEntry(string Path, string FileName, ScanKind Kind, string Reason);

public sealed class ScanReport
{
    public ScanReport(IReadOnlyList<ScanEntry> entries, string desktopDirectory)
    {
        Entries = entries;
        DesktopDirectory = desktopDirectory;
    }

    public string DesktopDirectory { get; }

    public IReadOnlyList<ScanEntry> Entries { get; }

    public IReadOnlyList<ScanEntry> Collectable =>
        Entries.Where(e => e.Kind == ScanKind.Collectable).ToList();

    public IReadOnlyList<ScanEntry> Skipped =>
        Entries.Where(e => e.Kind != ScanKind.Collectable).ToList();
}

/// <summary>
/// 只读扫描桌面，逐项给出结论与原因。绝不修改任何文件。
/// </summary>
public static class DesktopScanner
{
    public static ScanReport Scan(string desktopDirectory)
    {
        var entries = new List<ScanEntry>();

        if (!Directory.Exists(desktopDirectory))
        {
            return new ScanReport(entries, desktopDirectory);
        }

        foreach (var directory in Directory.EnumerateDirectories(desktopDirectory))
        {
            entries.Add(new ScanEntry(directory, Path.GetFileName(directory), ScanKind.Directory, "文件夹，不处理"));
        }

        foreach (var file in Directory.EnumerateFiles(desktopDirectory))
        {
            var result = ShortcutGuard.Inspect(file, desktopDirectory);

            entries.Add(result.Allowed
                ? new ScanEntry(file, Path.GetFileName(file), ScanKind.Collectable, "可收纳")
                : new ScanEntry(file, Path.GetFileName(file), ScanKind.Ignored, result.Reason));
        }

        return new ScanReport(entries, desktopDirectory);
    }
}
