using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexUsage.Domain;

namespace CodexUsage.Infrastructure.Collection;

internal static partial class SessionIndexParser
{
    internal const int MaximumLineBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static SessionIndexParseResult Parse(ReadOnlyMemory<byte> input)
    {
        var latestByConversation = new Dictionary<string, SessionIndexEntry>(StringComparer.Ordinal);
        var invalidRecords = 0;
        var offset = 0;
        while (offset < input.Length)
        {
            var remaining = input.Span[offset..];
            var newline = remaining.IndexOf((byte)'\n');
            var length = newline < 0 ? remaining.Length : newline;
            var line = input.Slice(offset, length);
            SessionIndexEntry? entry = null;
            if (!IsBlank(line.Span) && !TryParseEntry(line, offset == 0, out entry))
            {
                invalidRecords++;
            }
            else if (entry is not null
                && (!latestByConversation.TryGetValue(entry.ConversationId, out var prior)
                    || entry.UpdatedAtUtc >= prior.UpdatedAtUtc))
            {
                latestByConversation[entry.ConversationId] = entry;
            }

            if (newline < 0) break;
            offset += length + 1;
        }

        var titles = latestByConversation.ToDictionary(
            static value => value.Key,
            static value => value.Value.Title,
            StringComparer.Ordinal);
        return new SessionIndexParseResult(new ReadOnlyDictionary<string, string>(titles), invalidRecords);
    }

    private static bool TryParseEntry(ReadOnlyMemory<byte> bytes, bool isFirstRecord, out SessionIndexEntry? entry)
    {
        entry = null;
        if (bytes.Length > MaximumLineBytes) return false;

        try
        {
            var span = bytes.Span;
            if (isFirstRecord && span.Length >= 3
                && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
                span = span[3..];
            var line = StrictUtf8.GetString(span);
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetNonEmptyString(root, "id", out var id)
                || !ConversationId.IsUuidV7(id)
                || !TryGetNonEmptyString(root, "thread_name", out var title)
                || !TryGetIsoTimestamp(root, "updated_at", out var updatedAtUtc))
                return false;

            entry = new SessionIndexEntry(id.ToLowerInvariant(), title.Trim(), updatedAtUtc);
            return entry.Title.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryGetNonEmptyString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetIsoTimestamp(JsonElement root, string name, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!TryGetNonEmptyString(root, name, out var value) || !IsoTimestampPattern().IsMatch(value)) return false;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);
    }

    private static bool IsBlank(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
            if (item is not (byte)' ' and not (byte)'\t' and not (byte)'\r') return false;
        return true;
    }

    [GeneratedRegex("^(\\d{4})-(\\d{2})-(\\d{2})T(\\d{2}):(\\d{2}):(\\d{2})(?:\\.\\d+)?(?:Z|[+-]\\d{2}:\\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoTimestampPattern();

    private sealed record SessionIndexEntry(string ConversationId, string Title, DateTimeOffset UpdatedAtUtc);
}

internal sealed record SessionIndexParseResult(
    IReadOnlyDictionary<string, string> ThreadTitles,
    int InvalidRecords)
{
    internal bool IsAuthoritative => InvalidRecords == 0;
}
