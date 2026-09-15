using CodexBridge.Host;
using CodexRemote.UI;

namespace CodexBridge.App;

internal static class Program
{
    private static void BootTrace(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "codexbridge-live.log");
            File.AppendAllText(path, $"{DateTime.Now:O} program.{message}{Environment.NewLine}");
        }
        catch { }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        BootTrace("begin");
        // 设计稿以 100% / 125% / 150% DPI 为基准，使用 PerMonitorV2
        // 让 WinForms 在显示器切换和系统缩放变化时重新布局，而不是把整窗体位图拉伸。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        BootTrace("dpi");
        ApplicationConfiguration.Initialize();
        BootTrace("config");
        ToolStripManager.Renderer = new ModernToolStripRenderer();
        BootTrace("renderer");
        ProductEnvironment.Apply();
        BootTrace("environment");
        ConfigureProcessLogging();
        BootTrace("logging");
        try
        {
            BootTrace("before-lease");
            using var lease = SingleInstanceLease.Acquire(@"Local\CodexBridge.Host");
            BootTrace("lease-acquired");
            using var context = new BridgeApplicationContext(
                args.Contains("--background", StringComparer.OrdinalIgnoreCase));
            BootTrace("context-created");
            Application.Run(context);
            BootTrace("run-returned");
        }
        catch (InvalidOperationException exception)
        {
            BootTrace("invalid-operation " + exception.Message);
            MessageBox.Show(
                exception.Message,
                "Codex Bridge",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            BootTrace("fatal " + exception);
            try
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "startup-error.log"),
                    exception.ToString(),
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
            MessageBox.Show(
                exception.Message,
                "Codex Bridge 启动失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void ConfigureProcessLogging()
    {
        try
        {
            var directory = AppContext.BaseDirectory;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "codexbridge-live.log");
            // WinExe 没有可靠的控制台句柄；重定向 Console.Out/Err 可能阻塞启动。
            // 这里只确保诊断文件存在，启动过程不再替换全局输出流。
            if (!File.Exists(path)) File.WriteAllText(path, string.Empty, new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // Logging must never prevent the tray app from starting.
        }
    }
}
