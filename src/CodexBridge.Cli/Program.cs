using CodexBridge.Core;
using CodexBridge.Windows;
using System.Text.Json;

return await BridgeCli.RunAsync(args);

internal static class BridgeCli
{
    private static readonly string StateDatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "state_5.sqlite");

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            var policy = new TargetPolicy();
            var catalog = new SqliteThreadCatalog(StateDatabasePath, policy);

            return args[0] switch
            {
                "list" => await ListAsync(args, catalog),
                "show" => await ShowAsync(args, catalog),
                "inspect-ui" => InspectUi(),
                "inspect-ui-summary" => InspectUiSummary(),
                "select-thread" => await SelectThreadAsync(args, catalog),
                "send" => await SendAsync(args, catalog, policy),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"拒绝：{exception.Message}");
            return 3;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("操作已取消。");
            return 4;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"失败：{exception.Message}");
            return 1;
        }
    }

    private static async Task<int> ListAsync(string[] args, IThreadCatalog catalog)
    {
        var projectPath = GetRequiredOption(args, "--project");
        var threads = await catalog.ListByProjectAsync(projectPath);

        foreach (var thread in threads)
        {
            Console.WriteLine($"{thread.Id}\t{thread.Title}\t{thread.ModelProvider}\t{thread.UpdatedAtMs}");
        }

        return 0;
    }

    private static async Task<int> ShowAsync(string[] args, IThreadCatalog catalog)
    {
        var threadId = GetRequiredOption(args, "--thread");
        var follow = args.Contains("--follow", StringComparer.Ordinal);
        var thread = await catalog.GetAsync(threadId)
            ?? throw new InvalidOperationException($"找不到未归档会话：{threadId}");
        var reader = new RolloutConversationReader();

        await foreach (var item in reader.ReadAsync(thread.RolloutPath, follow))
        {
            if (item.Kind == ConversationEventKind.Unknown)
            {
                continue;
            }

            var timestamp = item.Timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
            var text = item.Text is null ? string.Empty : $"\t{item.Text.ReplaceLineEndings("\\n")}";
            Console.WriteLine($"{timestamp}\t{item.Kind}\t{item.TurnId ?? "-"}{text}");
        }

        return 0;
    }

    private static string GetRequiredOption(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"缺少参数 {option}");
        }

        return args[index + 1];
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        PrintUsage();
        return 2;
    }

    private static int InspectUi()
    {
        var snapshot = new CodexUiProbe().Capture();
        Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        return 0;
    }

    private static int InspectUiSummary()
    {
        var snapshot = new CodexUiProbe().Capture();
        var keywords = new[]
        {
            "测试会话",
            "返回测试会话1",
            "返回测试会话2",
            "发送",
            "提交",
            "停止",
            "输入",
            "纯文本",
        };
        var rootDocument = snapshot.Elements.FirstOrDefault(element =>
            element.ControlType == "Document" && element.AutomationId == "RootWebArea");
        var composerTop = rootDocument is null
            ? double.MaxValue
            : rootDocument.Top + (rootDocument.Height * 0.68);
        var relevant = snapshot.Elements
            .Concat(snapshot.GlobalMatches)
            .Concat(snapshot.FragmentElements)
            .Where(element =>
                !element.IsOffscreen &&
                (element.ControlType is "Edit" or "Document" ||
                 (element.ControlType == "Button" && element.Top >= composerTop) ||
                 keywords.Any(keyword => element.Name.Contains(keyword, StringComparison.Ordinal))))
            .DistinctBy(element => new
            {
                element.ControlType,
                element.Name,
                element.AutomationId,
                element.ClassName,
                element.Left,
                element.Top,
                element.Width,
                element.Height,
            })
            .ToArray();

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            snapshot.Window,
            NativeChildren = snapshot.NativeChildren.Where(child =>
                child.ClassName.Contains("Chrome", StringComparison.OrdinalIgnoreCase)),
            RelevantElements = relevant,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task<int> SelectThreadAsync(string[] args, IThreadCatalog catalog)
    {
        var threadId = GetRequiredOption(args, "--thread");
        var thread = await catalog.GetAsync(threadId)
            ?? throw new InvalidOperationException($"找不到未归档会话：{threadId}");

        var policy = new TargetPolicy();
        policy.AssertCanSend(thread.Cwd, thread.Id);
        new CodexUiController().SelectThread(DesktopThreadName.FromDatabaseTitle(thread.Title));
        Console.WriteLine($"已切换到：{thread.Title} ({thread.Id})");
        return 0;
    }

    private static async Task<int> SendAsync(
        string[] args,
        IThreadCatalog catalog,
        TargetPolicy policy)
    {
        var threadId = GetRequiredOption(args, "--thread");
        var message = GetRequiredOption(args, "--message");
        var sender = new UiAutomationCodexDesktopSender(catalog, policy);
        var result = await sender.SendAsync(
            TargetPolicy.AllowedProjectPath,
            threadId,
            message);

        Console.WriteLine(
            $"已通过 Codex Desktop 发送到：{result.ThreadTitle} ({result.ThreadId})");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("用法：");
        Console.Error.WriteLine("  codex-bridge list --project <path>");
        Console.Error.WriteLine("  codex-bridge show --thread <id> [--follow]");
        Console.Error.WriteLine("  codex-bridge inspect-ui");
        Console.Error.WriteLine("  codex-bridge inspect-ui-summary");
        Console.Error.WriteLine("  codex-bridge select-thread --thread <id>");
        Console.Error.WriteLine("  codex-bridge send --thread <id> --message <单行纯文本>");
    }
}
