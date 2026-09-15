using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodexBridge.Host.Remote;

public sealed record CloudLicenseCredential(string LicenseId, string RefreshToken, string EntitlementToken);

public sealed class EntitlementCloudException(string errorCode, HttpStatusCode? statusCode = null) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public interface IEntitlementCloudClient
{
    Task<CloudLicenseCredential> RedeemAsync(
        string code, string userId, string hostId, string deviceId, CancellationToken cancellationToken);
    Task<string> RefreshAsync(
        string licenseId, string refreshToken, string hostId, string deviceId, CancellationToken cancellationToken);
}

public sealed class DisabledEntitlementCloudClient : IEntitlementCloudClient
{
    public Task<CloudLicenseCredential> RedeemAsync(
        string code, string userId, string hostId, string deviceId, CancellationToken cancellationToken) =>
        Task.FromException<CloudLicenseCredential>(new EntitlementCloudException("entitlement_cloud_unconfigured"));
    public Task<string> RefreshAsync(
        string licenseId, string refreshToken, string hostId, string deviceId, CancellationToken cancellationToken) =>
        Task.FromException<string>(new EntitlementCloudException("entitlement_cloud_unconfigured"));
}

public sealed class EntitlementCloudClient(HttpClient httpClient) : IEntitlementCloudClient
{
    public async Task<CloudLicenseCredential> RedeemAsync(
        string code, string userId, string hostId, string deviceId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/v1/redeem", new { code, userId, hostId, deviceId }, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CloudLicenseCredential>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RefreshAsync(
        string licenseId, string refreshToken, string hostId, string deviceId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/v1/token", new { licenseId, refreshToken, hostId, deviceId }, cancellationToken).ConfigureAwait(false);
        var result = await ReadAsync<RefreshResponse>(response, cancellationToken).ConfigureAwait(false);
        return result.EntitlementToken;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 16 * 1024)
            throw new EntitlementCloudException("entitlement_response_too_large", response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            string errorCode = "entitlement_cloud_error";
            try
            {
                var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(error?.Error)) errorCode = error.Error;
            }
            catch (JsonException) { }
            throw new EntitlementCloudException(errorCode, response.StatusCode);
        }
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
                ?? throw new EntitlementCloudException("entitlement_response_invalid", response.StatusCode);
        }
        catch (JsonException exception)
        {
            throw new EntitlementCloudException("entitlement_response_invalid", response.StatusCode) { Source = exception.Source };
        }
    }

    private sealed record RefreshResponse(string EntitlementToken);
    private sealed record ErrorResponse(string Error);
}
