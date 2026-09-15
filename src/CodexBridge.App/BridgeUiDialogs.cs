namespace CodexBridge.App;

public interface IBridgeUiDialogs
{
    string? SelectProject(IWin32Window owner);
    bool Confirm(IWin32Window owner, string message, string title);
    void ShowError(IWin32Window owner, string message);
}

internal sealed class WinFormsBridgeUiDialogs : IBridgeUiDialogs
{
    public string? SelectProject(IWin32Window owner)
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "选择允许 Codex Bridge 读取会话的项目根目录",
            UseDescriptionForTitle = true,
        };
        return picker.ShowDialog(owner) == DialogResult.OK ? picker.SelectedPath : null;
    }

    public bool Confirm(IWin32Window owner, string message, string title) =>
        MessageBox.Show(owner, message, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;

    public void ShowError(IWin32Window owner, string message) =>
        MessageBox.Show(owner, message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
