namespace CodexBridge.App.Tests;

public sealed class PairingExpiryPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreshCode_ShowsFiveMinuteCountdownWithoutWarning()
    {
        var result = MainForm.FormatPairingExpiry(Now.AddMinutes(5), Now);

        Assert.Equal("有效期 05:00", result.Text);
        Assert.False(result.Warning);
        Assert.False(result.Expired);
    }

    [Fact]
    public void FinalMinute_UsesWarningState()
    {
        var result = MainForm.FormatPairingExpiry(Now.AddSeconds(59.2), Now);

        Assert.Equal("有效期 01:00", result.Text);
        Assert.True(result.Warning);
        Assert.False(result.Expired);
    }

    [Fact]
    public void ExpiredCode_ShowsZeroAndRequestsReplacement()
    {
        var result = MainForm.FormatPairingExpiry(Now, Now);

        Assert.Equal("有效期 00:00", result.Text);
        Assert.False(result.Warning);
        Assert.True(result.Expired);
    }
}
