namespace DeskCaddy.Core.Safety;

public enum GuardStatus
{
    Allowed,
    EmptyPath,
    InvalidPath,
    NotFound,
    NotAFile,
    ReparsePoint,
    SystemFile,
    DisallowedExtension,
    ReservedName,
    OutsideDesktop
}

public sealed record GuardResult(bool Allowed, GuardStatus Status, string Reason)
{
    public static GuardResult Ok() => new(true, GuardStatus.Allowed, string.Empty);

    public static GuardResult Reject(GuardStatus status, string reason) => new(false, status, reason);
}

/// <summary>
/// 唯一的安全闸门：任何要进入收纳区的文件，都必须先过这里。
/// 规则刻意写死且保守 —— 宁可拒绝，也不猜。
/// </summary>
public static class ShortcutGuard
{
    /// <summary>允许被收纳的扩展名。除此之外一律拒绝，不做任何推断。</summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk",
        ".url"
    };

    /// <summary>桌面目录下永远不碰的文件名。</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini",
        "thumbs.db",
        "iconcache.db"
    };

    /// <summary>
    /// 校验一个候选桌面项是否允许被收纳。
    /// </summary>
    /// <param name="candidate">桌面上的项。</param>
    /// <param name="desktopDirectory">用户桌面目录（来自 KnownFolders.Desktop）。</param>
    /// <param name="allowRealItems">
    /// 是否放行真实文件与文件夹（拖入收纳模式）。默认 false——
    /// 扫描器 / collect-all 仍然只认快捷方式，避免批量扫走整个桌面。
    /// 放行后，重解析点、系统属性、保留名、桌面归属这些防线全部保留。
    /// </param>
    public static GuardResult Inspect(string candidate, string desktopDirectory, bool allowRealItems = false)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return GuardResult.Reject(GuardStatus.EmptyPath, "路径为空");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GuardResult.Reject(GuardStatus.InvalidPath, "路径非法");
        }

        var isDirectory = Directory.Exists(fullPath);

        if (!isDirectory && !File.Exists(fullPath))
        {
            return GuardResult.Reject(GuardStatus.NotFound, "文件不存在或不是文件");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GuardResult.Reject(GuardStatus.InvalidPath, "无法读取文件属性");
        }

        if ((attributes & FileAttributes.Directory) != 0 && !allowRealItems)
        {
            return GuardResult.Reject(GuardStatus.NotAFile, "是目录，不是快捷方式");
        }

        // 符号链接 / 目录联接：可能指向任意位置（包括系统目录），一律拒绝。
        // 对文件夹同样生效——目录联接（junction）也是 ReparsePoint。
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return GuardResult.Reject(GuardStatus.ReparsePoint, "是重解析点（符号链接/联接），拒绝处理");
        }

        // desktop.ini 等系统文件 / 系统属性文件夹。
        if ((attributes & FileAttributes.System) != 0)
        {
            return GuardResult.Reject(GuardStatus.SystemFile, "系统文件，拒绝处理");
        }

        var fileName = Path.GetFileName(fullPath);
        if (ReservedNames.Contains(fileName))
        {
            return GuardResult.Reject(GuardStatus.ReservedName, "保留文件名，拒绝处理");
        }

        // 白名单：默认只认 .lnk / .url。拖入模式放行真实文件，
        // 但上面的重解析点 / 系统属性 / 保留名 / 桌面归属防线不变。
        if (!allowRealItems)
        {
            var extension = Path.GetExtension(fullPath);
            if (!AllowedExtensions.Contains(extension))
            {
                return GuardResult.Reject(
                    GuardStatus.DisallowedExtension,
                    $"只允许收纳快捷方式（{string.Join(" / ", AllowedExtensions)}），当前为 \"{extension}\"");
            }
        }

        // 必须直接位于桌面目录下，防止传入子目录或被劫持的路径。
        string desktop;
        try
        {
            desktop = Path.GetFullPath(desktopDirectory).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return GuardResult.Reject(GuardStatus.InvalidPath, "桌面路径非法");
        }

        var parent = Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(parent, desktop, StringComparison.OrdinalIgnoreCase))
        {
            return GuardResult.Reject(GuardStatus.OutsideDesktop, "不在用户桌面目录下，拒绝处理");
        }

        return GuardResult.Ok();
    }
}
