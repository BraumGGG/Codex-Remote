namespace CodexBridge.Host.Auth;

public sealed record DevicePrincipal(string DeviceId, string DisplayName, bool CanSend)
{
    public const string ContextKey = "CodexBridge.DevicePrincipal";

    public static DevicePrincipal Require(HttpContext context) =>
        context.Items.TryGetValue(ContextKey, out var value) && value is DevicePrincipal principal
            ? principal
            : throw new InvalidOperationException("请求缺少已认证设备上下文。");
}
