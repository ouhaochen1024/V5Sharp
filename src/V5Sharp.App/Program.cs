using System.Text;

namespace V5Sharp.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private static void Report(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "v5sharp_crash.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch { /* 忽略 */ }

        try
        {
            MessageBox.Show(ex.Message, "V5Sharp 出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { /* 忽略 */ }
    }
}
