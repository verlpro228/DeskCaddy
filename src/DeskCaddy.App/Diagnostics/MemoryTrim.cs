using System.Runtime.InteropServices;

namespace DeskCaddy.Diagnostics;

/// <summary>
/// 主动把工作集修剪到最小。
///
/// WPF 的渲染缓存、字体与 JIT 代码在 GC 之后仍以物理页形式驻留，
/// 任务管理器里的「内存占用」会一直停在高位。面板关闭这类「用户暂时
/// 不再交互」的时机调用一次，Windows 会把这些页挪进待备列表，
/// 显示占用立降；下次打开面板时页从待备列表软调回，几乎无感。
/// </summary>
internal static class MemoryTrim
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr process,
        IntPtr minimumWorkingSetSize,
        IntPtr maximumWorkingSetSize);

    public static void Trim()
    {
        // 先跑一遍 GC，让位图等带终结器的原生资源真正释放，再修剪工作集。
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
    }
}
