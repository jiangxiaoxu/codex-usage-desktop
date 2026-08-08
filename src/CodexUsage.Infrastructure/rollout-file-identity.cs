using CodexUsage.Domain;

namespace CodexUsage.Infrastructure;

internal static class RolloutFileIdentity
{
    internal static string FallbackRolloutId(string filePath)
    {
        if (TryGetTrailingUuidV7(filePath, out var rolloutId)) return rolloutId;
        return LegacyFallbackRolloutId(filePath);
    }

    internal static bool TryGetTrailingUuidV7(string filePath, out string rolloutId)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (name.StartsWith("rollout-", StringComparison.Ordinal) && name.Length >= 36)
        {
            var candidate = name[^36..];
            if (IsUuidV7(candidate))
            {
                rolloutId = candidate.ToLowerInvariant();
                return true;
            }
        }

        rolloutId = string.Empty;
        return false;
    }

    internal static string LegacyFallbackRolloutId(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (!name.StartsWith("rollout-", StringComparison.Ordinal)) return name;
        var separators = 0;
        for (var index = 0; index < name.Length; index++)
        {
            if (name[index] != '-') continue;
            separators++;
            if (separators == 2) return name[(index + 1)..];
        }
        return name;
    }

    internal static bool IsUuidV7(string value) => ConversationId.IsUuidV7(value);
}
