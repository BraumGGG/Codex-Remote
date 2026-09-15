using CodexBridge.Host.Diagnostics;

namespace CodexBridge.App.Tests;

public sealed class DiagnosticPresentationTests
{
    [Theory]
    [InlineData("Host", "本机服务运行正常")]
    [InlineData("Signal", "已连接公网信令服务")]
    [InlineData("TURN", "中继凭据获取正常")]
    [InlineData("Sidecar", "加密传输组件正常")]
    [InlineData("Pairing", "等待手机扫码")]
    [InlineData("Codex Desktop", "Codex Desktop 已运行")]
    public void OnlineComponent_ShowsNormalInsteadOfDash(string component, string description)
    {
        var result = DiagnosticPresentation.From(component, new BridgeComponentHealth(
            BridgeComponentState.Online,
            DateTimeOffset.UtcNow,
            null));

        Assert.Equal("正常", result.Status);
        Assert.Equal(description, result.Problem);
        Assert.Equal("无异常", result.ErrorCode);
        Assert.Equal("无需处理", result.Action);
    }

    [Fact]
    public void PairingStarting_ShowsPhoneArrivalInsteadOfGenericInitialization()
    {
        var result = DiagnosticPresentation.From("Pairing", new BridgeComponentHealth(
            BridgeComponentState.Starting,
            DateTimeOffset.UtcNow,
            null));

        Assert.Equal("连接中", result.Status);
        Assert.Equal("手机已扫码，等待配对请求", result.Problem);
        Assert.Equal("请稍候", result.Action);
    }

    [Theory]
    [InlineData("Signal", "signal_connection_failed", "无法连接公网信令服务", "检查网络后重新检测")]
    [InlineData("TURN", "turn_refresh_failed", "无法获取网络中继凭据", "检查网络，仍失败请联系服务支持")]
    [InlineData("Sidecar", "sidecar_circuit_open", "传输组件连续失败后已暂停", "重启 Host；仍失败请重新安装客户端")]
    [InlineData("Sidecar", "sidecar_transport_integrity_signature_invalid", "当前版本错误地要求代码签名", "安装支持无证书发行的最新 Windows 客户端")]
    [InlineData("Pairing", "pairing_client_timeout", "手机已扫码但未发起配对", "在手机端重新扫码")]
    [InlineData("Pairing", "pairing_failed", "配对处理失败", "在手机端重新扫码；仍失败请联系服务支持")]
    [InlineData("Codex Desktop", "desktop_unavailable", "未检测到 Codex Desktop", "启动 Codex Desktop 后重新检测")]
    public void OfflineComponent_ShowsProblemCodeAndAction(
        string component,
        string code,
        string problem,
        string action)
    {
        var result = DiagnosticPresentation.From(component, new BridgeComponentHealth(
            BridgeComponentState.Offline,
            DateTimeOffset.UtcNow,
            code));

        Assert.Equal("异常", result.Status);
        Assert.Equal(problem, result.Problem);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(action, result.Action);
    }
}
