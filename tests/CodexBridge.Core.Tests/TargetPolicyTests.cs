using CodexBridge.Core;

namespace CodexBridge.Core.Tests;

public sealed class TargetPolicyTests
{
    private readonly TargetPolicy _policy = new();

    [Fact]
    public void BridgeAction_ExposesOnlyViewAndSend()
    {
        Assert.Equal(["View", "Send"], Enum.GetNames<BridgeAction>());
    }

    [Theory]
    [InlineData(TargetPolicy.AllowedProjectPath)]
    [InlineData(@"\\?\D:\claudecode\cchaha\Project\测试会话")]
    [InlineData(@"d:\CLAUDECODE\cchaha\Project\测试会话\")]
    public void AssertCanView_AcceptsOnlyNormalizedAllowedProject(string projectPath)
    {
        var result = _policy.AssertCanView(projectPath);

        Assert.Equal(
            TargetPolicy.NormalizePath(TargetPolicy.AllowedProjectPath),
            result,
            StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"D:\claudecode\cchaha\Project")]
    [InlineData(@"D:\claudecode\cchaha\Project\测试会话2")]
    [InlineData(@"D:\claudecode\cchaha\Project\测试会话\child")]
    public void AssertCanView_RejectsOtherProjects(string projectPath)
    {
        Assert.Throws<UnauthorizedAccessException>(() => _policy.AssertCanView(projectPath));
    }

    [Theory]
    [InlineData("01a00749-2fb0-7fa0-9186-4f8292732f2c")]
    [InlineData("01a00749-6d7c-7072-9b22-f4a70ea35331")]
    public void AssertCanSend_AcceptsAllowedThreads(string threadId)
    {
        var result = _policy.AssertCanSend(TargetPolicy.AllowedProjectPath, threadId);

        Assert.Equal(TargetPolicy.NormalizePath(TargetPolicy.AllowedProjectPath), result);
    }

    [Fact]
    public void AssertCanSend_RejectsBlankThreadAndSimilarProject()
    {
        Assert.Throws<UnauthorizedAccessException>(
            () => _policy.AssertCanSend(TargetPolicy.AllowedProjectPath, ""));
        Assert.Throws<UnauthorizedAccessException>(
            () => _policy.AssertCanSend(TargetPolicy.AllowedProjectPath + "-similar", "new-thread"));
    }

    [Fact]
    public void ConfiguredPolicy_AcceptsEachExactProjectAndRejectsSimilarPaths()
    {
        var first = TestPaths.PathFor("policy-one");
        var second = TestPaths.PathFor("policy-two");
        var policy = new TargetPolicy([first, second]);

        Assert.Equal(TargetPolicy.NormalizePath(first), policy.AssertCanView(first));
        Assert.Equal(TargetPolicy.NormalizePath(second), policy.AssertCanView(second));
        Assert.Throws<UnauthorizedAccessException>(() => policy.AssertCanView(first + "-similar"));
        Assert.Equal(2, policy.GetAllowedProjectPaths().Count);
    }

    [Fact]
    public void ConfiguredPolicy_AuthorizesSendByExactProjectInsteadOfPublishedThreadList()
    {
        var project = TestPaths.PathFor("dynamic-send");
        var policy = new TargetPolicy([project]);

        Assert.Equal(
            TargetPolicy.NormalizePath(project),
            policy.AssertCanSend(project, "new-thread-created-after-install"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            policy.AssertCanSend(project + "-other", "new-thread-created-after-install"));
    }
}
