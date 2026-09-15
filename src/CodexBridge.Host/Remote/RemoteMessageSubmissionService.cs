using CodexBridge.Host.Auth;
using CodexBridge.Host.Services;
using System.Security.Cryptography;
using System.Text;

namespace CodexBridge.Host.Remote;

public sealed class RemoteMessageSubmissionService(
    RemoteCommandReceiptStore receipts,
    MessageSubmissionService submissions)
{
    public async Task<RemoteCommandReceipt> SubmitOnceAsync(
        DevicePrincipal principal,
        Guid commandId,
        string threadId,
        string? text,
        CancellationToken cancellationToken = default)
    {
        await submissions.ValidateAsync(principal, threadId, text, cancellationToken).ConfigureAwait(false);
        var begin = await receipts.BeginAsync(
            commandId,
            principal.DeviceId,
            threadId,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text!))).ToLowerInvariant(),
            cancellationToken).ConfigureAwait(false);
        if (!begin.IsNew) return begin.Receipt;

        try
        {
            await submissions.SubmitAsync(principal, threadId, text, cancellationToken).ConfigureAwait(false);
            return await receipts.CompleteAsync(commandId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await receipts.MarkUnknownAsync(commandId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (MessageSubmissionException exception)
        {
            await receipts.RejectAsync(commandId, exception.ErrorCode, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch
        {
            await receipts.MarkUnknownAsync(commandId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
