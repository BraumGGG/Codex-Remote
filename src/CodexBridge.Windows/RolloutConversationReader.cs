using System.Runtime.CompilerServices;
using System.Text.Json;
using CodexBridge.Core;

namespace CodexBridge.Windows;

public sealed class RolloutConversationReader : IConversationReader
{
    private static readonly TimeSpan FollowPollInterval = TimeSpan.FromMilliseconds(200);

    public async IAsyncEnumerable<ConversationEvent> ReadAsync(
        string rolloutPath,
        bool follow,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in ReadFromOffsetAsync(rolloutPath, 0, follow, cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<ConversationEvent> ReadFromOffsetAsync(
        string rolloutPath,
        long offset,
        bool follow,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rolloutPath);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));

        await using var stream = new FileStream(
            rolloutPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (offset > stream.Length) throw new InvalidDataException("Rollout offset exceeds the file length.");
        stream.Position = offset;
        using var reader = new StreamReader(stream);

        string? pendingFragment = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);

            if (line is null)
            {
                if (!follow)
                {
                    yield break;
                }

                await Task.Delay(FollowPollInterval, cancellationToken);
                continue;
            }

            if (pendingFragment is not null)
            {
                line = pendingFragment + line;
                pendingFragment = null;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ConversationEvent? parsed;
            try
            {
                parsed = ParseVisibleEvent(line);
            }
            catch (JsonException) when (follow)
            {
                pendingFragment = line;
                continue;
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed is not null)
            {
                yield return parsed;
            }
        }
    }

    public static ConversationEvent? ParseVisibleEvent(string jsonLine)
    {
        using var document = JsonDocument.Parse(jsonLine);
        var root = document.RootElement;

        // response_item includes hidden developer/system context and must never reach the phone UI.
        if (!TryGetString(root, "type", out var outerType) || outerType != "event_msg")
        {
            return null;
        }

        if (!root.TryGetProperty("payload", out var payload) ||
            !TryGetString(payload, "type", out var payloadType))
        {
            return null;
        }

        DateTimeOffset? timestamp = null;
        if (TryGetString(root, "timestamp", out var timestampText) &&
            DateTimeOffset.TryParse(timestampText, out var parsedTimestamp))
        {
            timestamp = parsedTimestamp;
        }

        // Newer Codex rollouts wrap visible messages in item_completed. Keep
        // accepting the older direct event_msg payload format for existing files.
        JsonElement messagePayload = payload;
        string effectiveType = payloadType;
        if (payloadType == "item_completed" &&
            payload.TryGetProperty("item", out var item) &&
            item.ValueKind == JsonValueKind.Object &&
            TryGetString(item, "type", out var itemType))
        {
            messagePayload = item;
            effectiveType = itemType;
        }

        var kind = effectiveType switch
        {
            "user_message" => ConversationEventKind.UserMessage,
            "agent_message" => ConversationEventKind.AgentMessage,
            "UserMessage" => ConversationEventKind.UserMessage,
            "AgentMessage" => ConversationEventKind.AgentMessage,
            "task_started" => ConversationEventKind.TaskStarted,
            "task_complete" => ConversationEventKind.TaskCompleted,
            "token_count" => ConversationEventKind.TokenCount,
            _ => ConversationEventKind.Unknown,
        };

        var localImagePaths = kind == ConversationEventKind.UserMessage
            ? ReadStringArray(messagePayload, "local_images")
            : [];
        var text = kind is ConversationEventKind.UserMessage or ConversationEventKind.AgentMessage
            ? ReadMessageText(messagePayload)
            : null;
        if (kind == ConversationEventKind.UserMessage && text is not null)
        {
            text = SanitizeUserMessage(text, localImagePaths);
        }
        var turnId = TryGetString(messagePayload, "turn_id", out var id) ? id : null;

        return new ConversationEvent(kind, timestamp, text, turnId, payloadType, localImagePaths);
    }

    private static string? ReadMessageText(JsonElement payload)
    {
        if (TryGetString(payload, "message", out var message))
        {
            return message;
        }

        if (!payload.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = content.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item =>
                TryGetString(item, "text", out var text) || TryGetString(item, "Text", out text)
                    ? text
                    : null)
            .Where(text => !string.IsNullOrWhiteSpace(text));
        var result = string.Join("\n", parts);
        return result.Length == 0 ? null : result;
    }

    private static string SanitizeUserMessage(
        string message,
        IReadOnlyList<string> localImagePaths)
    {
        var trimmedMessage = message.TrimStart();
        if (localImagePaths.Count > 0 &&
            trimmedMessage.StartsWith("# Files mentioned by the user:", StringComparison.Ordinal))
        {
            const string requestMarker = "## My request:";
            var requestIndex = trimmedMessage.IndexOf(requestMarker, StringComparison.Ordinal);
            if (requestIndex >= 0)
            {
                return trimmedMessage[(requestIndex + requestMarker.Length)..].TrimStart();
            }
        }

        foreach (var path in localImagePaths)
        {
            message = message.Replace(path, "[图片]", StringComparison.OrdinalIgnoreCase);
            message = message.Replace(
                path.Replace('\\', '/'),
                "[图片]",
                StringComparison.OrdinalIgnoreCase);
        }

        return message;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
