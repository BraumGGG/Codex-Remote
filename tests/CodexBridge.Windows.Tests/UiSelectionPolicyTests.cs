namespace CodexBridge.Windows.Tests;

public sealed class UiSelectionPolicyTests
{
    [Fact]
    public void SelectThreadButton_ChoosesSidebarItemInsteadOfDragLayer()
    {
        var dragLayer = Element("Button", "返回测试会话1", "cursor-grab active:cursor-grabbing");
        var actualButton = Element(
            "Button",
            "返回测试会话1",
            "group relative cursor-interaction sidebar-item");

        var result = UiSelectionPolicy.SelectThreadButton([dragLayer, actualButton], "返回测试会话1");

        Assert.Equal(actualButton, result);
    }

    [Fact]
    public void SelectThreadButton_DoesNotMatchArchiveButton()
    {
        var archive = Element("Button", "归档聊天", "sidebar-hover-icon-button-tint");

        Assert.Throws<InvalidOperationException>(
            () => UiSelectionPolicy.SelectThreadButton([archive], "返回测试会话1"));
    }

    [Fact]
    public void SelectThreadButton_RejectsMultipleActualButtons()
    {
        var first = Element("Button", "返回测试会话1", "sidebar-item");
        var second = first with { Left = 200 };

        Assert.Throws<InvalidOperationException>(
            () => UiSelectionPolicy.SelectThreadButton([first, second], "返回测试会话1"));
    }

    [Fact]
    public void SelectThreadButton_RejectsPrefixMatchAndOffscreenExactMatch()
    {
        var prefix = Element("Button", "返回测试会话1-copy", "sidebar-item");
        var offscreen = Element("Button", "返回测试会话1", "sidebar-item") with
        {
            IsOffscreen = true,
        };

        Assert.Throws<InvalidOperationException>(
            () => UiSelectionPolicy.SelectThreadButton([prefix, offscreen], "返回测试会话1"));
    }

    [Fact]
    public void SelectComposer_RequiresUniqueVisibleProseMirrorEdit()
    {
        var composer = Element("Edit", "随心输入", "ProseMirror ProseMirror-focused");

        var result = UiSelectionPolicy.SelectComposer([composer]);

        Assert.Equal(composer, result);
    }

    [Fact]
    public void SelectComposer_RejectsMultipleVisibleEditors()
    {
        var first = Element("Edit", "随心输入", "ProseMirror");
        var second = first with { Left = 300 };

        Assert.Throws<InvalidOperationException>(
            () => UiSelectionPolicy.SelectComposer([first, second]));
    }

    [Theory]
    [InlineData("Document", "随心输入", "ProseMirror")]
    [InlineData("Edit", "输入", "ProseMirror")]
    [InlineData("Edit", "随心输入", "other-editor")]
    public void SelectComposer_RejectsSimilarControls(string type, string name, string className)
    {
        Assert.Throws<InvalidOperationException>(
            () => UiSelectionPolicy.SelectComposer([Element(type, name, className)]));
    }

    private static UiElementSnapshot Element(string type, string name, string className) => new(
        0,
        type,
        name,
        string.Empty,
        className,
        IsEnabled: true,
        IsOffscreen: false,
        Left: 100,
        Top: 100,
        Width: 100,
        Height: 30);
}
