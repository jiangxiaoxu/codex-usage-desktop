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

    internal static bool IsUuidV7(string value) =>
        value.Length == 36
        && value[8] == '-'
        && value[13] == '-'
        && value[18] == '-'
        && value[23] == '-'
        && value[14] == '7'
        && value[19] is '8' or '9' or 'a' or 'b' or 'A' or 'B'
        && Guid.TryParseExact(value, "D", out _);
}
