namespace CodexBridge.Host;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var lease = SingleInstanceLease.Acquire(@"Local\CodexBridge.Host");
        var app = HostApplication.Build(args);
        Console.WriteLine("Codex Bridge 本机管理 Host 已启动；手机连接仅使用公网加密通道。");
        await app.RunAsync();
    }
}
