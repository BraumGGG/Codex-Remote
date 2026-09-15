namespace CodexBridge.Host.Contracts;

public sealed record MessageRequest(string? Text);

public sealed record MessageAcceptedResponse(
    string SubmissionId,
    string ThreadId,
    DateTimeOffset AcceptedAt);
