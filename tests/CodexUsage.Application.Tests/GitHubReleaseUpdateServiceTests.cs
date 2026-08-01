using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexUsage.Application;
using CodexUsage.Infrastructure;
using Xunit;

namespace CodexUsage.Application.Tests;

public sealed class GitHubReleaseUpdateServiceTests
{
    [Fact]
    public async Task CheckAcceptsOnlyTheExpectedNewerGitHubReleaseAsset()
    {
        var manifest = CreateManifest("v0.3.2", "installer bytes");
        using var client = CreateClient([JsonResponse(manifest)]);
        var dataDirectory = CreateDataDirectory();
        try
        {
            var service = CreateService(client, dataDirectory);

            var result = await service.CheckAsync();

            Assert.True(result.IsAvailable);
            Assert.True(result.IsUpdateAvailable);
            Assert.NotNull(result.Package);
            Assert.Equal("0.3.2", result.Package.Version);
            Assert.Equal("v0.3.2", result.Package.ReleaseTag);
            Assert.Equal(
                "https://github.com/jiangxiaoxu/codex-usage-desktop/releases/download/v0.3.2/codex-usage-desktop-setup-0.3.2-x64.exe",
                result.Package.DownloadUri.AbsoluteUri);
            Assert.Contains("未签名实验版本", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CheckRejectsMultipleAssetsEvenWhenOneAssetLooksValid()
    {
        var manifest = CreateManifest("v0.3.2", "installer bytes", assetCount: 2);
        using var client = CreateClient([JsonResponse(manifest)]);
        var dataDirectory = CreateDataDirectory();
        try
        {
            var service = CreateService(client, dataDirectory);

            var result = await service.CheckAsync();

            Assert.True(result.IsAvailable);
            Assert.False(result.IsUpdateAvailable);
            Assert.Null(result.Package);
            Assert.Contains("只包含一个 installer asset", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CheckRejectsAReleaseFromAnotherRepository()
    {
        var manifest = CreateManifest("v0.3.2", "installer bytes").Replace(
            "github.com/jiangxiaoxu/codex-usage-desktop",
            "github.com/not-owner/not-repository",
            StringComparison.Ordinal);
        using var client = CreateClient([JsonResponse(manifest)]);
        var dataDirectory = CreateDataDirectory();
        try
        {
            var result = await CreateService(client, dataDirectory).CheckAsync();

            Assert.False(result.IsUpdateAvailable);
            Assert.Contains("owner/repository", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CheckRejectsNonSemanticVersionTagsAndMissingDigest()
    {
        var invalidVersion = CreateManifest("v0.3.2", "installer bytes").Replace(
            "v0.3.2",
            "v0.3",
            StringComparison.Ordinal);
        var missingDigestNode = JsonNode.Parse(CreateManifest("v0.3.2", "installer bytes"))!.AsObject();
        missingDigestNode["assets"]!.AsArray()[0]!.AsObject().Remove("digest");
        var missingDigest = missingDigestNode.ToJsonString();
        var dataDirectory = CreateDataDirectory();
        try
        {
            using var invalidVersionClient = CreateClient([JsonResponse(invalidVersion)]);
            using var missingDigestClient = CreateClient([JsonResponse(missingDigest)]);

            var invalidVersionResult = await CreateService(invalidVersionClient, dataDirectory).CheckAsync();
            var missingDigestResult = await CreateService(missingDigestClient, dataDirectory).CheckAsync();

            Assert.False(invalidVersionResult.IsUpdateAvailable);
            Assert.Contains("semantic version", invalidVersionResult.Message, StringComparison.Ordinal);
            Assert.False(missingDigestResult.IsUpdateAvailable);
            Assert.Contains("digest", missingDigestResult.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadWritesOnlyTheValidatedSha256Installer()
    {
        const string payload = "installer bytes";
        var manifest = CreateManifest("v0.3.2", payload);
        using var client = CreateClient(
            [JsonResponse(manifest), new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload)),
            }]);
        var dataDirectory = CreateDataDirectory();
        try
        {
            var service = CreateService(client, dataDirectory);
            var check = await service.CheckAsync();

            var download = await service.DownloadAsync(check.Package!);

            Assert.True(download.Status == ReleaseUpdateDownloadStatus.Completed, download.Message);
            Assert.NotNull(download.InstallerPath);
            Assert.Equal(payload, await File.ReadAllTextAsync(download.InstallerPath));
            Assert.Contains("运行前会再次校验", download.Message, StringComparison.Ordinal);

            var verification = await service.VerifyDownloadedInstallerAsync(
                check.Package!,
                download.InstallerPath);
            Assert.True(verification.IsValid);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadRejectsDigestMismatchAndDoesNotKeepTheInstaller()
    {
        const string expected = "expected installer bytes";
        const string received = "modified installer bytes";
        var manifest = CreateManifest("v0.3.2", expected);
        using var client = CreateClient(
            [JsonResponse(manifest), new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(received)),
            }]);
        var dataDirectory = CreateDataDirectory();
        try
        {
            var service = CreateService(client, dataDirectory);
            var check = await service.CheckAsync();

            var download = await service.DownloadAsync(check.Package!);

            Assert.Equal(ReleaseUpdateDownloadStatus.Failed, download.Status);
            Assert.Null(download.InstallerPath);
            Assert.Contains("SHA-256", download.Message, StringComparison.Ordinal);
            var updateDirectory = Path.Combine(dataDirectory, "update-downloads");
            Assert.True(!Directory.Exists(updateDirectory) || !Directory.EnumerateFiles(updateDirectory).Any());
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallerVerificationRejectsAFileModifiedAfterDownload()
    {
        const string payload = "installer bytes";
        var manifest = CreateManifest("v0.3.2", payload);
        using var client = CreateClient(
            [JsonResponse(manifest), new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload)),
            }]);
        var dataDirectory = CreateDataDirectory();
        try
        {
            var service = CreateService(client, dataDirectory);
            var check = await service.CheckAsync();
            var download = await service.DownloadAsync(check.Package!);
            await File.WriteAllTextAsync(download.InstallerPath!, "modified installer bytes");

            var verification = await service.VerifyDownloadedInstallerAsync(
                check.Package!,
                download.InstallerPath!);

            Assert.False(verification.IsValid);
            Assert.Contains("校验失败", verification.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadRejectsAnUnexpectedRedirectHost()
    {
        const string payload = "installer bytes";
        var manifest = CreateManifest("v0.3.2", payload);
        using var client = CreateClient(
            [JsonResponse(manifest), new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload)),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://untrusted.example/installer.exe"),
            }]);
        var dataDirectory = CreateDataDirectory();
        try
        {
            var service = CreateService(client, dataDirectory);
            var check = await service.CheckAsync();

            var download = await service.DownloadAsync(check.Package!);

            Assert.Equal(ReleaseUpdateDownloadStatus.Failed, download.Status);
            Assert.Contains("不受信任", download.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CheckConvertsTransportFailuresIntoAStableDiagnostic()
    {
        using var client = new HttpClient(new QueueHttpHandler(
            [new HttpRequestException("offline")]))
        {
            Timeout = TimeSpan.FromSeconds(1),
        };
        var dataDirectory = CreateDataDirectory();
        try
        {
            var service = CreateService(client, dataDirectory);

            var result = await service.CheckAsync();

            Assert.True(result.IsAvailable);
            Assert.False(result.IsUpdateAvailable);
            Assert.Contains("检查更新失败", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void ConstructorRejectsAnUpdateDirectoryInsideProtectedCodexSources()
    {
        using var client = CreateClient([]);
        var dataDirectory = CreateDataDirectory();
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => new GitHubReleaseUpdateService(
                client,
                dataDirectory,
                "0.3.1",
                new ProtectedPathPolicy([dataDirectory])));

            Assert.Contains("read-only observation sources", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static HttpClient CreateClient(IReadOnlyList<HttpResponseMessage> responses) => new(
        new QueueHttpHandler(responses))
    {
        Timeout = TimeSpan.FromSeconds(1),
    };

    private static GitHubReleaseUpdateService CreateService(HttpClient client, string dataDirectory) => new(
        client,
        dataDirectory,
        "0.3.1",
        new ProtectedPathPolicy([Path.Combine(dataDirectory, "protected-codex-source")]));

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private static string CreateManifest(string tag, string installer, int assetCount = 1)
    {
        var version = tag[1..];
        var assetName = $"codex-usage-desktop-setup-{version}-x64.exe";
        var digest = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installer))).ToLowerInvariant()}";
        var assets = Enumerable.Range(0, assetCount)
            .Select(index => new
            {
                id = 101 + index,
                url = $"https://api.github.com/repos/jiangxiaoxu/codex-usage-desktop/releases/assets/{101 + index}",
                name = index == 0 ? assetName : $"extra-{index}.txt",
                size = Encoding.UTF8.GetByteCount(installer),
                browser_download_url = index == 0
                    ? $"https://github.com/jiangxiaoxu/codex-usage-desktop/releases/download/{tag}/{assetName}"
                    : $"https://github.com/jiangxiaoxu/codex-usage-desktop/releases/download/{tag}/extra-{index}.txt",
                digest,
            });
        return JsonSerializer.Serialize(new
        {
            id = 7,
            url = "https://api.github.com/repos/jiangxiaoxu/codex-usage-desktop/releases/7",
            html_url = $"https://github.com/jiangxiaoxu/codex-usage-desktop/releases/tag/{tag}",
            tag_name = tag,
            draft = false,
            prerelease = false,
            published_at = "2026-08-01T00:00:00Z",
            assets,
        });
    }

    private static string CreateDataDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-usage-update-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class QueueHttpHandler : HttpMessageHandler
    {
        private readonly Queue<object> _responses;

        public QueueHttpHandler(IEnumerable<object> responses)
        {
            _responses = new Queue<object>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = _responses.Dequeue();
            if (next is Exception error) throw error;
            var response = (HttpResponseMessage)next;
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
