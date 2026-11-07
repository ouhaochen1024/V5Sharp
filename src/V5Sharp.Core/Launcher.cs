using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace V5Sharp.Core;

/// <summary>启动参数</summary>
public sealed class LaunchOptions
{
    public string ExePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public bool WriteHookLog { get; set; } = true;
    public string? LogDirectory { get; set; }

    /// <summary>
    /// 让被注入进程"看不见"这些进程（; 分隔，支持 * ?）。
    /// 用于对付启动器枚举同名进程后 TerminateProcess 的行为，例如 "app.exe"。
    /// </summary>
    public string HideProcessNames { get; set; } = "";

    /// <summary>
    /// 加强模式（虚拟环境）：
    /// 1) 把"独占打开自身 exe / 锁文件"的检测放宽成共享打开，让文件锁型单实例检查失效；
    /// 2) 给每个实例独立的临时目录，避免临时文件互撞。
    /// </summary>
    public bool StrongMode { get; set; }

    /// <summary>加强模式额外要放宽的锁文件后缀（; 分隔），留空用默认 *.lock;*.pid;*.mutex</summary>
    public string StrongSuffixes { get; set; } = "";

    /// <summary>
    /// 全局隐藏：把"其他实例"从进程枚举结果里摘掉，让对方完全看不见。
    /// 比单纯的"保护进程"更彻底 —— 后者只拦终止请求，前者连枚举都看不到。
    /// </summary>
    public bool GlobalHide { get; set; }

    /// <summary>
    /// 注册表隔离关键词（; 分隔）。命中的注册表路径会被追加组号后缀，
    /// 每个实例拥有一棵独立的配置子树。留空 = 完全不碰注册表。
    /// </summary>
    public string RegFilter { get; set; } = "";
}

/// <summary>
/// 启动器：与原始 V5.exe 的 CreateProcessW 流程等价，但改成 C# 实现并支持 64 位目标。
///
/// 顺序：构建环境块(带 ProcessGroup) → CREATE_SUSPENDED 创建进程
///       → 注入 v5_hook → ResumeThread。
/// 挂起创建保证 hook 一定在目标执行任何代码之前装好。
/// </summary>
public static class Launcher
{
    public const string Payload32 = "V5Hook32.dll";
    public const string Payload64 = "V5Hook64.dll";

    /// <summary>注入模块目录：优先输出目录下的 payload\，其次向上找 dist\payload\（开发/CLI 场景）</summary>
    public static string PayloadDirectory
    {
        get
        {
            string local = Path.Combine(AppContext.BaseDirectory, "payload");
            if (Directory.Exists(local)) return local;

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            {
                string cand = Path.Combine(dir.FullName, "dist", "payload");
                if (Directory.Exists(cand)) return cand;
            }
            return local;
        }
    }

    public static Instance Launch(LaunchOptions opt, uint groupId)
    {
        if (string.IsNullOrWhiteSpace(opt.ExePath))
            throw new ArgumentException("未指定可执行文件");
        if (!File.Exists(opt.ExePath))
            throw new FileNotFoundException("找不到可执行文件", opt.ExePath);

        string workDir = opt.WorkingDirectory ?? Path.GetDirectoryName(opt.ExePath) ?? ".";
        string? hookLog = null;

        if (opt.WriteHookLog)
        {
            string dir = opt.LogDirectory ?? Path.Combine(AppContext.BaseDirectory, "Log");
            Directory.CreateDirectory(dir);
            hookLog = Path.Combine(dir, $"instance_{groupId}.log");
        }

        var si = new Win32.STARTUPINFO { cb = Marshal.SizeOf<Win32.STARTUPINFO>() };
        var cmdline = new StringBuilder($"\"{opt.ExePath}\" {opt.Arguments}".TrimEnd());

        bool is32 = PeInfo.Is32Bit(opt.ExePath);
        string payload = Path.Combine(PayloadDirectory, is32 ? Payload32 : Payload64);
        if (!File.Exists(payload))
            throw new FileNotFoundException($"缺少注入模块: {payload}");

        IntPtr envBlock = IntPtr.Zero;
        Win32.PROCESS_INFORMATION pi;
        JobObject? job = null;
        try
        {
            if (is32)
            {
                // 32 位：交给 32 位辅助进程（它会以调试方式创建目标、在 32 位初始断点处注入）
                // 环境变量由 psi.Environment 传给辅助进程，这里不需要自己拼环境块
                pi = LaunchViaHelper32(opt, groupId, payload, hookLog);

                // 辅助进程已经把它创建好了，这里只能补登记（子进程仍可靠父 PID 追踪）
                job = JobObject.TryCreate();
                if (job is not null && !job.Assign(pi.hProcess)) { job.Dispose(); job = null; }
            }
            else
            {
                envBlock = BuildEnvironment(groupId, hookLog, opt.HideProcessNames, opt);
                if (!Win32.CreateProcessW(
                        opt.ExePath, cmdline, IntPtr.Zero, IntPtr.Zero, false,
                        Win32.CREATE_SUSPENDED | Win32.CREATE_UNICODE_ENVIRONMENT,
                        envBlock, workDir, ref si, out pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW 失败");

                // 挂起状态下先入 Job：目标之后拉起的子进程会自动继承 Job 成员身份，
                // 这样"启动器退出、客户端接管"时能准确认回来，结束实例也能整树结束。
                job = JobObject.TryCreate();
                if (job is not null && !job.Assign(pi.hProcess)) { job.Dispose(); job = null; }

                try
                {
                    Injector.Inject(pi.dwProcessId, payload);
                }
                catch
                {
                    try { Win32.TerminateProcess(pi.hProcess, 0); } catch { /* ignore */ }
                    job?.Dispose();
                    Win32.CloseHandle(pi.hThread);
                    Win32.CloseHandle(pi.hProcess);
                    throw;
                }
            }
        }
        finally
        {
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
        }

        var inst = new Instance
        {
            Pid = pi.dwProcessId,
            GroupId = groupId,
            ExePath = opt.ExePath,
            ExeName = Path.GetFileName(opt.ExePath),
            StartTime = DateTime.Now,
            HookLogPath = hookLog,
            ProcessHandle = pi.hProcess,
            Job = job,
            JobAssigned = job is not null
        };

        if (pi.hThread != IntPtr.Zero)
        {
            Win32.ResumeThread(pi.hThread);
            Win32.CloseHandle(pi.hThread);
        }
        return inst;
    }

    /// <summary>
    /// 32 位目标：通过 V5Inject32.exe 创建并注入，返回已启动进程的句柄信息。
    /// 具体原因见 native\V5Inject32\V5Inject32.c 顶部注释。
    /// </summary>
    private static Win32.PROCESS_INFORMATION LaunchViaHelper32(
        LaunchOptions opt, uint groupId, string payload, string? hookLogPath)
    {
        string helper = Path.Combine(PayloadDirectory, "V5Inject32.exe");
        if (!File.Exists(helper))
            throw new FileNotFoundException("缺少 32 位注入辅助进程，无法启动 32 位目标", helper);

        string pidFile = Path.Combine(Path.GetTempPath(), $"v5sharp_{groupId}_{Environment.ProcessId}.pid");
        try { File.Delete(pidFile); } catch { /* ignore */ }

        var psi = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--pidfile"); psi.ArgumentList.Add(pidFile);
        psi.ArgumentList.Add("--exe");  psi.ArgumentList.Add(opt.ExePath);
        if (!string.IsNullOrWhiteSpace(opt.Arguments))
        {
            psi.ArgumentList.Add("--args"); psi.ArgumentList.Add(opt.Arguments);
        }
        psi.ArgumentList.Add("--group"); psi.ArgumentList.Add(groupId.ToString());
        if (hookLogPath is not null)
        {
            psi.ArgumentList.Add("--log"); psi.ArgumentList.Add(hookLogPath);
        }
        if (!string.IsNullOrWhiteSpace(opt.HideProcessNames))
        {
            psi.ArgumentList.Add("--hide"); psi.ArgumentList.Add(opt.HideProcessNames.Trim());
        }
        if (opt.GlobalHide)
            psi.ArgumentList.Add("--global-hide");
        if (!string.IsNullOrWhiteSpace(opt.RegFilter))
        {
            psi.ArgumentList.Add("--reg"); psi.ArgumentList.Add(opt.RegFilter.Trim());
        }
        if (opt.StrongMode)
        {
            psi.ArgumentList.Add("--strong");
            if (!string.IsNullOrWhiteSpace(opt.StrongSuffixes))
            {
                psi.ArgumentList.Add("--strong-sfx"); psi.ArgumentList.Add(opt.StrongSuffixes.Trim());
            }
            // 辅助进程会把自身环境传给目标，这里先把 TEMP 换成实例私有目录
            var envMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ApplyExtraEnvironment(envMap, groupId, opt);
            foreach (var kv in envMap)
            {
                if (kv.Key is "TEMP" or "TMP" or "V5Strong" or "V5StrongSuffixes"
                         or "V5GlobalHide" or "V5RegFilter")
                    psi.Environment[kv.Key] = kv.Value;
            }
        }
        psi.ArgumentList.Add("--dll"); psi.ArgumentList.Add(payload);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 32 位注入辅助进程");

        if (!proc.WaitForExit(30000))
        {
            // 辅助进程卡死：清理掉，否则它的句柄与临时文件会一直挂着
            try { proc.Kill(true); } catch { /* ignore */ }
            try { File.Delete(pidFile); } catch { /* ignore */ }
            throw new TimeoutException("32 位注入辅助进程 30 秒内没有返回，已强制结束");
        }

        string result = File.Exists(pidFile) ? File.ReadAllText(pidFile).Trim() : "";
        try { File.Delete(pidFile); } catch { /* ignore */ }

        const string marker = "PID=";
        int idx = result.IndexOf(marker, StringComparison.Ordinal);
        if (proc.ExitCode != 0 || idx < 0)
            throw new InvalidOperationException($"32 位启动失败 (exit={proc.ExitCode}) {result}".Trim());

        int p0 = idx + marker.Length, p1 = p0;
        while (p1 < result.Length && char.IsDigit(result[p1])) p1++;
        if (p1 == p0)
            throw new InvalidOperationException("32 位辅助进程未返回有效 PID: " + result);
        int pid = int.Parse(result[p0..p1]);
        IntPtr hProc = Win32.OpenProcess(
            Win32.PROCESS_QUERY_LIMITED_INFORMATION | Win32.PROCESS_TERMINATE | 0x00100000 /*SYNCHRONIZE*/,
            false, pid);
        if (hProc == IntPtr.Zero)
            throw new InvalidOperationException("已注入成功但无法打开目标进程句柄");

        return new Win32.PROCESS_INFORMATION
        {
            hProcess = hProc,
            hThread = IntPtr.Zero,
            dwProcessId = pid,
            dwThreadId = 0
        };
    }

    /// <summary>
    /// 加强模式的环境部分：打开注入模块的开关，并把 TEMP/TMP 指到实例私有目录。
    ///
    /// 为什么要独立 TEMP：不少程序把"实例标识 / 锁 / 解包缓存"放在 %TEMP%，
    /// 共用同一个目录时两个实例会互相覆盖、互相删文件。分开之后各写各的。
    /// 目录按组号命名且组号唯一，所以每次启动前可以放心清空，不会误删别人的。
    /// </summary>
    private static void ApplyExtraEnvironment(IDictionary<string, string> map, uint groupId,
                                               LaunchOptions opt)
    {
        /* 加强模式 */
        if (!opt.StrongMode)
        {
            map.Remove("V5Strong");
            map.Remove("V5StrongSuffixes");
        }
        else
        {
            map["V5Strong"] = "1";
            if (!string.IsNullOrWhiteSpace(opt.StrongSuffixes))
                map["V5StrongSuffixes"] = opt.StrongSuffixes.Trim();
            else
                map.Remove("V5StrongSuffixes");
        }

        /* 全局隐藏 */
        if (opt.GlobalHide) map["V5GlobalHide"] = "1";
        else map.Remove("V5GlobalHide");

        /* 注册表隔离 */
        if (!string.IsNullOrWhiteSpace(opt.RegFilter))
            map["V5RegFilter"] = opt.RegFilter.Trim();
        else
            map.Remove("V5RegFilter");

        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "V5Sharp", "inst", groupId.ToString());
            string temp = Path.Combine(root, "temp");

            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* 占用中就这样 */ }
            Directory.CreateDirectory(temp);

            map["TEMP"] = temp;
            map["TMP"] = temp;
        }
        catch
        {
            // 目录建不出来就退回共享 TEMP，加强模式的文件锁部分仍然有效
        }
    }

    /// <summary>
    /// 构建目标进程的环境块：当前环境 + ProcessGroup=<组号> [+ V5SharpLog=<日志>]
    /// （原始 V5.exe 是 "%s=%s" 往 GetEnvironmentStringsW 的结果后面追加，作用相同）
    /// </summary>
    private static IntPtr BuildEnvironment(uint groupId, string? hookLogPath, string? hideNames,
                                           LaunchOptions opt)
    {
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k && e.Value is string v && k.Length > 0)
                map[k] = v;
        }
        map["ProcessGroup"] = groupId.ToString();
        if (hookLogPath is not null)
            map["V5SharpLog"] = hookLogPath;
        else
            map.Remove("V5SharpLog");

        if (!string.IsNullOrWhiteSpace(hideNames))
            map["V5HideNames"] = hideNames.Trim();
        else
            map.Remove("V5HideNames");

        ApplyExtraEnvironment(map, groupId, opt);

        var sb = new StringBuilder();
        foreach (var kv in map)
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }
}
