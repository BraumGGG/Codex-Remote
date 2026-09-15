namespace CodexBridge.Host.Contracts;

public sealed record ProjectDto(
    string Id,
    string Name,
    int ThreadCount,
    long LastActivityAtMs,
    int RunningThreadCount,
    string IndexState,
    bool CanSend = true);

public sealed record ThreadDto(
    string Id,
    string Title,
    string Preview,
    long UpdatedAtMs,
    string Status,
    string DisplayName = "");

public sealed record ThreadPageDto(
    IReadOnlyList<ThreadDto> Items,
    string? NextCursor,
    bool HasMore,
    int TotalApproximate);

public sealed record ConversationEventDto(
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

public sealed record ConversationEventPageDto(
    IReadOnlyList<ConversationEventDto> Items,
    string? PreviousCursor,
    bool HasMoreBefore,
    long LatestSequence);

public sealed record ImageAttachmentDto(string Id);

public sealed record TextFileAttachmentDto(string Id, string Name, string Kind);

public sealed record TextFilePreviewDto(string Name, string Kind, string Content);

public sealed record EventTextContentDto(string Content, int Utf8Length);

public sealed record StatusResponse(
    string HostVersion,
    bool DesktopOnline);
