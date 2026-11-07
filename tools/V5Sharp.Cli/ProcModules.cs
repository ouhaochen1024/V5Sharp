using System.Runtime.InteropServices;
using System.Text;

namespace V5Sharp.Cli;

/// <summary>诊断用：枚举指定进程的模块（区分 32/64 位视图）</summary>
internal static class ProcModules
{
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule,
        uint cb, out uint lpcbNeeded, uint dwFilterFlag);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleBaseNameW(IntPtr hProcess, IntPtr hModule,
        StringBuilder lpBaseName, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    public static int Dump(int pid, int filter)
    {
        IntPtr h = OpenProcess(0x0400 | 0x0010 /*QUERY_INFORMATION|VM_READ*/, false, pid);
        if (h == IntPtr.Zero)
        {
            Console.Error.WriteLine($"OpenProcess 失败: {Marshal.GetLastWin32Error()}");
            return 0;
        }
        try
        {
            var mods = new IntPtr[512];
            bool ok = EnumProcessModulesEx(h, mods, (uint)(mods.Length * IntPtr.Size), out uint needed, (uint)filter);
            Console.WriteLine($"EnumProcessModulesEx(filter=0x{filter:X}) => {ok}, needed={needed}, err={Marshal.GetLastWin32Error()}");
            if (!ok) return 0;

            int count = (int)(needed / (uint)IntPtr.Size);
            var sb = new StringBuilder(260);
            for (int i = 0; i < count && i < mods.Length; i++)
            {
                sb.Clear();
                uint len = GetModuleBaseNameW(h, mods[i], sb, (uint)sb.Capacity);
                Console.WriteLine($"  [{i}] base=0x{mods[i].ToInt64():X} name={(len > 0 ? sb.ToString() : "<fail>")}");
            }
            return count;
        }
        finally { CloseHandle(h); }
    }
}
