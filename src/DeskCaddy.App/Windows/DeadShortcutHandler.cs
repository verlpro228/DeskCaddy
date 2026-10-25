using System.ComponentModel;
using System.IO;
using System.Windows;
using DeskCaddy.Core.Library;
using DeskCaddy.Core.Shell;
using DeskCaddy.Diagnostics;

namespace DeskCaddy.Windows;

/// <summary>
/// 处理「快捷方式打不开」的情况。
///
/// 移除确认要过五道防线，确保不可能误删：
/// 1. 错误码必须是「文件/路径未找到」——拒绝访问、共享冲突等一律只报错
/// 2. .url 的目标是协议，无法验证，永不提供移除
/// 3. 解析不出目标路径 = 无法判断，不提供移除
/// 4. 目标路径中任何一个存在于磁盘 = 快捷方式有效，不提供移除
/// 5. 用户确认后、落盘前再复查一遍（对话框停留的几秒里磁盘可能已恢复）
///
/// 且删除是「移入隔离区」而非硬删除，万一仍误判可手工找回。
/// </summary>
internal static class DeadShortcutHandler
{
    private const int SeErrFileNotFound = 2;
    private const int SeErrPathNotFound = 3;

    public static async Task<bool> OfferRemovalAsync(string shortcutPath, int shellErrorCode, int lastError)
    {
        var fileName = Path.GetFileName(shortcutPath);
        var displayName = Path.GetFileNameWithoutExtension(shortcutPath);

        // 用户在 UAC / 系统提示里主动取消不是错误，静默忽略。
        // （WeGame 等需要管理员权限的程序，取消 UAC 会报「拒绝访问」）
        if (lastError == 1223)   // ERROR_CANCELLED
        {
            AppLog.Info($"「{displayName}」启动被用户取消（UAC），忽略");
            return false;
        }

        // ── 防线 1：错误码必须是「文件/路径未找到」 ──
        // 拒绝访问、共享冲突、内存不足等失败与「已卸载」无关。
        if (shellErrorCode != SeErrFileNotFound && shellErrorCode != SeErrPathNotFound)
        {
            var hint = shellErrorCode == 5
                ? "该程序可能需要管理员权限运行。"
                : "失败原因与「目标不存在」无关，已跳过移除流程。";

            Warn(displayName, shellErrorCode, hint);
            return false;
        }

        // ── 防线 2：.url 的目标是协议（steam://、http://），无法验证 ──
        if (shortcutPath.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            Warn(displayName, shellErrorCode, "Internet 快捷方式无法自动验证目标，已跳过移除流程。");
            return false;
        }

        // ── 防线 3 + 4：解析目标路径并验证 ──
        var candidates = ShortcutTargetResolver.FindLocalPathCandidates(shortcutPath);

        if (candidates.Count == 0)
        {
            Warn(displayName, shellErrorCode, "无法解析目标路径，不能确认是否失效，已跳过移除流程。");
            return false;
        }

        if (candidates.Any(File.Exists))
        {
            AppLog.Info($"「{displayName}」目标仍存在（候选 {candidates.Count} 个），不提供移除（错误 {shellErrorCode}）");

            Warn(displayName, shellErrorCode, "目标程序仍在，可能是暂时性错误（磁盘唤醒、文件占用），请稍后重试。");
            return false;
        }

        AppLog.Info($"「{displayName}」候选目标全部缺失：{string.Join(" | ", candidates)}");

        var choice = MessageBox.Show(
            $"「{displayName}」的目标程序可能已被卸载或移动。\n\n" +
            $"是否将这个失效的快捷方式从收纳库中移除？\n" +
            $"（快捷方式会被移入隔离区，不影响任何程序文件）",
            "DeskCaddy",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (choice != MessageBoxResult.Yes)
        {
            return false;
        }

        // ── 防线 5：落盘前再复查一遍 ──
        // 用户看对话框的几秒里，磁盘可能已唤醒、文件可能已恢复。
        candidates = ShortcutTargetResolver.FindLocalPathCandidates(shortcutPath);

        if (candidates.Any(File.Exists))
        {
            AppLog.Info("确认移除前目标已恢复存在，取消移除");

            MessageBox.Show(
                $"检测到「{displayName}」的目标已恢复可用，本次不移除。",
                "DeskCaddy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return false;
        }

        var result = await Task.Run(() =>
            CollectionService.CreateDefault().Remove(fileName, apply: true));

        if (!result.Success)
        {
            AppLog.Error($"移除失效快捷方式失败：{result.Message}");
            return false;
        }

        AppLog.Info(result.Message);
        LibraryEvents.RaiseChanged();

        return true;
    }

    private static void Warn(string displayName, int shellErrorCode, string reason)
    {
        MessageBox.Show(
            $"「{displayName}」启动失败。\n\n{DescribeError(shellErrorCode)}\n\n{reason}",
            "DeskCaddy",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static string DescribeError(int shellErrorCode)
    {
        try
        {
            return $"系统错误 {shellErrorCode}：{new Win32Exception(shellErrorCode).Message}";
        }
        catch (Exception)
        {
            return $"系统错误 {shellErrorCode}。";
        }
    }
}
