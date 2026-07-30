namespace CodexUsage.App.Shell;

internal sealed record ApplicationPaths(string CodexHome, string DataDirectory)
{
    public static ApplicationPaths Resolve()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataOverride = Environment.GetEnvironmentVariable("CODEX_USAGE_DATA_DIR")?.Trim();
        var dataDirectory = string.IsNullOrEmpty(dataOverride)
            ? Path.Combine(localData, "Codex Usage Desktop")
            : Path.GetFullPath(dataOverride);

        return new ApplicationPaths(Path.Combine(userProfile, ".codex"), Path.GetFullPath(dataDirectory));
    }

    public static ApplicationPaths CreateSmokeTest()
    {
        var smokeRoot = Path.Combine(
            Path.GetTempPath(),
            "Codex Usage Desktop",
            "smoke",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        var codexHome = Path.Combine(smokeRoot, "codex-home");
        var dataDirectory = Path.Combine(smokeRoot, "data");

        Directory.CreateDirectory(Path.Combine(codexHome, "sessions"));
        Directory.CreateDirectory(Path.Combine(codexHome, "archived_sessions"));
        Directory.CreateDirectory(Path.Combine(codexHome, "agents"));
        Directory.CreateDirectory(dataDirectory);
        return new ApplicationPaths(codexHome, dataDirectory);
    }

    public void DeleteSmokeTestData()
    {
        var smokeRoot = Directory.GetParent(DataDirectory)?.FullName
            ?? throw new InvalidOperationException("The smoke-test data directory has no parent.");
        var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Codex Usage Desktop", "smoke"))
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedRoot = Path.GetFullPath(smokeRoot)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!normalizedRoot.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to delete a smoke-test directory outside {expectedRoot}");
        }

        if (Directory.Exists(smokeRoot))
        {
            Directory.Delete(smokeRoot, true);
        }
    }
}
