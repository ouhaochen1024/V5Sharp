/*
 * TestTarget - 模拟"只允许单实例"的程序，用于验证多开器。
 *
 * 单实例判据与游戏启动器一致：命名互斥体 + 命名事件 + 命名共享内存。
 *   - 直接运行两次：第二次 BLOCKED 并退出；
 *   - 通过 V5Sharp 启动：组号不同 → 名字被改成 xxxGroup_N → 全部 RUNNING。
 *
 * 结果同时写到 exe 同目录的 run.log，避免控制台被父进程关闭时丢失输出。
 */
using System.IO.MemoryMappedFiles;
using System.Text;

const string MutexName = "V5Test_MultiOpen_Mutex";
const string EventName = "V5Test_MultiOpen_Event";
const string MapName = "V5Test_MultiOpen_SharedMem";

int seconds = args.Length > 0 && int.TryParse(args[0], out int s) ? s : 10;

string logPath = Path.Combine(AppContext.BaseDirectory, "run.log");
void Trace(string text)
{
    try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}", Encoding.UTF8); }
    catch { /* ignore */ }
}

Mutex? mutex = null;
EventWaitHandle? evt = null;
MemoryMappedFile? map = null;
try
{
    mutex = new Mutex(true, MutexName, out bool created);
    if (!created)
    {
        Trace($"BLOCKED pid={Environment.ProcessId}");
        Console.WriteLine($"BLOCKED  pid={Environment.ProcessId}");
        return 2;
    }

    evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
    map = MemoryMappedFile.CreateOrOpen(MapName, 4096);

    string group = Environment.GetEnvironmentVariable("ProcessGroup") ?? "(none)";
    string arch = Environment.Is64BitProcess ? "x64" : "x86";
    Trace($"RUNNING pid={Environment.ProcessId} group={group} {arch} secs={seconds}");
    Console.WriteLine($"RUNNING  pid={Environment.ProcessId}  group={group}  {arch}");

    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    Trace($"EXIT    pid={Environment.ProcessId}");
    return 0;
}
finally
{
    map?.Dispose();
    evt?.Dispose();
    mutex?.Dispose();
}
