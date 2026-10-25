using System.Runtime.InteropServices;

namespace DeskCaddy.Core.Safety;

/// <summary>
/// 用 KnownFolder API 解析桌面路径，而不是拼 %USERPROFILE%\Desktop。
/// 桌面可能被 OneDrive 重定向或用户改到其它盘，硬编码路径会直接改错地方。
/// </summary>
public static class KnownFolders
{
    private static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    private static string? _desktop;

    public static string Desktop => _desktop ??= ResolveDesktop();

    private static string ResolveDesktop()
    {
        try
        {
            var id = FolderIdDesktop;
            var hr = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var buffer);

            if (hr == 0 && buffer != IntPtr.Zero)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(buffer);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        return Path.GetFullPath(path);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(buffer);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
