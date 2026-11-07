using System.Text;

namespace V5Sharp.Core;

/// <summary>极简 ini 读写（对应原始 config.ini 的 [V5] 段，字段名保持兼容）</summary>
public sealed class AppConfig
{
    public string ExePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkDir { get; set; } = "";

    /// <summary>让目标进程"看不见"的进程名（; 分隔），用于启动器类程序</summary>
    public string HideNames { get; set; } = "";
    public int Count { get; set; } = 2;
    public bool WriteHookLog { get; set; } = true;

    /// <summary>加强模式（虚拟环境）：文件锁虚拟化 + 每实例独立临时目录</summary>
    public bool StrongMode { get; set; }

    /// <summary>加强模式额外放宽的锁文件后缀（; 分隔），留空用默认值</summary>
    public string StrongSuffixes { get; set; } = "";

    /// <summary>全局隐藏：让对方在进程枚举里看不到其他实例</summary>
    public bool GlobalHide { get; set; }

    /// <summary>注册表隔离关键词（; 分隔），留空不隔离</summary>
    public string RegFilter { get; set; } = "";

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "v5sharp.ini");

    public static AppConfig Load(string path)
    {
        var cfg = new AppConfig();
        if (!File.Exists(path)) return cfg;

        foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();

            switch (key.ToLowerInvariant())
            {
                case "exepath":
                    cfg.ExePath = val; break;
                case "lastpath":            // 兼容原版键名；不要覆盖已显式配置的 ExePath
                    if (string.IsNullOrWhiteSpace(cfg.ExePath)) cfg.ExePath = val;
                    break;
                case "arguments":
                case "args":
                    cfg.Arguments = val; break;
                case "workdir":
                case "workingdirectory":
                    cfg.WorkDir = val; break;
                case "hidenames":
                case "hide":
                    cfg.HideNames = val; break;
                case "count":
                case "instances":
                    if (int.TryParse(val, out int c)) cfg.Count = Math.Clamp(c, 1, 64); break;
                case "writehooklog":
                case "query":
                    cfg.WriteHookLog = Flag(val); break;
                case "strongmode":
                case "strong":
                    cfg.StrongMode = Flag(val); break;
                case "strongsuffixes":
                    cfg.StrongSuffixes = val; break;
                case "globalhide":
                case "global-hide":
                    cfg.GlobalHide = Flag(val); break;
                case "regfilter":
                case "reg":
                    cfg.RegFilter = val; break;
            }
        }
        return cfg;
    }

    /// <summary>开关键取值：1/true/yes/on 都算开（大小写不敏感），其余算关</summary>
    private static bool Flag(string val) =>
        val.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    public void Save(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; V5Sharp 多开器配置");
        sb.AppendLine("[V5Sharp]");
        sb.AppendLine($"ExePath={ExePath}");
        sb.AppendLine($"Arguments={Arguments}");
        sb.AppendLine($"WorkDir={WorkDir}");
        sb.AppendLine($"HideNames={HideNames}");
        sb.AppendLine($"Count={Count}");
        sb.AppendLine($"WriteHookLog={(WriteHookLog ? 1 : 0)}");
        sb.AppendLine($"StrongMode={(StrongMode ? 1 : 0)}");
        if (!string.IsNullOrWhiteSpace(StrongSuffixes))
            sb.AppendLine($"StrongSuffixes={StrongSuffixes}");
        sb.AppendLine($"GlobalHide={(GlobalHide ? 1 : 0)}");
        if (!string.IsNullOrWhiteSpace(RegFilter))
            sb.AppendLine($"RegFilter={RegFilter}");
        sb.AppendLine();
        sb.AppendLine("[V5]");
        sb.AppendLine("Title=V5多开器Pro");
        sb.AppendLine($"LastPath={ExePath}");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }
}
