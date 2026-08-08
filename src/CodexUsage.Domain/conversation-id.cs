using System.Text.RegularExpressions;

namespace CodexUsage.Domain;

public static partial class ConversationId
{
    public static bool IsUuidV7(string? value) => value is not null && UuidV7Pattern().IsMatch(value);

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UuidV7Pattern();
}
