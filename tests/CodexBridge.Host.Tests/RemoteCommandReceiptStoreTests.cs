using CodexBridge.Host.Remote;

namespace CodexBridge.Host.Tests;

public sealed class RemoteCommandReceiptStoreTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDirectory("remote-receipts");

    [Fact]
    public async Task Begin_PersistsAcceptedAndRestoresItAsUnknown()
    {
        var path = Path.Combine(_directory, "receipts.json");
        var commandId = Guid.NewGuid();
        var first = new RemoteCommandReceiptStore(path, TimeProvider.System);
        Assert.True((await first.BeginAsync(commandId, "device", "thread", Digest("first"))).IsNew);

        var restored = new RemoteCommandReceiptStore(path, TimeProvider.System);
        var duplicate = await restored.BeginAsync(commandId, "device", "thread", Digest("first"));

        Assert.False(duplicate.IsNew);
        Assert.Equal(RemoteCommandState.Unknown, duplicate.Receipt.State);
    }

    [Fact]
    public async Task Begin_SameIdWithDifferentBindingIsRejected()
    {
        var store = new RemoteCommandReceiptStore(Path.Combine(_directory, "conflict.json"), TimeProvider.System);
        var commandId = Guid.NewGuid();
        await store.BeginAsync(commandId, "device-a", "thread-a", Digest("first"));
        var conflict = await store.BeginAsync(commandId, "device-b", "thread-b", Digest("first"));
        Assert.Equal(RemoteCommandState.Rejected, conflict.Receipt.State);
        Assert.Equal("command_id_conflict", conflict.Receipt.ErrorCode);
    }

    [Fact]
    public async Task Begin_SameIdWithDifferentTextDigestIsRejectedWithoutStoringBody()
    {
        var path = Path.Combine(_directory, "text-conflict.json");
        var store = new RemoteCommandReceiptStore(path, TimeProvider.System);
        var commandId = Guid.NewGuid();
        await store.BeginAsync(commandId, "device", "thread", Digest("first secret"));

        var conflict = await store.BeginAsync(commandId, "device", "thread", Digest("changed secret"));

        Assert.Equal(RemoteCommandState.Rejected, conflict.Receipt.State);
        Assert.Equal("command_id_conflict", conflict.Receipt.ErrorCode);
        var persisted = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("first secret", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("changed secret", persisted, StringComparison.Ordinal);
    }

    private static string Digest(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
