// 验证注册表隔离：程序在 HKCU\Software\V5RegTest 下建键写值，
// 然后枚举 HKCU\Software 下所有 V5RegTest* 的键。
// 不注入时应只有 V5RegTest；开启注册表隔离后应出现 V5RegTestGroup_<组号>。
using System.Text;
using Microsoft.Win32;

string basePath = @"Software\V5RegTest";
var sb = new StringBuilder();
sb.Append($"pid={Environment.ProcessId} reg='{Environment.GetEnvironmentVariable("V5RegFilter") ?? "-"}' ");

// 先把上次留下的 V5RegTest* 全部清掉，保证每次都走"创建"路径
try
{
    using var root0 = Registry.CurrentUser.OpenSubKey("Software", writable: true);
    foreach (var n in root0!.GetSubKeyNames()
                          .Where(n => n.StartsWith("V5RegTest", StringComparison.OrdinalIgnoreCase))
                          .ToArray())
        root0.DeleteSubKeyTree(n);
}
catch { }

try
{
    using var k = Registry.CurrentUser.CreateSubKey(basePath + @"\Instance");
    k.SetValue("marker", "hello");
    sb.Append("write=OK ");
}
catch (Exception e) { sb.Append("write=FAIL:" + e.GetType().Name + " "); }

try
{
    using var root = Registry.CurrentUser.OpenSubKey("Software");
    var hits = root!.GetSubKeyNames()
                   .Where(n => n.Contains("V5Reg", StringComparison.OrdinalIgnoreCase))
                   .OrderBy(n => n).ToArray();
    sb.Append("keys=").Append(string.Join(",", hits));
}
catch (Exception e) { sb.Append("enum=FAIL:" + e.GetType().Name); }

string outPath = Path.Combine(AppContext.BaseDirectory, "regtest.out");
File.AppendAllText(outPath, sb.ToString() + Environment.NewLine);
Console.WriteLine(sb.ToString());
Thread.Sleep(4000);   // 留时间给 hook 日志落盘
