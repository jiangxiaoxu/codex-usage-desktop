using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CodexUsage.Infrastructure;

namespace CodexUsage.Application;

public sealed class GitHubReleaseUpdateService : IReleaseUpdateService
{
    public const string Owner = "jiangxiaoxu";
    public const string Repository = "codex-usage-desktop";
    public static readonly Uri LatestReleaseEndpoint = new(
        $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");

    private const long MaximumInstallerBytes = 1024L * 1024L * 1024L;
    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com",
    };

    private readonly HttpClient _httpClient;
    private readonly ProtectedPathPolicy _protectedPathPolicy;
    private readonly string _downloadDirectory;
    private readonly ReleaseSemanticVersion _currentVersion;

    public GitHubReleaseUpdateService(
        HttpClient httpClient,
        string applicationDataDirectory,
        string currentVersion,
        ProtectedPathPolicy protectedPathPolicy)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _protectedPathPolicy = protectedPathPolicy ?? throw new ArgumentNullException(nameof(protectedPathPolicy));
        if (string.IsNullOrWhiteSpace(applicationDataDirectory))
        {
            throw new ArgumentException("An application data directory is required.", nameof(applicationDataDirectory));
        }

        if (!ReleaseSemanticVersion.TryParse(currentVersion, out _currentVersion))
        {
            throw new ArgumentException("The current version must be a strict semantic version.", nameof(currentVersion));
        }

        _downloadDirectory = Path.GetFullPath(Path.Combine(applicationDataDirectory, "update-downloads"));
        _protectedPathPolicy.AssertWritablePath(_downloadDirectory);
    }

    public bool IsAvailable => true;

    public async Task<ReleaseUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateGetRequest(LatestReleaseEndpoint);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            ValidateFinalReleaseUri(response.RequestMessage?.RequestUri);

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
            var package = ParsePackage(document.RootElement);

            if (ReleaseSemanticVersion.TryParse(package.Version, out var availableVersion)
                && availableVersion.CompareTo(_currentVersion) > 0)
            {
                return new ReleaseUpdateCheckResult(
                    true,
                    true,
                    $"发现未签名实验版本 {package.Version}; 仅校验 GitHub SHA-256 后才可下载",
                    package);
            }

            return new ReleaseUpdateCheckResult(
                true,
                false,
                $"当前已是最新版本 {_currentVersion}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ReleaseUpdateCheckResult(true, false, "检查更新超时; 未执行安装");
        }
        catch (Exception error) when (IsExpectedNetworkOrValidationFailure(error))
        {
            return new ReleaseUpdateCheckResult(true, false, $"检查更新失败: {error.Message}");
        }
    }

    public async Task<ReleaseUpdateDownloadResult> DownloadAsync(
        ReleaseUpdatePackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        string? temporaryPath = null;
        try
        {
            ValidatePackage(package);
            _protectedPathPolicy.AssertWritablePath(_downloadDirectory);
            Directory.CreateDirectory(_downloadDirectory);

            var installerPath = GetInstallerPath(package.Version);
            temporaryPath = Path.Combine(
                _downloadDirectory,
                $"{Path.GetFileName(installerPath)}.{Guid.NewGuid():N}.partial");

            using var request = CreateGetRequest(package.DownloadUri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            ValidateFinalDownloadUri(response.RequestMessage?.RequestUri);

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is not null && declaredLength.Value != package.SizeBytes)
            {
                throw new InvalidDataException("下载响应大小与 Release asset metadata 不一致.");
            }

            var downloadedBytes = await CopyAndHashAsync(
                response.Content,
                temporaryPath,
                package.SizeBytes,
                cancellationToken).ConfigureAwait(false);

            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(downloadedBytes.Sha256),
                    Convert.FromHexString(package.Sha256)))
            {
                throw new InvalidDataException("下载文件的 SHA-256 与 GitHub Release digest 不一致.");
            }

            if (downloadedBytes.Count != package.SizeBytes)
            {
                throw new InvalidDataException("下载文件大小与 GitHub Release asset metadata 不一致.");
            }

            File.Move(temporaryPath, installerPath, overwrite: true);
            temporaryPath = null;
            return new ReleaseUpdateDownloadResult(
                ReleaseUpdateDownloadStatus.Completed,
                $"已校验 SHA-256 并下载未签名实验安装器 {package.Version}; 运行前会再次校验文件",
                installerPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ReleaseUpdateDownloadResult(
                ReleaseUpdateDownloadStatus.Cancelled,
                "更新下载已取消; 未启动安装器");
        }
        catch (OperationCanceledException)
        {
            return new ReleaseUpdateDownloadResult(
                ReleaseUpdateDownloadStatus.Failed,
                "更新下载超时; 未启动安装器");
        }
        catch (Exception error) when (IsExpectedNetworkOrValidationFailure(error))
        {
            return new ReleaseUpdateDownloadResult(
                ReleaseUpdateDownloadStatus.Failed,
                $"更新下载失败: {error.Message}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDelete(temporaryPath);
            }
        }
    }

    public async Task<ReleaseUpdateInstallerVerificationResult> VerifyDownloadedInstallerAsync(
        ReleaseUpdatePackage package,
        string installerPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        try
        {
            ValidatePackage(package);
            var expectedPath = GetInstallerPath(package.Version);
            if (!string.Equals(
                    Path.GetFullPath(installerPath),
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新安装器不位于应用下载目录.");
            }

            if (!File.Exists(expectedPath))
            {
                throw new FileNotFoundException("已下载的更新安装器不存在.", expectedPath);
            }

            var verified = await HashFileAsync(expectedPath, package.SizeBytes, cancellationToken).ConfigureAwait(false);
            if (verified.Count != package.SizeBytes
                || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(verified.Sha256),
                    Convert.FromHexString(package.Sha256)))
            {
                throw new InvalidDataException("安装前 SHA-256 复验失败; 未启动安装器.");
            }

            return new ReleaseUpdateInstallerVerificationResult(true, "安装器 SHA-256 复验通过");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ReleaseUpdateInstallerVerificationResult(false, "安装前校验已取消; 未启动安装器");
        }
        catch (OperationCanceledException)
        {
            return new ReleaseUpdateInstallerVerificationResult(false, "安装前校验超时; 未启动安装器");
        }
        catch (Exception error) when (IsExpectedNetworkOrValidationFailure(error)
            || error is FileNotFoundException
            or UnauthorizedAccessException)
        {
            return new ReleaseUpdateInstallerVerificationResult(false, $"安装前校验失败: {error.Message}");
        }
    }

    private static HttpRequestMessage CreateGetRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("CodexUsageDesktop/0.3");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    private static ReleaseUpdatePackage ParsePackage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("GitHub Release feed 必须是 JSON object.");
        }

        if (ReadRequiredBoolean(root, "draft") || ReadRequiredBoolean(root, "prerelease"))
        {
            throw new InvalidDataException("draft 或 prerelease 不能作为稳定更新.");
        }

        var releaseId = ReadRequiredInt64(root, "id");
        var tag = ReadRequiredString(root, "tag_name");
        if (!ReleaseSemanticVersion.TryParseTag(tag, out var version))
        {
            throw new InvalidDataException("Release tag 不是严格的 semantic version.");
        }

        ValidateReleaseApiUri(ReadRequiredUri(root, "url"), releaseId);
        ValidateReleaseHtmlUri(ReadRequiredUri(root, "html_url"), tag);
        var publishedUtc = ReadRequiredUtc(root, "published_at");

        var assets = ReadRequiredProperty(root, "assets");
        if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() != 1)
        {
            throw new InvalidDataException("稳定 Release 必须只包含一个 installer asset.");
        }

        var asset = assets[0];
        if (asset.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Release asset 必须是 JSON object.");
        }

        var expectedName = InstallerFileName(version.ToString());
        var assetName = ReadRequiredString(asset, "name");
        if (!string.Equals(assetName, expectedName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Release asset 名称不符合当前 x64 installer 约定.");
        }

        var size = ReadRequiredInt64(asset, "size");
        if (size <= 0 || size > MaximumInstallerBytes)
        {
            throw new InvalidDataException("Release asset 大小不在允许范围内.");
        }

        var downloadUri = ReadRequiredUri(asset, "browser_download_url");
        ValidateDownloadUri(downloadUri, tag, expectedName);
        ValidateAssetApiUri(ReadRequiredUri(asset, "url"), ReadRequiredInt64(asset, "id"));
        var digest = ParseSha256Digest(ReadRequiredString(asset, "digest"));

        return new ReleaseUpdatePackage(
            version.ToString(),
            tag,
            downloadUri,
            digest,
            size,
            publishedUtc);
    }

    private static void ValidatePackage(ReleaseUpdatePackage package)
    {
        if (!ReleaseSemanticVersion.TryParse(package.Version, out var version)
            || !ReleaseSemanticVersion.TryParseTag(package.ReleaseTag, out var tagVersion)
            || version != tagVersion)
        {
            throw new InvalidDataException("更新版本 metadata 无效.");
        }

        if (package.SizeBytes <= 0 || package.SizeBytes > MaximumInstallerBytes)
        {
            throw new InvalidDataException("更新文件大小不在允许范围内.");
        }

        _ = ValidateSha256(package.Sha256);
        ValidateDownloadUri(package.DownloadUri, package.ReleaseTag, InstallerFileName(package.Version));
    }

    private string GetInstallerPath(string version)
    {
        var path = Path.GetFullPath(Path.Combine(_downloadDirectory, InstallerFileName(version)));
        var directoryWithSeparator = _downloadDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新下载路径越出了应用数据目录.");
        }

        _protectedPathPolicy.AssertWritablePath(path);

        return path;
    }

    private static async Task<(long Count, string Sha256)> CopyAndHashAsync(
        HttpContent content,
        string destinationPath,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long count = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count = checked(count + read);
                if (count > expectedBytes || count > MaximumInstallerBytes)
                {
                    throw new InvalidDataException("下载文件超过 Release asset 声明的大小.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return (count, Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<(long Count, string Sha256)> HashFileAsync(
        string path,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long count = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count = checked(count + read);
                if (count > expectedBytes || count > MaximumInstallerBytes)
                {
                    throw new InvalidDataException("安装器文件超过 Release asset 声明的大小.");
                }

                hash.AppendData(buffer, 0, read);
            }

            return (count, Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateReleaseApiUri(Uri uri, long releaseId)
    {
        if (!IsHttpsHost(uri, "api.github.com")
            || !string.Equals(
                uri.AbsolutePath,
                $"/repos/{Owner}/{Repository}/releases/{releaseId}",
                StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("Release API URL 不属于预期 owner/repository.");
        }
    }

    private static void ValidateReleaseHtmlUri(Uri uri, string tag)
    {
        if (!IsHttpsHost(uri, "github.com")
            || !string.Equals(
                uri.AbsolutePath,
                $"/{Owner}/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}",
                StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("Release page URL 不属于预期 owner/repository.");
        }
    }

    private static void ValidateAssetApiUri(Uri uri, long assetId)
    {
        if (!IsHttpsHost(uri, "api.github.com")
            || !string.Equals(
                uri.AbsolutePath,
                $"/repos/{Owner}/{Repository}/releases/assets/{assetId}",
                StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("Release asset API URL 不属于预期 owner/repository.");
        }
    }

    private static void ValidateDownloadUri(Uri uri, string tag, string expectedAssetName)
    {
        if (!IsHttpsHost(uri, "github.com")
            || !string.Equals(
                uri.AbsolutePath,
                $"/{Owner}/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{expectedAssetName}",
                StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("Release 下载 URL 不属于预期 owner/repository 或 asset.");
        }
    }

    private static void ValidateFinalDownloadUri(Uri? uri)
    {
        if (uri is null || !IsHttpsHost(uri) || !AllowedDownloadHosts.Contains(uri.Host))
        {
            throw new InvalidDataException("下载请求被重定向到不受信任的主机.");
        }
    }

    private static void ValidateFinalReleaseUri(Uri? uri)
    {
        if (uri is null
            || !IsHttpsHost(uri, "api.github.com")
            || !string.Equals(
                uri.AbsolutePath,
                $"/repos/{Owner}/{Repository}/releases/latest",
                StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("Release metadata 请求被重定向到不受信任的 endpoint.");
        }
    }

    private static bool IsHttpsHost(Uri uri, string? expectedHost = null) =>
        uri.IsAbsoluteUri
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort
        && (expectedHost is null || string.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase));

    private static string InstallerFileName(string version) =>
        $"codex-usage-desktop-setup-{version}-x64.exe";

    private static string ParseSha256Digest(string value)
    {
        const string prefix = "sha256:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Length != prefix.Length + 64)
        {
            throw new InvalidDataException("Release asset 未提供有效 SHA-256 digest.");
        }

        var hex = value[prefix.Length..];
        try
        {
            _ = Convert.FromHexString(hex);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("Release asset SHA-256 digest 格式无效.", error);
        }

        return ValidateSha256(hex);
    }

    private static string ValidateSha256(string value)
    {
        if (value.Length != 64)
        {
            throw new InvalidDataException("Release asset SHA-256 digest 格式无效.");
        }

        try
        {
            _ = Convert.FromHexString(value);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("Release asset SHA-256 digest 格式无效.", error);
        }

        return value.ToUpperInvariant();
    }

    private static JsonElement ReadRequiredProperty(JsonElement objectElement, string name)
    {
        if (!objectElement.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException($"Release feed 缺少 {name}.");
        }

        return value;
    }

    private static string ReadRequiredString(JsonElement objectElement, string name)
    {
        var value = ReadRequiredProperty(objectElement, name);
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Release feed 的 {name} 必须是非空 string.");
        }

        return value.GetString()!;
    }

    private static long ReadRequiredInt64(JsonElement objectElement, string name)
    {
        var value = ReadRequiredProperty(objectElement, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number <= 0)
        {
            throw new InvalidDataException($"Release feed 的 {name} 必须是正整数.");
        }

        return number;
    }

    private static bool ReadRequiredBoolean(JsonElement objectElement, string name)
    {
        var value = ReadRequiredProperty(objectElement, name);
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException($"Release feed 的 {name} 必须是 boolean.");
        }

        return value.GetBoolean();
    }

    private static Uri ReadRequiredUri(JsonElement objectElement, string name)
    {
        var value = ReadRequiredString(objectElement, name);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException($"Release feed 的 {name} 必须是 absolute URL.");
        }

        return uri;
    }

    private static DateTimeOffset ReadRequiredUtc(JsonElement objectElement, string name)
    {
        var value = ReadRequiredString(objectElement, name);
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new InvalidDataException($"Release feed 的 {name} 必须是 UTC timestamp.");
        }

        return parsed;
    }

    private static bool IsExpectedNetworkOrValidationFailure(Exception error) =>
        error is HttpRequestException
            or IOException
            or JsonException
            or InvalidDataException
            or FormatException
            or OverflowException;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct ReleaseSemanticVersion(int Major, int Minor, int Patch) : IComparable<ReleaseSemanticVersion>
    {
        public static bool TryParse(string? value, out ReleaseSemanticVersion version)
        {
            version = default;
            if (string.IsNullOrEmpty(value)) return false;
            var segments = value.Split('.', StringSplitOptions.None);
            return segments.Length == 3
                && TryParsePart(segments[0], out var major)
                && TryParsePart(segments[1], out var minor)
                && TryParsePart(segments[2], out var patch)
                && Set(out version, new ReleaseSemanticVersion(major, minor, patch));
        }

        public static bool TryParseTag(string? value, out ReleaseSemanticVersion version)
        {
            if (value is { Length: > 1 } && value[0] == 'v')
            {
                return TryParse(value[1..], out version);
            }

            return TryParse(value, out version);
        }

        public int CompareTo(ReleaseSemanticVersion other)
        {
            var major = Major.CompareTo(other.Major);
            if (major != 0) return major;
            var minor = Minor.CompareTo(other.Minor);
            return minor != 0 ? minor : Patch.CompareTo(other.Patch);
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}";

        private static bool TryParsePart(string value, out int part)
        {
            part = 0;
            return value.Length > 0
                && (value.Length == 1 || value[0] != '0')
                && value.All(static character => character is >= '0' and <= '9')
                && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out part);
        }

        private static bool Set(out ReleaseSemanticVersion destination, ReleaseSemanticVersion value)
        {
            destination = value;
            return true;
        }
    }
}
