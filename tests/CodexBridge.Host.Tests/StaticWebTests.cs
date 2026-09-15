using System.Net;

namespace CodexBridge.Host.Tests;

public sealed class StaticWebTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/app.css")]
    [InlineData("/app.js")]
    [InlineData("/transport.js")]
    [InlineData("/remote-transport.js")]
    [InlineData("/remote-frame-codec.js")]
    [InlineData("/remote-key-store.js")]
    [InlineData("/remote-peer-connector.js")]
    [InlineData("/signal-auth.js")]
    [InlineData("/config.js")]
    [InlineData("/vendor/marked.min.js")]
    [InlineData("/vendor/purify.min.js")]
    public async Task StaticAssets_AreServed(string path)
    {
        await using var host = await RunningHost.StartAsync();

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void WebSource_DoesNotContainConversationManagementActions()
    {
        var root = FindRepositoryRoot();
        var source = string.Join('\n',
            Directory.GetFiles(
                    Path.Combine(root, "src", "CodexBridge.Host", "wwwroot"),
                    "*.*",
                    SearchOption.AllDirectories)
                .Where(path => !string.Equals(Path.GetFileName(path), "design-prototype.html", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

        foreach (var forbidden in new[]
                 {
                     "删除会话",
                     "归档会话",
                     "delete-thread",
                     "archive-thread",
                     "rename-thread",
                 })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WebSource_DeduplicatesSequencedEventsAndLoadsAuthenticatedImages()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(
            Path.Combine(root, "src", "CodexBridge.Host", "wwwroot", "app.js"));
        var config = File.ReadAllText(
            Path.Combine(root, "src", "CodexBridge.Host", "wwwroot", "config.js"));
        var remotePeerConnector = File.ReadAllText(
            Path.Combine(root, "src", "CodexBridge.Host", "wwwroot", "remote-peer-connector.js"));

        Assert.Contains("sequence <= state.lastSequence", script, StringComparison.Ordinal);
        Assert.Contains("normalizeMessageText", script, StringComparison.Ordinal);
        Assert.Contains("loadThreadPage(project", script, StringComparison.Ordinal);
        Assert.Contains("transport.getEvents(threadId, state.historyCursor)", script, StringComparison.Ordinal);
        Assert.Contains("transport.getEventText(", script, StringComparison.Ordinal);
        Assert.Contains("HISTORY_WINDOW_SIZE = 120", script, StringComparison.Ordinal);
        Assert.DoesNotContain("transport.getEvents(threadId, state.lastSequence)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("item.kind === \"TaskCompleted\" && state.pollTimer", script, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("EventSource", script, StringComparison.Ordinal);
        Assert.DoesNotContain("LanTransport", config, StringComparison.Ordinal);
        Assert.DoesNotContain("lan-transport", config, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("iceTransportPolicy: forceRelay ? \"relay\" : \"all\"", remotePeerConnector, StringComparison.Ordinal);
        Assert.Contains("client-telemetry", remotePeerConnector, StringComparison.Ordinal);
        var remoteTransport = File.ReadAllText(
            Path.Combine(root, "src", "CodexBridge.Host", "wwwroot", "remote-transport.js"));
        Assert.Contains("channel_closed_before_open", remoteTransport, StringComparison.Ordinal);
        Assert.Contains("错误码：", script, StringComparison.Ordinal);
        Assert.Contains("transport.addEventListener(\"statechange\", handleRemoteStateChange)", script, StringComparison.Ordinal);
        Assert.Contains("retryRemoteConnection", script, StringComparison.Ordinal);
        Assert.Contains("showRemoteConnecting()", script, StringComparison.Ordinal);
        Assert.Contains("URL.createObjectURL", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LanTransport_IsNotPublished()
    {
        await using var host = await RunningHost.StartAsync();

        var response = await host.Client.GetAsync("/lan-transport.js");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void DefaultHost_OnlyListensOnLoopback()
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_BRIDGE_LISTEN_URL");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_BRIDGE_LISTEN_URL", null);
            Assert.Equal("http://127.0.0.1:5096", HostOptions.CreateDefault().ListenUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_BRIDGE_LISTEN_URL", previous);
        }
    }

    [Fact]
    public void WebSource_UsesLocalSanitizedReadOnlyFilePreview()
    {
        var root = FindRepositoryRoot();
        var webRoot = Path.Combine(root, "src", "CodexBridge.Host", "wwwroot");
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var script = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.Contains("vendor/marked.min.js", html, StringComparison.Ordinal);
        Assert.Contains("vendor/purify.min.js", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"关闭文件预览\"", html, StringComparison.Ordinal);
        Assert.Contains("DOMPurify.sanitize", script, StringComparison.Ordinal);
        Assert.Contains("RETURN_DOM_FRAGMENT", script, StringComparison.Ordinal);
        Assert.Contains("openFilePreview", script, StringComparison.Ordinal);
        Assert.DoesNotContain("https://cdn", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("download", script, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodexBridge.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("找不到解决方案根目录。");
    }
}
