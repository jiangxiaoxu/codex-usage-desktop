namespace CodexUsage.Application;

public static class StartupLaunchContract
{
    public const string Argument = "--startup";

    public static bool IsStartupLaunch(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Contains(Argument, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsStartupLaunch(string? activationArguments)
    {
        if (string.IsNullOrWhiteSpace(activationArguments)) return false;
        return activationArguments
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(Argument, StringComparer.OrdinalIgnoreCase);
    }

    public static string CreateRunCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        }

        var normalized = Path.GetFullPath(executablePath);
        if (normalized.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("The executable path cannot contain a quote.", nameof(executablePath));
        }

        return $"\"{normalized}\" {Argument}";
    }

    public static bool IsOwnedRunCommand(string? command, string executablePath) =>
        string.Equals(command, CreateRunCommand(executablePath), StringComparison.OrdinalIgnoreCase);
}
