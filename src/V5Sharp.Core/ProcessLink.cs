using System.Runtime.InteropServices;
using System.Text;

namespace V5Sharp.Core;

/// <summary>一条进程快照记录</summary>
public readonly record struct ProcEntry(int Pid, int ParentPid, string Name);

/// <summary>
/// 进程树工具：快照枚举、句柄打开、镜像路径。
///
/// 用于"启动器拉起客户端后自己退出"的场景：入口进程消失时，
/// 靠父 PID 关系把真正在跑的客户端认回来。
/// </summary>
public static class ProcTree
{
    public static List<ProcEntry> Snapshot()
    {
        var list = new List<ProcEntry>(256);
        IntPtr snap = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero) return list;

        try
        {
            var e = new Win32.PROCESSENTRY32W { dwSize = Marshal.SizeOf<Win32.PROCESSENTRY32W>() };
            if (!Win32.Process32FirstW(snap, ref e)) return list;

            do
            {
                list.Add(new ProcEntry(e.th32ProcessID, e.th32ParentProcessID, e.szExeFile ?? ""));
                e.dwSize = Marshal.SizeOf<Win32.PROCESSENTRY32W>();
            }
            while (Win32.Process32NextW(snap, ref e));
        }
        catch
        {
            // 快照过程失败就当没看到，调用方会走"已退出"分支
        }
        finally
        {
            Win32.CloseHandle(snap);
        }
        return list;
    }

    /// <summary>某个 PID 的直接子进程（父进程已退出也仍然能枚举到）</summary>
    public static IEnumerable<int> ChildrenOf(int parentPid)
    {
        if (parentPid <= 4) yield break;

        foreach (var e in Snapshot())
        {
            if (e.ParentPid != parentPid) continue;
            if (e.Pid == parentPid || e.Pid <= 4) continue;
            yield return e.Pid;
        }
    }

    public static bool TryOpen(int pid, out IntPtr handle)
    {
        handle = Win32.OpenProcess(
            Win32.SYNCHRONIZE | Win32.PROCESS_QUERY_LIMITED_INFORMATION | Win32.PROCESS_TERMINATE,
            false, pid);
        return handle != IntPtr.Zero;
    }

    /// <summary>进程创建时间；拿不到返回 null</summary>
    public static DateTime? CreatedAt(IntPtr handle)
    {
        if (!Win32.GetProcessTimes(handle, out long creation, out _, out _, out _)) return null;
        try { return DateTime.FromFileTimeUtc(creation).ToLocalTime(); }
        catch { return null; }
    }

    /// <summary>进程完整镜像路径</summary>
    public static string? ImagePath(IntPtr handle)
    {
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        if (!Win32.QueryFullProcessImageNameW(handle, 0, sb, ref size)) return null;
        return size > 0 ? sb.ToString(0, size) : null;
    }

    public static bool TryKill(int pid)
    {
        if (pid <= 4) return false;
        if (!TryOpen(pid, out IntPtr h)) return false;
        try { return Win32.TerminateProcess(h, 0); }
        finally { Win32.CloseHandle(h); }
    }
}

/// <summary>
/// 每个实例一个 Job 对象：目标进程及其所有子进程自动加入同一个 Job，
/// 因此启动器退出后新拉起的客户端仍在 Job 里，可以准确认回来。
/// 结束实例时 TerminateJobObject 会连同子进程一起结束，不留残进程。
/// </summary>
public sealed class JobObject : IDisposable
{
    private const int JobObjectBasicProcessIdList = 3;

    private IntPtr _handle;

    private JobObject(IntPtr handle) => _handle = handle;

    public IntPtr Handle => _handle;
    public bool IsValid => _handle != IntPtr.Zero;

    public static JobObject? TryCreate()
    {
        IntPtr h = Win32.CreateJobObjectW(IntPtr.Zero, null);
        return h == IntPtr.Zero ? null : new JobObject(h);
    }

    public bool Assign(IntPtr process)
    {
        if (!IsValid || process == IntPtr.Zero) return false;
        return Win32.AssignProcessToJobObject(_handle, process);
    }

    /// <summary>Job 内当前存活的 PID 列表</summary>
    public List<int> ProcessIds(int capacity = 64)
    {
        var result = new List<int>();
        if (!IsValid) return result;

        int unit = IntPtr.Size;
        int size = 8 + unit * capacity;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            // list 比 capacity 长时返回 FALSE + ERROR_MORE_DATA，但已经填好的那一截仍然有效
            bool ok = Win32.QueryInformationJobObject(_handle, JobObjectBasicProcessIdList,
                                                     buffer, size, out _);
            const int ERROR_MORE_DATA = 234;
            if (!ok && Marshal.GetLastPInvokeError() != ERROR_MORE_DATA) return result;

            int count = Math.Min(Marshal.ReadInt32(buffer, 4), capacity);
            for (int i = 0; i < count; i++)
            {
                long pid = unit == 8
                    ? Marshal.ReadInt64(buffer, 8 + i * 8)
                    : Marshal.ReadInt32(buffer, 8 + i * 4);
                if (pid > 4) result.Add((int)pid);
            }
        }
        catch
        {
            // 结构体解析失败时退回父 PID 追踪
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    public bool Terminate(uint exitCode = 0) => IsValid && Win32.TerminateJobObject(_handle, exitCode);

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        Win32.CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }
}
