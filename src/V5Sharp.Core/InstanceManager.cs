using System.Diagnostics;

namespace V5Sharp.Core;

/// <summary>
/// 一个多开实例。句柄在进程退出后立即释放，避免长时间运行累积句柄。
///
/// 特别注意"启动器型"目标（如 Launch.exe）：它拉起真正的客户端后自己就退出了。
/// 这种入口进程消失并不代表实例结束，因此退出时会先在 Job / 子进程里找"接替者"，
/// 找到就把实例平移到新进程上，UI 上看到的是同一个实例、新的 PID。
/// </summary>
public sealed class Instance : IDisposable
{
    private const uint STILL_ACTIVE = 259;

    /// <summary>入口进程退出后，等待接替进程出现的时间窗</summary>
    private static readonly TimeSpan ChaseWindow = TimeSpan.FromSeconds(6);

    public int Pid { get; internal set; }
    public uint GroupId { get; init; }
    public string ExePath { get; internal set; } = "";
    public string ExeName { get; internal set; } = "";
    public DateTime StartTime { get; init; } = DateTime.Now;
    public string? HookLogPath { get; init; }

    public IntPtr ProcessHandle { get; internal set; } = IntPtr.Zero;
    public bool IsRunning { get; private set; } = true;
    public uint? ExitCode { get; private set; }

    /// <summary>接替次数：0 = 一直是入口进程，≥1 = 已跟随到后续进程</summary>
    public int ChaseCount { get; private set; }

    /// <summary>入口进程已退出、正在等接替进程出现（过渡态，UI 显示"接替中"）</summary>
    public bool IsChasing => IsRunning && _graceUntil is not null;

    /// <summary>待写进日志的说明（UI 取走后清空）</summary>
    public string? PendingNote { get; private set; }

    internal JobObject? Job { get; set; }
    internal bool JobAssigned { get; set; }

    private DateTime? _graceUntil;

    public string? TakeNote()
    {
        string? note = PendingNote;
        PendingNote = null;
        return note;
    }

    /// <summary>
    /// 轮询一次状态。claimed 是"别的实例已经占用的 PID"，
    /// 用来避免两个实例抢同一个接替进程。
    /// </summary>
    public void Refresh(ISet<int>? claimed = null)
    {
        if (!IsRunning) return;
        if (ProcessHandle == IntPtr.Zero)
        {
            MarkExited();
            return;
        }

        if (!Win32.GetExitCodeProcess(ProcessHandle, out uint code)) return;
        if (code == STILL_ACTIVE)
        {
            _graceUntil = null;
            return;
        }

        // 入口进程没了：先尝试"接替"
        if (TryAdopt(claimed)) return;

        // 拉起的客户端可能晚几百毫秒才出现，给一个宽限窗口再判死。
        // 窗口起点用进程"真正退出"的时刻，而不是第一次轮询到它退出的时刻：
        // 面板收起、窗口最小化、系统休眠都会让轮询延后很久，按发现时刻计时的话，
        // 一个早就退出的实例会被重新塞进一整段"接替中"。
        if (_graceUntil is null)
        {
            DateTime exitAt = ProcTree.ExitedAt(ProcessHandle) ?? DateTime.UtcNow;
            _graceUntil = exitAt + ChaseWindow;
            if (DateTime.UtcNow < _graceUntil.Value)
                PendingNote = $"实例组 {GroupId}：入口进程 PID={Pid} 已退出，等待它拉起的客户端进程…";
        }
        if (DateTime.UtcNow < _graceUntil.Value) return;

        ExitCode = code;
        MarkExited();
    }

    /// <summary>尝试把实例平移到接替进程上（Job 内的进程优先，其次按父 PID 找子进程）</summary>
    private bool TryAdopt(ISet<int>? claimed)
    {
        int from = Pid;

        var candidates = new List<int>();
        if (Job is { IsValid: true } && JobAssigned)
            candidates.AddRange(Job.ProcessIds());
        candidates.AddRange(ProcTree.ChildrenOf(from));

        foreach (int pid in candidates.Distinct())
        {
            if (pid == from || pid <= 4) continue;
            if (claimed is not null && claimed.Contains(pid)) continue;
            if (!ProcTree.TryOpen(pid, out IntPtr handle)) continue;

            // 只接受"本次启动之后才出现"的进程，防止 PID 复用时认错人
            DateTime? created = ProcTree.CreatedAt(handle);
            if (created is DateTime t && t < StartTime.AddSeconds(-2))
            {
                Win32.CloseHandle(handle);
                continue;
            }

            Adopt(pid, handle, ProcTree.ImagePath(handle));
            return true;
        }
        return false;
    }

    private void Adopt(int pid, IntPtr handle, string? imagePath)
    {
        if (ProcessHandle != IntPtr.Zero) Win32.CloseHandle(ProcessHandle);

        ProcessHandle = handle;
        Pid = pid;
        ChaseCount++;
        _graceUntil = null;

        if (!string.IsNullOrEmpty(imagePath))
        {
            ExePath = imagePath;
            ExeName = Path.GetFileName(imagePath);
        }

        PendingNote = $"实例组 {GroupId}：已接替到新进程 PID={pid}（{ExeName}）";
    }

    public void MarkExited()
    {
        IsRunning = false;
        if (ProcessHandle != IntPtr.Zero)
        {
            Win32.CloseHandle(ProcessHandle);
            ProcessHandle = IntPtr.Zero;
        }
        Job?.Dispose();
        Job = null;
        JobAssigned = false;
    }

    public bool Kill()
    {
        bool ok = false;

        // 有 Job 就整树结束（启动器 + 客户端一起走）
        if (Job is { IsValid: true } && JobAssigned && Job.Terminate(0))
            ok = true;
        else if (ProcessHandle != IntPtr.Zero)
            ok = Win32.TerminateProcess(ProcessHandle, 0);

        // 主动结束 = 实例到此为止。不然被杀的目标常常会拉起
        // UniCrashReporter 之类的"临终进程"，接替逻辑会把它误当成客户端继续跟。
        _graceUntil = null;
        ExitCode ??= 0;
        MarkExited();
        return ok;
    }

    public void Dispose() => MarkExited();
}

/// <summary>
/// 实例管理器：分配组号、批量启动、状态刷新、批量结束。
/// 线程安全；UI 通过 <see cref="Instances"/> 拿到快照渲染。
/// </summary>
public sealed class InstanceManager : IDisposable
{
    private const int MaxHistory = 60;

    private readonly List<Instance> _instances = new();
    private readonly object _gate = new();
    private long _nextGroup;

    /// <summary>需要写进 UI 日志的事件（接替成功、等待接替等）</summary>
    public event Action<string>? Notify;

    public InstanceManager()
    {
        // 组号只需"同时刻唯一"，用毫秒计数起步足够
        _nextGroup = Environment.TickCount64 % 900_000;
    }

    public IReadOnlyList<Instance> Instances
    {
        get { lock (_gate) return _instances.ToArray(); }
    }

    public int RunningCount
    {
        get { lock (_gate) return _instances.Count(i => i.IsRunning); }
    }

    public Instance LaunchOne(LaunchOptions opt)
    {
        uint group = NextGroup();
        Instance inst = Launcher.Launch(opt, group);
        lock (_gate) _instances.Add(inst);
        return inst;
    }

    public List<Instance> LaunchMany(LaunchOptions opt, int count,
                                     Action<string>? log = null,
                                     CancellationToken ct = default)
    {
        var started = new List<Instance>();
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Instance inst = LaunchOne(opt);
                started.Add(inst);
                log?.Invoke($"已启动实例 #{started.Count}  PID={inst.Pid}  组号={inst.GroupId}");
                if (i < count - 1) Thread.Sleep(600);   // 错开启动，避免目标自身初始化竞争
            }
            catch (Exception ex)
            {
                log?.Invoke($"第 {i + 1} 个实例启动失败: {ex.Message}");
                break;
            }
        }
        return started;
    }

    /// <summary>刷新所有实例状态，并裁剪过期的历史记录。</summary>
    public void Refresh()
    {
        var notes = new List<string>();

        lock (_gate)
        {
            // 先收集"正在运行"的 PID，作为本次接替的排除集合
            var claimed = new HashSet<int>(_instances.Where(i => i.IsRunning).Select(i => i.Pid));

            foreach (var inst in _instances)
            {
                if (!inst.IsRunning) continue;
                claimed.Remove(inst.Pid);
                inst.Refresh(claimed);
                if (inst.IsRunning) claimed.Add(inst.Pid);

                string? note = inst.TakeNote();
                if (note is not null) notes.Add(note);
            }

            int exited = _instances.Count(i => !i.IsRunning);
            if (exited > MaxHistory)
            {
                int toRemove = exited - MaxHistory;
                for (int i = 0; i < _instances.Count && toRemove > 0; i++)
                {
                    if (_instances[i].IsRunning) continue;
                    _instances[i].Dispose();
                    _instances.RemoveAt(i);
                    i--;
                    toRemove--;
                }
            }
        }

        // 日志在锁外发，避免 UI 回调再进管理器时死锁
        foreach (string note in notes) Notify?.Invoke(note);
    }

    public int KillAll()
    {
        int n = 0;
        lock (_gate)
        {
            foreach (var inst in _instances)
                if (inst.Kill()) n++;
        }
        return n;
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var i in _instances) i.Dispose();
            _instances.Clear();
        }
    }

    private uint NextGroup()
    {
        lock (_gate)
        {
            for (int guard = 0; guard < 1000; guard++)
            {
                _nextGroup++;
                if (_nextGroup > 900_000) _nextGroup = 1;
                uint candidate = (uint)_nextGroup;
                if (_instances.Any(i => i.IsRunning && i.GroupId == candidate)) continue;
                return candidate;
            }
            return (uint)Random.Shared.Next(1, 900_000);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var i in _instances) i.Dispose();
            _instances.Clear();
        }
    }
}
