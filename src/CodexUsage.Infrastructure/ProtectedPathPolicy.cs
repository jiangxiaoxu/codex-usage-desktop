namespace CodexUsage.Infrastructure;

public sealed class ProtectedPathPolicy
{
    private const string RejectionMessage =
        "The Codex source directories are read-only observation sources and cannot be used for application output.";

    private readonly IReadOnlyList<string> _protectedDirectories;
    private readonly StringComparison _comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public ProtectedPathPolicy(IEnumerable<string> protectedDirectories)
    {
        ArgumentNullException.ThrowIfNull(protectedDirectories);
        _protectedDirectories = protectedDirectories
            .Select(ResolveThroughExistingAncestors)
            .ToArray();
        if (_protectedDirectories.Count == 0)
        {
            throw new ArgumentException("At least one protected directory is required.", nameof(protectedDirectories));
        }
    }

    public static ProtectedPathPolicy ForCodexHome(string codexHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        var root = Path.GetFullPath(codexHome);
        return new ProtectedPathPolicy(
        [
            Path.Combine(root, "sessions"),
            Path.Combine(root, "archived_sessions"),
            Path.Combine(root, "agents"),
        ]);
    }

    public void AssertWritablePath(string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        var resolvedCandidate = ResolveThroughExistingAncestors(candidate);
        if (_protectedDirectories.Any(directory => IsWithin(directory, resolvedCandidate)))
        {
            throw new InvalidOperationException(RejectionMessage);
        }
    }

    internal static string ResolveThroughExistingAncestors(string candidate)
    {
        var fullPath = Path.GetFullPath(candidate);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("The path has no filesystem root.", nameof(candidate));
        var relative = fullPath[root.Length..];
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolved = root;

        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].Length == 0)
            {
                continue;
            }

            var next = Path.Combine(resolved, segments[index]);
            FileSystemInfo? info = Directory.Exists(next)
                ? new DirectoryInfo(next)
                : File.Exists(next)
                    ? new FileInfo(next)
                    : null;
            if (info is null)
            {
                return Path.GetFullPath(Path.Combine(resolved, Path.Combine(segments[index..])));
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info = info.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new IOException($"Unable to resolve reparse point: {info.FullName}");
            }

            resolved = info.FullName;
        }

        return Path.GetFullPath(resolved);
    }

    private bool IsWithin(string directory, string candidate)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (string.Equals(normalizedDirectory, normalizedCandidate, _comparison))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(
            normalizedDirectory + Path.DirectorySeparatorChar,
            _comparison);
    }
}
