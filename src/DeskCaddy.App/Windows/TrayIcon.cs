using System.IO;
using System.Runtime.InteropServices;

namespace DeskCaddy.Windows;

/// <summary>
/// 托盘图标。直接调 Shell_NotifyIcon，不引入 WinForms —— 后者会额外加载一整套程序集。
/// 消息回调走宿主窗口的 WndProc。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public const int CallbackMessage = 0x8000 + 1; // WM_APP + 1

    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NifShowTip = 0x00000080;

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;

    private readonly IntPtr _hwnd;
    private readonly int _id = 1;

    private IntPtr _icon;
    private bool _installed;

    public TrayIcon(IntPtr hwnd, string tooltip)
    {
        _hwnd = hwnd;
        _icon = LoadApplicationIcon();

        Write(NimAdd, tooltip);
        _installed = true;
    }

    public void Dispose()
    {
        if (_installed)
        {
            var data = CreateData(string.Empty);
            Shell_NotifyIconW(NimDelete, ref data);
            _installed = false;
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    private void Write(int command, string tooltip)
    {
        var data = CreateData(tooltip);
        Shell_NotifyIconW(command, ref data);
    }

    private NotifyIconData CreateData(string tooltip) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _hwnd,
        Id = _id,
        Flags = NifMessage | NifIcon | NifTip | NifShowTip,
        CallbackMessage = CallbackMessage,
        IconHandle = _icon,
        Tip = tooltip.Length > 127 ? tooltip[..127] : tooltip
    };

    private static IntPtr LoadApplicationIcon()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
        {
            return IntPtr.Zero;
        }

        try
        {
            // 取 exe 内嵌的第一组图标；小图标更适合托盘。
            var count = ExtractIconExW(executable, 0, out var large, out var small, 1);
            if (count == 0)
            {
                return IntPtr.Zero;
            }

            if (small != IntPtr.Zero)
            {
                if (large != IntPtr.Zero)
                {
                    DestroyIcon(large);
                }

                return small;
            }

            return large;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr WindowHandle;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public int State;
        public int StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public int TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public int InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, int count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
