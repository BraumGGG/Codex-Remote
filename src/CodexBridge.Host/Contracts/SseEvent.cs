namespace CodexBridge.Host.Contracts;

public sealed record SseEvent(
    long Sequence,
    string Kind,
    DateTimeOffset? Timestamp,
    string? Text,
    string? TurnId,
    IReadOnlyList<ImageAttachmentDto> Images,
    IReadOnlyList<TextFileAttachmentDto> Files,
    string? TextPreview = null,
    string? TextContentId = null,
    int TextLength = 0);

public sealed record StreamTicketResponse(string Ticket, DateTimeOffset ExpiresAt);
