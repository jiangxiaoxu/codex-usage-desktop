using System.Diagnostics;
using Xunit;

namespace CodexUsage.Infrastructure.Tests;

public sealed class ProtectedPathPolicyTests
{
    [Fact]
    public void ForCodexHomeProtectsAllThreeObservationDirectories()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = Path.Combine(temporary.Path, ".codex");
        var outside = Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(Path.Combine(codexHome, "sessions"));
        Directory.CreateDirectory(Path.Combine(codexHome, "archived_sessions"));
        Directory.CreateDirectory(Path.Combine(codexHome, "agents"));
        Directory.CreateDirectory(outside);
        var policy = ProtectedPathPolicy.ForCodexHome(codexHome);

        Assert.Throws<InvalidOperationException>(() =>
            policy.AssertWritablePath(Path.Combine(codexHome, "sessions", "new", "output.csv")));
        Assert.Throws<InvalidOperationException>(() =>
            policy.AssertWritablePath(Path.Combine(codexHome, "archived_sessions", "output.csv")));
        Assert.Throws<InvalidOperationException>(() =>
            policy.AssertWritablePath(Path.Combine(codexHome, "agents", "worker", "output.csv")));
        policy.AssertWritablePath(Path.Combine(outside, "output.csv"));
    }

    [Fact]
    public void ExistingReparsePointCannotBypassProtection()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = Path.Combine(temporary.Path, ".codex");
        var agents = Path.Combine(codexHome, "agents");
        var outside = Path.Combine(temporary.Path, "outside");
        var link = Path.Combine(outside, "agents-link");
        Directory.CreateDirectory(agents);
        Directory.CreateDirectory(outside);
        CreateJunction(link, agents);
        var policy = ProtectedPathPolicy.ForCodexHome(codexHome);

        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                policy.AssertWritablePath(Path.Combine(link, "missing", "output.csv")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void WindowsComparisonIsCaseInsensitive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var codexHome = Path.Combine(temporary.Path, ".codex");
        Directory.CreateDirectory(Path.Combine(codexHome, "sessions"));
        var policy = ProtectedPathPolicy.ForCodexHome(codexHome);

        Assert.Throws<InvalidOperationException>(() => policy.AssertWritablePath(
            Path.Combine(codexHome.ToUpperInvariant(), "SESSIONS", "output.csv")));
    }

    private static void CreateJunction(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", path, target },
        }) ?? throw new InvalidOperationException("Unable to start mklink.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException(process.StandardError.ReadToEnd());
        }
    }
}
