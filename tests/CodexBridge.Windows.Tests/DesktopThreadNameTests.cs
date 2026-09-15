namespace CodexBridge.Windows.Tests;

public sealed class DesktopThreadNameTests
{
    [Theory]
    [InlineData("新建会话", "返回新建会话")]
    [InlineData("同名会话", "返回同名会话")]
    [InlineData("Codex 任务 42", "返回Codex 任务 42")]
    [InlineData("返回测试会话2", "返回测试会话2")]
    [InlineData("返回：测试会话2", "返回测试会话2")]
    [InlineData("返回: 测试会话2", "返回测试会话2")]
    public void FromDatabaseTitle_BuildsExactAccessibleName(string title, string expected)
    {
        Assert.Equal(expected, DesktopThreadName.FromDatabaseTitle(title));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("line\nbreak")]
    public void FromDatabaseTitle_RejectsAmbiguousOrUnsafeTitle(string title)
    {
        Assert.Throws<DesktopVersionUnsupportedException>(
            () => DesktopThreadName.FromDatabaseTitle(title));
    }

    [Fact]
    public void FromDatabaseTitle_RejectsAbnormallyLongTitle()
    {
        Assert.Throws<DesktopVersionUnsupportedException>(
            () => DesktopThreadName.FromDatabaseTitle(new string('x', 513)));
    }
}
