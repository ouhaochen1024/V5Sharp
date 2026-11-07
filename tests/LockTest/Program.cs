// 模拟两类"文件锁型"单实例检测：
//   1) 独占打开自身 exe（运行中的映像占着这张文件，第二个实例必然失败）
//   2) 独占打开程序目录下的锁文件，并持有若干秒
// 结果追加到 locktest.out，便于对照"注入前 / 注入后"。
using System.Text;

string exe = Environment.ProcessPath!;
string dir = AppContext.BaseDirectory;
int holdMs = args.Length > 0 && int.TryParse(args[0], out int s) ? s * 1000 : 6000;

var sb = new StringBuilder();
sb.Append($"pid={Environment.ProcessId} ");
sb.Append($"strong={Environment.GetEnvironmentVariable("V5Strong") ?? "-"} ");
sb.Append($"temp={Path.GetFileName(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))} ");

// 故意创建一个命名互斥体，用来确认 hook 与日志链路是通的
using var probeMutex = new Mutex(false, "V5LockTestProbe");

string exeProbe;
try
{
    using var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None);
    exeProbe = "exe=OK";
}
catch (Exception e) { exeProbe = "exe=FAIL:" + e.GetType().Name; }
sb.Append(exeProbe).Append(' ');

FileStream? hold = null;
string lockProbe;
try
{
    hold = new FileStream(Path.Combine(dir, "lock.dat"), FileMode.OpenOrCreate,
                          FileAccess.ReadWrite, FileShare.None);
    lockProbe = "lock=OK";
}
catch (Exception e) { lockProbe = "lock=FAIL:" + e.GetType().Name; }
sb.Append(lockProbe).AppendLine();

File.AppendAllText(Path.Combine(dir, "locktest.out"), sb.ToString());
Thread.Sleep(holdMs);
hold?.Dispose();
