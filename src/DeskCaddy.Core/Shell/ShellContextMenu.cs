using System.Runtime.InteropServices;

namespace DeskCaddy.Core.Shell;

/// <summary>
/// 弹出资源管理器同款右键菜单。
///
/// 走 IShellItem → BindToHandler(BHID_SFUIObject) → IContextMenu，
/// 比老的 SHParseDisplayName + IShellFolder.GetUIObjectOf 路线短得多：
/// IShellItem 只有 5 个方法，不需要手工维护 IShellFolder 那 10 个方法的虚表顺序，
/// 也不会因为参数封送写错而直接触发访问违规把进程打死。
///
/// 菜单内容完全由 Shell 生成，第三方扩展（7-Zip、TortoiseGit 等）自动包含。
/// </summary>
public static class ShellContextMenu
{
    private const uint CmfNormal = 0x00000000;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmTopAlign = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint CmicMaskUnicode = 0x00004000;

    // BHID_SFUIObject：向 IShellItem 索取「用于 UI 的对象」，右键菜单从这里拿。
    // 注意是 ...E225，不是 ...E224 —— 后者是 BHID_SFObject，返回 IDataObject，
    // 拿 IContextMenu 会得到 E_NOTIMPL。
    private static readonly Guid BhidSfuiObject = new("3981E225-F559-11D3-8E3A-00C04F6837D5");

    public static bool Show(IntPtr ownerWindow, string path, out string? error)
    {
        error = null;

        IntPtr contextMenuPtr = IntPtr.Zero;
        IntPtr hMenu = IntPtr.Zero;

        try
        {
            var shellItemIid = typeof(IShellItem).GUID;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemIid, out var shellItemPtr);

            if (hr != 0 || shellItemPtr == IntPtr.Zero)
            {
                error = $"SHCreateItemFromParsingName 失败（0x{hr:X8}）";
                return false;
            }

            IContextMenu contextMenu;

            try
            {
                var shellItem = (IShellItem)Marshal.GetObjectForIUnknown(shellItemPtr);

                var contextMenuIid = typeof(IContextMenu).GUID;
                var bhid = BhidSfuiObject;

                hr = shellItem.BindToHandler(IntPtr.Zero, ref bhid, ref contextMenuIid, out contextMenuPtr);
                if (hr != 0 || contextMenuPtr == IntPtr.Zero)
                {
                    error = $"BindToHandler 失败（0x{hr:X8}）";
                    return false;
                }

                contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);
            }
            finally
            {
                if (shellItemPtr != IntPtr.Zero)
                {
                    Marshal.Release(shellItemPtr);
                }
            }

            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero)
            {
                error = "CreatePopupMenu 失败";
                return false;
            }

            // 命令 ID 从 1 开始，TrackPopupMenu 返回的 id 减去 1 就是 Shell 的命令偏移。
            hr = contextMenu.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CmfNormal);
            if (hr < 0)
            {
                error = $"QueryContextMenu 失败（0x{hr:X8}）";
                return false;
            }

            GetCursorPos(out var cursor);

            var command = TrackPopupMenuEx(
                hMenu,
                TpmLeftAlign | TpmTopAlign | TpmRightButton | TpmReturnCmd,
                cursor.X,
                cursor.Y,
                ownerWindow,
                IntPtr.Zero);

            if (command == 0)
            {
                // 用户点了别处或按 Esc，属于正常取消。
                return true;
            }

            var invoke = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CmicMaskUnicode,
                hwnd = ownerWindow,
                // 动词传「命令偏移」而不是字符串：MAKEINTRESOURCE 形式，
                // 这样第三方扩展的自定义命令也能正确执行。
                lpVerb = command - 1,
                lpVerbW = command - 1,
                nShow = 1
            };

            hr = contextMenu.InvokeCommand(ref invoke);
            if (hr != 0)
            {
                error = $"InvokeCommand 失败（0x{hr:X8}）";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or EntryPointNotFoundException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (hMenu != IntPtr.Zero)
            {
                DestroyMenu(hMenu);
            }

            if (contextMenuPtr != IntPtr.Zero)
            {
                Marshal.Release(contextMenuPtr);
            }
        }
    }

    /// <summary>
    /// 诊断用：完整走一遍 COM 调用但不弹出菜单。
    /// 用来验证封送是否正确 —— 直接上真菜单时若结构体写错会崩掉整个进程。
    /// </summary>
    public static bool Probe(string path, out string detail)
    {
        IntPtr contextMenuPtr = IntPtr.Zero;
        IntPtr hMenu = IntPtr.Zero;

        try
        {
            var shellItemIid = typeof(IShellItem).GUID;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemIid, out var shellItemPtr);

            if (hr != 0 || shellItemPtr == IntPtr.Zero)
            {
                detail = $"SHCreateItemFromParsingName 失败（0x{hr:X8}）";
                return false;
            }

            try
            {
                var shellItem = (IShellItem)Marshal.GetObjectForIUnknown(shellItemPtr);

                var contextMenuIid = typeof(IContextMenu).GUID;
                var bhid = BhidSfuiObject;

                hr = shellItem.BindToHandler(IntPtr.Zero, ref bhid, ref contextMenuIid, out contextMenuPtr);
                if (hr != 0 || contextMenuPtr == IntPtr.Zero)
                {
                    detail = $"BindToHandler 失败（0x{hr:X8}）";
                    return false;
                }

                var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);

                hMenu = CreatePopupMenu();
                var result = contextMenu.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CmfNormal);

                detail = $"QueryContextMenu hr=0x{result:X8}，命令数 {result & 0xFFFF}";

                return result >= 0;
            }
            finally
            {
                Marshal.Release(shellItemPtr);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or EntryPointNotFoundException)
        {
            detail = ex.Message;
            return false;
        }
        finally
        {
            if (hMenu != IntPtr.Zero)
            {
                DestroyMenu(hMenu);
            }

            if (contextMenuPtr != IntPtr.Zero)
            {
                Marshal.Release(contextMenuPtr);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public Point32 ptInvoke;
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetParent(out IntPtr shellItem);

        [PreserveSig]
        int GetDisplayName(uint sigdnName, out IntPtr ppszName);

        [PreserveSig]
        int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        [PreserveSig]
        int Compare(IntPtr shellItem, uint hint, out int order);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IntPtr shellItem);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(
        IntPtr hMenu,
        uint fuFlags,
        int x,
        int y,
        IntPtr hwnd,
        IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);
}
