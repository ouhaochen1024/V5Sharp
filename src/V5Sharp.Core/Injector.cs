using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace V5Sharp.Core;

/// <summary>
/// 注入器：把 V5HookNN.dll 载入目标进程（x86 / x64 通吃，且不需要额外的
/// 32 位辅助进程）。
///
/// 原理：
///   - 同位数目标：直接 CreateRemoteThread(kernel32!LoadLibraryW)。
///   - 32 位目标 + 64 位宿主：远程线程的入口必须落在目标的 32 位地址空间，
///     因此先枚举目标里的 32 位模块拿到 kernel32 基址，再叠加从
///     SysWOW64\kernel32.dll 读出的 LoadLibraryW RVA，得到合法的 32 位地址。
///     这一步正是老版本"只能多开 32 位"的根因：原版选择"整体使用 32 位宿主"，
///     于是无法进入 64 位目标；这里改为"按目标位数选注入模块 + 换算地址"。
/// </summary>
public static class Injector
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint LIST_MODULES_32BIT = 0x01;
    private const uint LIST_MODULES_ALL = 0x03;

    /// <summary>
    /// SysWOW64\kernel32.dll 里 LoadLibraryW 的 RVA。这个值在一个系统上恒定，
    /// 而解析它要把 2MB 多的文件整个读进内存 —— 以前每启动一个 32 位实例就做一遍，
    /// 改成整个进程只算一次。
    /// </summary>
    private static readonly Lazy<uint> LoadLibraryRva32 = new(() =>
    {
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return PeExports.GetExportRva(Path.Combine(windir, "SysWOW64", "kernel32.dll"), "LoadLibraryW");
    });

    /// <summary>目标是否为 32 位（WOW64）。</summary>
    public static bool IsTarget32Bit(int pid)
    {
        IntPtr h = Win32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return !Environment.Is64BitProcess;
        try
        {
            if (Win32.IsWow64Process(h, out bool wow64))
                return !Environment.Is64BitProcess || wow64;
        }
        finally { Win32.CloseHandle(h); }
        return !Environment.Is64BitProcess;
    }

    /// <summary>注入并返回远端模块句柄（失败抛异常）。</summary>
    public static IntPtr Inject(int pid, string dllPath)
    {
        IntPtr hProc = Win32.OpenProcess(
            Win32.PROCESS_CREATE_THREAD | Win32.PROCESS_QUERY_INFORMATION |
            Win32.PROCESS_VM_OPERATION | Win32.PROCESS_VM_WRITE | Win32.PROCESS_VM_READ,
            false, pid);
        if (hProc == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess 失败（目标可能以更高权限运行）");

        IntPtr remote = IntPtr.Zero;
        IntPtr hThread = IntPtr.Zero;
        try
        {
            bool target32 = IsWow64(hProc);

            byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            remote = Win32.VirtualAllocEx(hProc, IntPtr.Zero, (nuint)pathBytes.Length,
                                          Win32.MEM_COMMIT | Win32.MEM_RESERVE, Win32.PAGE_READWRITE);
            if (remote == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx 失败");

            if (!Win32.WriteProcessMemory(hProc, remote, pathBytes, (nuint)pathBytes.Length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory 失败");

            IntPtr start = ResolveRemoteLoadLibrary(hProc, target32, out string diag);
            if (start == IntPtr.Zero)
                throw new InvalidOperationException($"无法解析目标进程内的 LoadLibraryW 地址 (target32={target32}, {diag})");

            hThread = Win32.CreateRemoteThread(hProc, IntPtr.Zero, 0, start, remote, 0, out _);
            if (hThread == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread 失败");

            if (Win32.WaitForSingleObject(hThread, 15000) != 0)
                throw new TimeoutException("等待远程 LoadLibrary 超时");

            Win32.GetExitCodeThread(hThread, out IntPtr hmod);
            if (hmod == IntPtr.Zero)
                throw new InvalidOperationException("目标进程 LoadLibrary 返回 NULL（路径或位数不匹配）");
            return hmod;
        }
        finally
        {
            if (hThread != IntPtr.Zero) Win32.CloseHandle(hThread);
            if (remote != IntPtr.Zero) Win32.VirtualFreeEx(hProc, remote, 0, Win32.MEM_RELEASE);
            Win32.CloseHandle(hProc);
        }
    }

    private static bool IsWow64(IntPtr hProc)
    {
        if (!Environment.Is64BitProcess) return false;   // 32 位宿主只可能处理 32 位目标
        return Win32.IsWow64Process(hProc, out bool wow64) && wow64;
    }

    private static IntPtr ResolveRemoteLoadLibrary(IntPtr hProc, bool target32, out string diag)
    {
        diag = "";
        if (!target32)
        {
            IntPtr kernel32 = Win32.GetModuleHandleW("kernel32.dll");
            diag = $"kernel32=0x{kernel32.ToInt64():X}";
            return Win32.GetProcAddress(kernel32, "LoadLibraryW");
        }

        // 32 位目标：从 SysWOW64 读取 RVA（进程内缓存一次），再叠加目标内 32 位 kernel32 基址
        uint rva = LoadLibraryRva32.Value;
        IntPtr base32 = FindModuleBase(hProc, "kernel32.dll", LIST_MODULES_32BIT, out diag);
        if (base32 == IntPtr.Zero) return IntPtr.Zero;
        diag = $"base32=0x{base32.ToInt64():X} rva=0x{rva:X}";
        return (IntPtr)((long)base32 + rva);
    }

    private static IntPtr FindModuleBase(IntPtr hProc, string moduleName, uint filter, out string diag)
    {
        var mods = new IntPtr[1024];
        if (!EnumProcessModulesEx(hProc, mods, (uint)(mods.Length * IntPtr.Size), out uint needed, filter))
        {
            diag = $"EnumProcessModulesEx 失败 err={Marshal.GetLastWin32Error()}";
            return IntPtr.Zero;
        }
        diag = $"模块数={needed / (uint)IntPtr.Size}";

        int count = (int)(needed / (uint)IntPtr.Size);
        var sb = new StringBuilder(260);
        for (int i = 0; i < count && i < mods.Length; i++)
        {
            sb.Clear();
            if (GetModuleBaseNameW(hProc, mods[i], sb, (uint)sb.Capacity) == 0) continue;
            if (string.Equals(sb.ToString(), moduleName, StringComparison.OrdinalIgnoreCase))
                return mods[i];
        }
        return IntPtr.Zero;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule,
        uint cb, out uint lpcbNeeded, uint dwFilterFlag);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleBaseNameW(IntPtr hProcess, IntPtr hModule,
        StringBuilder lpBaseName, uint nSize);
}
