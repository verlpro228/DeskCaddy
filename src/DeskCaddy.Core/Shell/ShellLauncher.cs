using System.Runtime.InteropServices;

namespace DeskCaddy.Core.Shell;

public static class ShellLauncher
{
    /// <summary>
    /// 启动快捷方式。
    ///
    /// 关键点：不能给 .lnk / .url 传工作目录。Shell 默认会用快捷方式里记录的
    /// 「起始位置」（Start in），一旦我们用 lpDirectory 覆盖它，像 Typora 这类
    /// Electron 应用会因为找不到相对路径的资源直接崩溃（0xC0000005）。
    /// </summary>
    public static bool TryLaunch(string path) => TryLaunch(path, out _, out _);

    /// <summary>
    /// 启动快捷方式。失败时带出两个错误码：
    /// shellError = ShellExecuteW 的 SE_ERR 返回码（2=文件未找到、5=拒绝访问…）
    /// lastError  = GetLastError（1223 = 用户在 UAC 弹窗里点了取消）。
    /// </summary>
    public static bool TryLaunch(string path, out int shellError, out int lastError)
    {
        shellError = 0;
        lastError = 0;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var workingDirectory = IsShortcutFile(path) ? null : Path.GetDirectoryName(path);

        var result = ShellExecuteW(
            IntPtr.Zero,
            "open",
            path,
            null,
            string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
            SW_SHOWNORMAL);

        var code = result.ToInt64();

        if (code > 32)
        {
            return true;
        }

        shellError = code > 0 && code <= 32 ? (int)code : 0;
        lastError = Marshal.GetLastWin32Error();

        return false;
    }

    private static bool IsShortcutFile(string path)
    {
        var extension = Path.GetExtension(path);

        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    private const int SW_SHOWNORMAL = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ShellExecuteW(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPWStr)] string lpOperation,
        [MarshalAs(UnmanagedType.LPWStr)] string lpFile,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpParameters,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpDirectory,
        int nShowCmd);
}
