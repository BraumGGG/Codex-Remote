using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CodexBridge.Host.Auth;
using CodexBridge.Host.Contracts;
using CodexBridge.Host.Remote;
using CodexBridge.Remote.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace CodexBridge.Host.Tests;

public sealed class DeviceManagementApiTests
{
    [Fact]
    public async Task ManagementApi_ListsRedactedRemoteDevicesRejectsReplayAndRevokes()
    {
        RemoteDeviceStore? remoteStore = null;
        DevicePrincipal? remotePrincipal = null;
        await using var host = await RunningHost.StartAsync(
            setupData: directory =>
            {
                WorkspaceApiTests.CreateWorkspaceData(directory);
                remoteStore = new RemoteDeviceStore(
                    Path.Combine(directory, "remote-devices.json"),
                    TimeProvider.System);
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                remotePrincipal = remoteStore.Register(
                    "Fake Remote Phone",
                    RemoteEncoding.Base64UrlEncode(key.ExportSubjectPublicKeyInfo()),
                    false);
            },
            configureServices: services => services.AddSingleton(remoteStore!));

        var unauthenticated = await host.Client.GetAsync("/api/management/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var listRequest = ManagementRequest(
            HttpMethod.Get,
            "/api/management/devices",
            host.ManagementToken,
            "management-nonce-list-0001");
        var listResponse = await host.Client.SendAsync(listRequest);
        listResponse.EnsureSuccessStatusCode();
        var json = await listResponse.Content.ReadAsStringAsync();
        var devices = JsonSerializer.Deserialize<ManagedDeviceDto[]>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(devices);
        var device = Assert.Single(devices);
        Assert.Equal("remote", device.Transport);
        Assert.False(device.CanSend);
        Assert.DoesNotContain("Token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PublicKey", json, StringComparison.OrdinalIgnoreCase);

        using var diagnosticsRequest = ManagementRequest(
            HttpMethod.Get,
            "/api/management/diagnostics",
            host.ManagementToken,
            "management-nonce-diagnostics");
        var diagnosticsResponse = await host.Client.SendAsync(diagnosticsRequest);
        diagnosticsResponse.EnsureSuccessStatusCode();
        var diagnosticsJson = await diagnosticsResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"host\"", diagnosticsJson, StringComparison.Ordinal);
        Assert.Contains("\"signal\"", diagnosticsJson, StringComparison.Ordinal);
        Assert.Contains("Disabled", diagnosticsJson, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "token", "secret", "credential", "sdp", "project", "thread",
                     "message", "path", "deviceid",
                 })
        {
            Assert.DoesNotContain(forbidden, diagnosticsJson, StringComparison.OrdinalIgnoreCase);
        }

        using var replay = ManagementRequest(
            HttpMethod.Get,
            "/api/management/devices",
            host.ManagementToken,
            "management-nonce-list-0001");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(replay)).StatusCode);

        using var revokeRemote = ManagementRequest(
            HttpMethod.Delete,
            $"/api/management/devices/remote/{remotePrincipal!.DeviceId}",
            host.ManagementToken,
            "management-nonce-revoke-remote");
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(revokeRemote)).StatusCode);
        Assert.Null(remoteStore!.Authenticate(remotePrincipal.DeviceId));

        using var rejectLegacyTransport = ManagementRequest(
            HttpMethod.Delete,
            "/api/management/devices/lan/legacy-device",
            host.ManagementToken,
            "management-nonce-reject-lan");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.SendAsync(rejectLegacyTransport)).StatusCode);

        using var revokeAgain = ManagementRequest(
            HttpMethod.Delete,
            $"/api/management/devices/remote/{remotePrincipal.DeviceId}",
            host.ManagementToken,
            "management-nonce-revoke-again");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.SendAsync(revokeAgain)).StatusCode);

        Assert.DoesNotContain(
            Enum.GetNames<RpcMethod>(),
            method => method.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                      method.Contains("manage", StringComparison.OrdinalIgnoreCase) ||
                      method.Contains("revoke", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpRequestMessage ManagementRequest(
        HttpMethod method,
        string path,
        string token,
        string nonce)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(ManagementAccessGuard.TokenHeader, token);
        request.Headers.Add(ManagementAccessGuard.NonceHeader, nonce);
        return request;
    }
}
