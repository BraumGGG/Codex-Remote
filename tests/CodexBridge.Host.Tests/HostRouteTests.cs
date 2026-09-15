using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using CodexBridge.Host;

namespace CodexBridge.Host.Tests;

public sealed class HostRouteTests
{
    [Fact]
    public void Build_MapsOnlyExplicitSafeApiRoutes()
    {
        var directory = TestPaths.CreateDirectory("host-routes");
        try
        {
            var options = new HostOptions(
                "http://127.0.0.1:0",
                Path.Combine(directory, "state.sqlite"),
                Path.Combine(directory, "audit.jsonl"),
                Path.Combine(directory, "bridge-config.json"));
            using var app = HostApplication.Build([], options);
            var routes = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText)
                .Where(route => route?.StartsWith("/api/", StringComparison.Ordinal) == true)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(
                [
                    "/api/management/devices",
                    "/api/management/devices/remote/{deviceId}/entitlement",
                    "/api/management/devices/remote/{deviceId}/redeem",
                    "/api/management/devices/{transport}/{deviceId}",
                    "/api/management/devices/{transport}/{deviceId}/permissions",
                    "/api/management/diagnostics",
                    "/api/management/pairing",
                    "/api/management/project-discovery",
                    "/api/management/projects",
                    "/api/status",
                ],
                routes.Order(StringComparer.Ordinal));

            Assert.DoesNotContain(routes, route => ContainsDangerousAction(route));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("http://0.0.0.0:5096")]
    [InlineData("http://192.168.1.20:5096")]
    [InlineData("https://127.0.0.1:5096")]
    public void Build_RejectsLanOrTlsListenAddresses(string listenUrl)
    {
        var directory = TestPaths.CreateDirectory("host-routes-loopback");
        try
        {
            var options = new HostOptions(
                listenUrl,
                Path.Combine(directory, "state.sqlite"),
                Path.Combine(directory, "audit.jsonl"),
                Path.Combine(directory, "bridge-config.json"));
            Assert.Throws<InvalidOperationException>(() => HostApplication.Build([], options));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool ContainsDangerousAction(string route) =>
        route.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
        route.Contains("archive", StringComparison.OrdinalIgnoreCase) ||
        route.Contains("rename", StringComparison.OrdinalIgnoreCase) ||
        route.Contains("pin", StringComparison.OrdinalIgnoreCase) ||
        route.Contains("rpc", StringComparison.OrdinalIgnoreCase);
}
