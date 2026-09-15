using CodexBridge.Entitlements;
using CodexBridge.Host.Auth;

namespace CodexBridge.Host.Remote;

public interface IRemoteCapabilityResolver
{
    DevicePrincipal Resolve(DevicePrincipal principal);
    EntitlementDecision Evaluate(string deviceId);
    bool Remove(string deviceId);
}

public sealed class RemoteEntitlementService(
    LocalEntitlementStore tokens,
    RemoteIdentityStore identities,
    EntitlementRefreshCredentialStore credentials,
    IEntitlementCloudClient cloud) : IRemoteCapabilityResolver
{
    private string? _hostId;
    private readonly HashSet<string> _revokedDevices = new(StringComparer.Ordinal);

    public void Initialize(string hostId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        Interlocked.CompareExchange(ref _hostId, hostId, null);
        if (!string.Equals(_hostId, hostId, StringComparison.Ordinal))
            throw new InvalidOperationException("Remote host identity changed during this process.");
    }

    public async Task<EntitlementDecision> InstallAsync(
        string token,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        using var identity = await identities.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        Initialize(identity.HostId);
        return tokens.Install(token, identity.HostId, deviceId);
    }

    public async Task<EntitlementDecision> RedeemAsync(
        string code,
        string userId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        using var identity = await identities.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        Initialize(identity.HostId);
        var issued = await cloud.RedeemAsync(
            code, userId, identity.HostId, deviceId, cancellationToken).ConfigureAwait(false);
        var decision = tokens.Install(issued.EntitlementToken, identity.HostId, deviceId);
        try
        {
            credentials.Put(new EntitlementRefreshCredential(
                issued.LicenseId, deviceId, issued.RefreshToken));
        }
        catch
        {
            tokens.Remove(deviceId);
            throw;
        }
        return decision;
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        using var identity = await identities.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        Initialize(identity.HostId);
        foreach (var credential in credentials.List())
        {
            try
            {
                var token = await cloud.RefreshAsync(
                    credential.LicenseId,
                    credential.RefreshToken,
                    identity.HostId,
                    credential.DeviceId,
                    cancellationToken).ConfigureAwait(false);
                tokens.Install(token, identity.HostId, credential.DeviceId);
            }
            catch (EntitlementCloudException exception) when (
                exception.ErrorCode is "license_revoked" or "license_expired" or "license_not_found" or "license_binding_mismatch")
            {
                tokens.Remove(credential.DeviceId);
                lock (_revokedDevices) _revokedDevices.Add(credential.DeviceId);
                credentials.Remove(credential.DeviceId);
            }
        }
    }

    public DevicePrincipal Resolve(DevicePrincipal principal)
    {
        var decision = Evaluate(principal.DeviceId);
        // A device permission granted by the host remains effective when no
        // entitlement token is installed. Explicit entitlement failures still
        // revoke sending capability.
        lock (_revokedDevices)
        {
            if (_revokedDevices.Contains(principal.DeviceId))
                return principal with { CanSend = false };
        }
        if (decision.State == EntitlementState.Free && decision.ErrorCode is null)
            return principal;
        return principal with { CanSend = decision.CanSend };
    }

    public EntitlementDecision Evaluate(string deviceId)
    {
        var hostId = Volatile.Read(ref _hostId);
        return hostId is null ? EntitlementDecision.Free("entitlement_host_unavailable") : tokens.Evaluate(hostId, deviceId);
    }

    public bool Remove(string deviceId)
    {
        var removedToken = tokens.Remove(deviceId);
        var removedCredential = credentials.Remove(deviceId);
        return removedToken || removedCredential;
    }
}

public sealed class EntitlementRefreshHostedService(
    RemoteAccessOptions options,
    RemoteEntitlementService entitlements,
    ILogger<EntitlementRefreshHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.EntitlementServiceUri is null) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await entitlements.RefreshAllAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning("Entitlement refresh failed: {ErrorType}", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
        }
    }
}
