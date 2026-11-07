using V5Sharp.Cli;
using V5Sharp.Core;

/*
 * V5Sharp.Cli - 命令行入口（便于脚本化 / 自测）
 *   V5Sharp.Cli launch --exe <path> [--args "..."] [--count N] [--no-log] [--wait SEC]
 *   V5Sharp.Cli kill   --pid <pid>[,<pid>...]
 */

if (args.Length == 0)
{
    Console.WriteLine(@"用法:
  V5Sharp.Cli launch --exe <程序路径> [--args ""参数""] [--workdir 目录] [--protect 进程名] [--strong] [--global-hide] [--reg 关键字] [--count N] [--no-log] [--wait 秒]
  V5Sharp.Cli kill   --pid <pid>[,pid...]");
    return 1;
}

string cmd = args[0].ToLowerInvariant();
var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (int i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) continue;
    string key = args[i][2..];
    string val = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "1";
    opt[key] = val;
}

switch (cmd)
{
    case "launch":
        {
            string exe = opt.GetValueOrDefault("exe", "");
            if (!File.Exists(exe)) { Console.Error.WriteLine("找不到 exe: " + exe); return 2; }

            var lo = new LaunchOptions
            {
                ExePath = exe,
                Arguments = opt.GetValueOrDefault("args", ""),
                WorkingDirectory = opt.TryGetValue("workdir", out string? wd) ? wd : null,
                HideProcessNames = opt.GetValueOrDefault("protect", opt.GetValueOrDefault("hide", "")),
                StrongMode = opt.ContainsKey("strong"),
                StrongSuffixes = opt.GetValueOrDefault("strong-sfx", ""),
                GlobalHide = opt.ContainsKey("global-hide"),
                RegFilter = opt.GetValueOrDefault("reg", ""),
                WriteHookLog = !opt.ContainsKey("no-log")
            };
            int count = int.TryParse(opt.GetValueOrDefault("count", "2"), out int c) ? c : 2;

            using var mgr = new InstanceManager();
            var started = mgr.LaunchMany(lo, count, Console.WriteLine);
            Console.WriteLine($"--- 成功启动 {started.Count} 个实例 ---");
            foreach (var i in started)
            {
                bool is32 = Injector.IsTarget32Bit(i.Pid);
                Console.WriteLine($"PID={i.Pid,-6} 组号={i.GroupId,-8} 位宽={(is32 ? "x86" : "x64"),-4} 日志={i.HookLogPath}");
            }

            if (int.TryParse(opt.GetValueOrDefault("wait", "0"), out int wait) && wait > 0)
            {
                Thread.Sleep(wait * 1000);
                Console.WriteLine($"等待 {wait}s 后状态: 运行中 {mgr.RunningCount}/{started.Count}");
            }
            return started.Count == count ? 0 : 3;
        }

    case "kill":
        {
            int n = 0;
            foreach (string s in opt.GetValueOrDefault("pid", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(int.Parse(s.Trim()));
                    p.Kill();
                    n++;
                }
                catch (Exception ex) { Console.Error.WriteLine($"{s}: {ex.Message}"); }
            }
            Console.WriteLine($"已结束 {n} 个进程");
            return 0;
        }

    case "modules":
        {
            // 诊断用：打印目标进程的 32/64 位模块列表
            int pid = int.Parse(opt.GetValueOrDefault("pid", "0"));
            int.TryParse(opt.GetValueOrDefault("filter", "1"), out int filter);
            int n = ProcModules.Dump(pid, filter);
            return n > 0 ? 0 : 4;
        }

    default:
        Console.Error.WriteLine("未知命令: " + cmd);
        return 1;
}
