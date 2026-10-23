using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace drcom4scutGUI;

public enum UpdateComponent { Gui, Core }

public sealed record UpdateRelease(UpdateComponent Component, string Version, string Tag, string Runtime,
    Uri ReleaseUrl, string AssetName, Uri AssetUrl, long AssetSize, string? Digest)
{
    internal string? ChecksumsDigest { get; init; }
    public string? SourceCommit { get; init; }
}

public sealed record UpdateCheckResult(UpdateComponent Component, string CurrentVersion, UpdateRelease Release, bool IsUpdateAvailable);

/// <summary>An independently verified standalone component. Disposal removes its private download directory.</summary>
public sealed class PreparedUpdate : IDisposable
{
    public UpdateRelease Release { get; }
    public string StagingDirectory { get; }
    private readonly string ownerDirectory;
    internal PreparedUpdate(UpdateRelease release, string stagingDirectory, string ownerDirectory)
        => (Release, StagingDirectory, this.ownerDirectory) = (release, stagingDirectory, ownerDirectory);
    public void Dispose()
    {
        if (Directory.Exists(ownerDirectory)) Directory.Delete(ownerDirectory, recursive: true);
    }
}

/// <summary>Uses GitHub's latest formal release through the configured HTTPS proxy.</summary>
public sealed class UpdateService : IDisposable
{
    public const string ProxyUrl = "https://gh-proxy.com/";
    internal const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly string runtime;
    private readonly Uri proxy;

    public UpdateService(HttpClient? httpClient = null, string? runtime = null, UpdateOptions? options = null)
    {
        proxy = (options ?? new UpdateOptionsStore().Load()).Validate();
        client = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(10) };
        ownsClient = httpClient is null;
        this.runtime = runtime ?? NativeRuntime;
        _ = CoreTarget(this.runtime);
    }

    public static string NativeRuntime => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" :
        OperatingSystem.IsMacOS() ? "osx" : throw new PlatformNotSupportedException("不支持此操作系统的自动更新。")) + "-" +
        (RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x64" : RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "arm64" : throw new PlatformNotSupportedException("不支持此处理器的自动更新。"));

    public async Task<UpdateCheckResult> CheckAsync(UpdateComponent component, string currentVersion, CancellationToken cancellationToken = default)
    {
        SemanticVersion current = ParseVersion(currentVersion);
        string repository = Repository(component);
        byte[] body = await ReadBytesAsync(new Uri($"https://api.github.com/repos/{repository}/releases/latest"), 2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        using JsonDocument json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement release = json.RootElement;
        string tag = String(release, "tag_name");
        SemanticVersion latest = ParseVersion(tag);
        if (latest.Prerelease.Length != 0 || !Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)
            || release.GetProperty("draft").ValueKind != JsonValueKind.False || release.GetProperty("prerelease").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("更新源未返回正式发布版本。");
        long releaseId = release.GetProperty("id").GetInt64();
        string expectedApi = $"https://api.github.com/repos/{repository}/releases/{releaseId.ToString(CultureInfo.InvariantCulture)}";
        Uri releaseUrl = new($"https://github.com/{repository}/releases/tag/{tag}");
        if (releaseId <= 0 || String(release, "url") != expectedApi || String(release, "html_url") != releaseUrl.AbsoluteUri)
            throw new InvalidDataException("发布信息属于其他仓库或标签。");
        string version = latest.Plain;
        string name = ArchiveName(component, version, runtime);
        JsonElement[] assets = release.GetProperty("assets").EnumerateArray().ToArray();
        JsonElement archive = SingleAsset(assets, name);
        JsonElement checksums = SingleAsset(assets, "SHA256SUMS");
        ValidateAsset(archive, repository, tag, name, MaximumArchiveBytes);
        ValidateAsset(checksums, repository, tag, "SHA256SUMS", 256 * 1024);
        string? digest = AssetDigest(archive);
        if (digest is null) throw new InvalidDataException("发布文件缺少 GitHub SHA-256 摘要。");
        string checksumsDigest = AssetDigest(checksums) ?? throw new InvalidDataException("发布校验清单缺少 GitHub SHA-256 摘要。");
        string target = String(release, "target_commitish");
        string? sourceCommit = Regex.IsMatch(target, @"^[0-9a-f]{40}$", RegexOptions.CultureInvariant) ? target : null;
        UpdateRelease result = new(component, version, tag, runtime, releaseUrl, name,
            new Uri(String(archive, "browser_download_url")), archive.GetProperty("size").GetInt64(), digest)
        { ChecksumsDigest = checksumsDigest, SourceCommit = sourceCommit };
        return new(component, currentVersion, result, latest.CompareTo(current) > 0);
    }

    public async Task<PreparedUpdate> DownloadAsync(UpdateRelease release, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateRelease(release, requireSource: false);
        if (release.Runtime != runtime) throw new InvalidDataException("更新包的平台与检查平台不一致。");
        string repository = Repository(release.Component);
        if (release.SourceCommit is null)
            release = release with { SourceCommit = await ResolveTagAsync(repository, release.Tag, cancellationToken).ConfigureAwait(false) };
        ValidateRelease(release);
        byte[] sums = await ReadBytesAsync(new Uri($"https://github.com/{repository}/releases/download/{release.Tag}/SHA256SUMS"),
            256 * 1024, cancellationToken).ConfigureAwait(false);
        CheckHash(sums, release.ChecksumsDigest ?? throw new InvalidDataException("发布校验清单缺少 GitHub SHA-256 摘要，请重新检查更新。"));
        string archiveHash = ChecksumFor(sums, release.AssetName);
        if (!string.Equals(archiveHash, release.Digest?[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA256SUMS 与 GitHub 发布摘要不一致。");
        string owner = Path.Combine(Path.GetTempPath(), "drcom-update-" + Guid.NewGuid().ToString("N"));
        UpdateInstaller.CreatePrivateDirectory(owner);
        try
        {
            string archive = Path.Combine(owner, release.AssetName);
            using (HttpResponseMessage response = await GetAsync(release.AssetUrl, cancellationToken).ConfigureAwait(false))
            {
                if (response.Content.Headers.ContentLength is { } length && length != release.AssetSize)
                    throw new InvalidDataException("下载文件大小与发布信息不符。");
                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using FileStream destination = new(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[65536];
                long written = 0;
                while (true)
                {
                    int count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    written = checked(written + count);
                    if (written > release.AssetSize || written > MaximumArchiveBytes) throw new InvalidDataException("下载文件超过允许大小。");
                    hash.AppendData(buffer.AsSpan(0, count));
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    progress?.Report((double)written / release.AssetSize);
                }
                if (written != release.AssetSize || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), archiveHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下载文件的大小或 SHA-256 校验失败。");
            }
            string extracted = Path.Combine(owner, "payload");
            string root = await UpdateArchive.ExtractAsync(archive, extracted, release, cancellationToken).ConfigureAwait(false);
            await UpdateArchive.VerifyAsync(root, release, cancellationToken).ConfigureAwait(false);
            File.Delete(archive);
            return new PreparedUpdate(release, root, owner);
        }
        catch
        {
            Directory.Delete(owner, recursive: true);
            throw;
        }
    }

    public Task InstallCoreAsync(PreparedUpdate update, string coreExecutablePath, CancellationToken cancellationToken = default)
        => UpdateInstaller.InstallCoreAsync(update, coreExecutablePath, cancellationToken);

    public Task ValidateGuiInstallAsync(PreparedUpdate update, string guiExecutablePath, CancellationToken cancellationToken = default)
        => UpdateInstaller.ValidateGuiInstallAsync(update, guiExecutablePath, cancellationToken);

    public Task ScheduleGuiInstallAsync(PreparedUpdate update, string guiExecutablePath, CancellationToken cancellationToken = default)
        => UpdateInstaller.ScheduleGuiInstallAsync(update, guiExecutablePath, cancellationToken);

    private async Task<byte[]> ReadBytesAsync(Uri canonical, int limit, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await GetAsync(canonical, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("更新信息超过允许大小。");
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream result = new();
        byte[] buffer = new byte[16384];
        while (true)
        {
            int count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (result.Length + count > limit) throw new InvalidDataException("更新信息超过允许大小。");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private async Task<string> ResolveTagAsync(string repository, string tag, CancellationToken cancellationToken)
    {
        Uri address = new($"https://api.github.com/repos/{repository}/git/ref/tags/{tag}");
        for (int depth = 0; depth < 4; depth++)
        {
            using JsonDocument document = JsonDocument.Parse(await ReadBytesAsync(address, 65536, cancellationToken).ConfigureAwait(false));
            JsonElement target = document.RootElement.GetProperty("object");
            string sha = String(target, "sha");
            string type = String(target, "type");
            if (!Regex.IsMatch(sha, @"^[0-9a-f]{40}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("发布标签的源码提交无效。");
            if (depth == 0 && String(document.RootElement, "ref") != "refs/tags/" + tag) throw new InvalidDataException("发布标签不匹配。");
            if (type == "commit") return sha;
            if (type != "tag") throw new InvalidDataException("发布标签没有指向源码提交。");
            address = new Uri($"https://api.github.com/repos/{repository}/git/tags/{sha}");
        }
        throw new InvalidDataException("发布标签的嵌套层数超过限制。");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri canonical, CancellationToken cancellationToken)
    {
        Uri address = new(proxy.AbsoluteUri + canonical.AbsoluteUri);
        for (int redirect = 0; redirect < 4; redirect++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("drcom4scutGUI-updater/3.2.0");
            request.Headers.Accept.ParseAdd(canonical.Host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream");
            HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            // Reject clients which silently follow redirects outside the explicitly selected proxy.
            if (response.RequestMessage?.RequestUri is { } final && !IsConfiguredProxy(final))
            {
                response.Dispose();
                throw new InvalidDataException("更新请求离开了指定的 HTTPS 代理。");
            }
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("更新代理返回无效跳转。");
                Uri next = new(address, location);
                if (!IsConfiguredProxy(next))
                    throw new InvalidDataException("更新代理尝试跳转到其他服务器。");
                address = next;
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                HttpStatusCode status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"更新代理请求失败（HTTP {(int)status}）。", null, status);
            }
            return response;
        }
        throw new InvalidDataException("更新代理跳转过多。");
    }

    private bool IsConfiguredProxy(Uri address) => address.Scheme == "https" && address.Host == proxy.Host && address.Port == proxy.Port
        && address.UserInfo.Length == 0 && address.AbsolutePath.StartsWith(proxy.AbsolutePath, StringComparison.Ordinal);

    internal static void ValidateRelease(UpdateRelease release, bool requireSource = true)
    {
        ArgumentNullException.ThrowIfNull(release);
        SemanticVersion version = ParseVersion(release.Tag);
        string repository = Repository(release.Component);
        if (version.Prerelease.Length != 0 || version.Plain != release.Version || !Regex.IsMatch(release.Tag, @"^v?\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)
            || release.AssetName != ArchiveName(release.Component, release.Version, release.Runtime)
            || release.ReleaseUrl.AbsoluteUri != $"https://github.com/{repository}/releases/tag/{release.Tag}"
            || release.AssetUrl.AbsoluteUri != $"https://github.com/{repository}/releases/download/{release.Tag}/{release.AssetName}"
            || release.AssetSize <= 0 || release.AssetSize > MaximumArchiveBytes
            || (release.SourceCommit is null ? requireSource : !Regex.IsMatch(release.SourceCommit, @"^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
            || release.Digest is null || !Regex.IsMatch(release.Digest, @"^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("更新包身份、平台或校验信息无效。");
    }

    private static void ValidateAsset(JsonElement asset, string repository, string tag, string name, long limit)
    {
        long size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > limit || String(asset, "state") != "uploaded"
            || String(asset, "browser_download_url") != $"https://github.com/{repository}/releases/download/{tag}/{name}")
            throw new InvalidDataException("发布资产的来源、状态或大小无效。");
    }

    private static string? AssetDigest(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        string digest = value.GetString() ?? "";
        if (!Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("发布资产摘要无效。");
        return digest;
    }

    private static JsonElement SingleAsset(JsonElement[] assets, string name)
    {
        JsonElement[] matches = assets.Where(asset => String(asset, "name") == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("最新正式版缺少唯一的本机组件包或校验清单。");
        return matches[0];
    }

    internal static string Repository(UpdateComponent component) => component switch
    {
        UpdateComponent.Gui => "SeaLoong/drcom4scutGUI", UpdateComponent.Core => "SeaLoong/drcom4scut",
        _ => throw new ArgumentOutOfRangeException(nameof(component))
    };

    internal static string CoreTarget(string runtime) => runtime switch
    {
        "win-x64" => "x86_64-pc-windows-msvc", "win-arm64" => "aarch64-pc-windows-msvc",
        "linux-x64" => "x86_64-unknown-linux-gnu", "linux-arm64" => "aarch64-unknown-linux-gnu",
        "osx-x64" => "x86_64-apple-darwin", "osx-arm64" => "aarch64-apple-darwin",
        _ => throw new PlatformNotSupportedException("不支持此平台的自动更新。")
    };

    internal static string ArchiveName(UpdateComponent component, string version, string runtime)
    {
        string target = CoreTarget(runtime);
        return $"{(component == UpdateComponent.Gui ? "drcom4scutGUI" : "drcom4scut")}-{version}-{(component == UpdateComponent.Gui ? runtime : target)}.{(runtime.StartsWith("win-", StringComparison.Ordinal) ? "zip" : "tar.gz")}";
    }

    private static string ChecksumFor(byte[] bytes, string name)
    {
        string text = new UTF8Encoding(false, true).GetString(bytes);
        Dictionary<string, string> entries = new(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = Regex.Match(line.TrimEnd('\r'), @"^([0-9a-fA-F]{64})  ([A-Za-z0-9_.-]+)$", RegexOptions.CultureInvariant);
            if (!match.Success || !entries.TryAdd(match.Groups[2].Value, match.Groups[1].Value))
                throw new InvalidDataException("SHA256SUMS 格式错误或包含重复项。");
        }
        return entries.TryGetValue(name, out string? value) ? value : throw new InvalidDataException("SHA256SUMS 缺少本机组件包。");
    }

    private static void CheckHash(byte[] bytes, string digest)
    {
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), digest[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("校验清单的 SHA-256 验证失败。");
    }

    internal static string String(JsonElement element, string property) => element.GetProperty(property).GetString() ?? throw new InvalidDataException("发布信息包含空字段。");

    internal static SemanticVersion ParseVersion(string value)
    {
        Match match = Regex.Match(value.Trim(), @"^(?:drcom4scut(?:GUI)?\s+)?v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int major) || !int.TryParse(match.Groups[2].Value, out int minor) || !int.TryParse(match.Groups[3].Value, out int patch))
            throw new InvalidDataException("无法识别当前或发布版本号。");
        return new SemanticVersion(major, minor, patch, match.Groups[4].Value);
    }

    internal sealed record SemanticVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<SemanticVersion>
    {
        public string Plain => $"{Major}.{Minor}.{Patch}" + (Prerelease.Length == 0 ? "" : "-" + Prerelease);
        public int CompareTo(SemanticVersion? other)
        {
            if (other is null) return 1;
            int result = Major.CompareTo(other.Major); if (result != 0) return result;
            result = Minor.CompareTo(other.Minor); if (result != 0) return result;
            result = Patch.CompareTo(other.Patch); if (result != 0) return result;
            if (Prerelease.Length == 0) return other.Prerelease.Length == 0 ? 0 : 1;
            if (other.Prerelease.Length == 0) return -1;
            string[] left = Prerelease.Split('.'), right = other.Prerelease.Split('.');
            for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                bool ln = long.TryParse(left[index], out long l), rn = long.TryParse(right[index], out long r);
                result = ln && rn ? l.CompareTo(r) : ln ? -1 : rn ? 1 : string.CompareOrdinal(left[index], right[index]);
                if (result != 0) return result;
            }
            return left.Length.CompareTo(right.Length);
        }
    }

    public void Dispose() { if (ownsClient) client.Dispose(); }
}
