namespace CodexUsage.Domain;

public static class ModelActivityVolumeEstimator
{
    public const int CurrentRevision = 1;

    // Codex rust-v0.160.0 reasoning context-volume estimate, not a token count or decoded size.
    public static long EstimateReasoningContentBytes(long rawPayloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawPayloadBytes);
        var adjusted = rawPayloadBytes / 4 * 3 + rawPayloadBytes % 4 * 3 / 4;
        return Math.Max(0, adjusted - 650);
    }
}
