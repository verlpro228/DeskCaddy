namespace DeskCaddy.Core.Library;

/// <summary>
/// 收纳库的物理布局。文件系统是唯一事实来源，state.json 只是索引。
/// </summary>
public static class LibraryPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskCaddy");

    /// <summary>收纳库：.lnk / .url 真实存放的地方。</summary>
    public static string CollectedDirectory { get; } = Path.Combine(Root, "collected");

    public static string StateFile { get; } = Path.Combine(Root, "state.json");

    public static string StateBackupFile { get; } = StateFile + ".bak";

    public static string StateTempFile { get; } = StateFile + ".tmp";

    public static string IconCacheDirectory { get; } = Path.Combine(Root, "cache", "icons");

    public static void EnsureCreated() => Directory.CreateDirectory(CollectedDirectory);
}
