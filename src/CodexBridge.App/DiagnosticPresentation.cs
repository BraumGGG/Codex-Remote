using CodexBridge.Host.Diagnostics;

namespace CodexBridge.App;

internal sealed record DiagnosticPresentation(
    string Status,
    string Problem,
    string ErrorCode,
    string Action,
    Color StatusColor)
{
    public static DiagnosticPresentation From(string component, BridgeComponentHealth health)
    {
        if (health.State == BridgeComponentState.Online)
            return new("正常", HealthyDescription(component), "无异常", "无需处理", UiTheme.Success);
        if (health.State == BridgeComponentState.Starting)
            return new(
                "连接中",
                component == "Pairing" ? "手机已扫码，等待配对请求" : "服务正在初始化",
                "无异常",
                "请稍候",
                UiTheme.Warning);
        if (health.State == BridgeComponentState.Disabled)
            return new("未启用", "远程服务当前未启用", "无异常", "在总览中启动 Host", UiTheme.Muted);

        return new(
            health.State == BridgeComponentState.CircuitOpen ? "已暂停" : "异常",
            ProblemDescription(component, health.ErrorCode),
            health.ErrorCode ?? "未记录错误码",
            SuggestedAction(component, health.ErrorCode),
            UiTheme.Danger);
    }

    private static string HealthyDescription(string component) => component switch
    {
        "Host" => "本机服务运行正常",
        "Signal" => "已连接公网信令服务",
        "TURN" => "中继凭据获取正常",
        "Sidecar" => "加密传输组件正常",
        "Pairing" => "等待手机扫码",
        "Codex Desktop" => "Codex Desktop 已运行",
        _ => "运行正常",
    };

    private static string ProblemDescription(string component, string? code) => code switch
    {
        "desktop_unavailable" => "未检测到 Codex Desktop",
        "turn_refresh_failed" => "无法获取网络中继凭据",
        "sidecar_circuit_open" => "传输组件连续失败后已暂停",
        "sidecar_stopped" => "传输组件未运行",
        "sidecar_transport_integrity_signature_invalid" => "当前版本错误地要求代码签名",
        "ice_failed" or "sidecar_ice_failed" => "设备间网络通道建立失败",
        "ice_disconnected" or "sidecar_ice_disconnected" => "设备间网络通道已断开",
        "signal_connection_failed" => "无法连接公网信令服务",
        "signal_io_error" => "公网信令连接中断",
        "signal_protocol_invalid" => "信令服务响应不兼容",
        "pairing_client_timeout" => "手机已扫码但未发起配对",
        "pairing_invalid" => "配对二维码无效",
        "pairing_expired" => "配对二维码已过期",
        "pairing_used" => "配对二维码已使用",
        "pairing_failed" => "配对处理失败",
        "pairing_signal_unavailable" => "配对服务未连接",
        _ when component == "Signal" => "公网信令服务不可用",
        _ when component == "TURN" => "网络中继服务不可用",
        _ when component == "Sidecar" => "加密传输组件不可用",
        _ => "组件当前不可用",
    };

    private static string SuggestedAction(string component, string? code) => code switch
    {
        "desktop_unavailable" => "启动 Codex Desktop 后重新检测",
        "turn_refresh_failed" => "检查网络，仍失败请联系服务支持",
        "sidecar_circuit_open" => "重启 Host；仍失败请重新安装客户端",
        "sidecar_stopped" => "重启 Host",
        "sidecar_transport_integrity_signature_invalid" => "安装支持无证书发行的最新 Windows 客户端",
        "signal_connection_failed" or "signal_io_error" => "检查网络后重新检测",
        "signal_protocol_invalid" => "更新 Windows 客户端",
        "pairing_client_timeout" or "pairing_invalid" or "pairing_expired" or "pairing_used" => "在手机端重新扫码",
        "pairing_failed" => "在手机端重新扫码；仍失败请联系服务支持",
        "pairing_signal_unavailable" => "检查 Signal 状态后重新检测",
        _ when component == "Sidecar" => "重启 Host；仍失败请重新安装客户端",
        _ => "重新检测；仍失败请联系服务支持",
    };
}
