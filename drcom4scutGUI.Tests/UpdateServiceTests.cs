using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class UpdateServiceTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Asset = "drcom4scut-0.6.0-x86_64-unknown-linux-gnu.tar.gz";
    private const string Stem = "drcom4scut-0.6.0-x86_64-unknown-linux-gnu";

    [Theory]
    [InlineData("0.5.1", true)]
    [InlineData("drcom4scut 0.6.0", false)]
    [InlineData("v0.7.0", false)]
    [InlineData("0.6.0-preview.2", true)]
    [InlineData("0.7.0-preview.1", false)]
    public async Task LatestFormalReleaseUsesProxyAndNeverDowngrades(string current, bool available)
    {
        byte[] archive = Archive();
        await using var server = new LoopbackReleaseServer(archive);
        using var client = new HttpClient(server.CreateHandler());
        using var service = new UpdateService(client, "linux-x64", new UpdateOptions());
        UpdateCheckResult result = await service.CheckAsync(UpdateComponent.Core, current, TestContext.Current.CancellationToken);
        Assert.Equal(available, result.IsUpdateAvailable);
        Assert.Equal("0.6.0", result.Release.Version);
        Assert.Equal(Commit, result.Release.SourceCommit);
        Assert.Equal(Asset, result.Release.AssetName);
        Assert.Single(server.Requests);
        Assert.All(server.Requests, url => Assert.StartsWith(UpdateService.ProxyUrl + "https://api.github.com/", url));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("tag")]
    [InlineData("repository")]
    [InlineData("asset-origin")]
    [InlineData("missing-sums")]
    [InlineData("duplicate-asset")]
    [InlineData("missing-digest")]
    [InlineData("missing-sums-digest")]
    public async Task RejectsUnpublishedForeignIncompleteOrAmbiguousRelease(string fault)
    {
        await using var server = new LoopbackReleaseServer(Archive(), fault);
        using var client = new HttpClient(server.CreateHandler());
        using var service = new UpdateService(client, "linux-x64", new UpdateOptions());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync(UpdateComponent.Core, "0.5.1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifiedArchiveIsStreamedAndDisposalRemovesOnlyItsPrivateStage()
    {
        await using var server = new LoopbackReleaseServer(Archive());
        using var client = new HttpClient(server.CreateHandler());
        using var service = new UpdateService(client, "linux-x64", new UpdateOptions());
        UpdateRelease release = (await service.CheckAsync(UpdateComponent.Core, "0.5.1", TestContext.Current.CancellationToken)).Release;
        PreparedUpdate prepared = await service.DownloadAsync(release, cancellationToken: TestContext.Current.CancellationToken);
        string stage = prepared.StagingDirectory;
        Assert.True(File.Exists(Path.Combine(stage, "drcom4scut")));
        Assert.True(File.Exists(Path.Combine(stage, "BUILD-INFO.json")));
        Assert.DoesNotContain(server.Requests, url => !url.StartsWith(UpdateService.ProxyUrl, StringComparison.Ordinal));
        Assert.Contains(UpdateService.ProxyUrl + $"https://github.com/SeaLoong/drcom4scut/releases/download/v0.6.0/{Asset}", server.Requests);
        prepared.Dispose();
        Assert.False(Directory.Exists(stage));
    }

    [Theory]
    [InlineData("traversal")]
    [InlineData("absolute")]
    [InlineData("symlink")]
    [InlineData("hardlink")]
    [InlineData("duplicate")]
    [InlineData("case-collision")]
    [InlineData("private-config")]
    [InlineData("architecture")]
    [InlineData("source")]
    [InlineData("version")]
    [InlineData("dirty")]
    [InlineData("openwrt")]
    [InlineData("execute-mode")]
    [InlineData("gzip-crc")]
    [InlineData("gzip-truncated-footer")]
    [InlineData("trailing-tar-garbage")]
    public async Task RejectsUnsafeArchiveOrMismatchedBuildBeforeInstall(string fault)
    {
        if (OperatingSystem.IsWindows() && fault == "execute-mode") return;
        await using var server = new LoopbackReleaseServer(Archive(fault));
        using var client = new HttpClient(server.CreateHandler());
        using var service = new UpdateService(client, "linux-x64", new UpdateOptions());
        UpdateRelease release = (await service.CheckAsync(UpdateComponent.Core, "0.5.1", TestContext.Current.CancellationToken)).Release;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("payload-corruption")]
    [InlineData("sums-corruption")]
    [InlineData("sums-disagreement")]
    [InlineData("truncated-download")]
    public async Task RejectsCorruptionAndMissingBytes(string fault)
    {
        await using var server = new LoopbackReleaseServer(Archive(), fault);
        using var client = new HttpClient(server.CreateHandler());
        using var service = new UpdateService(client, "linux-x64", new UpdateOptions());
        UpdateRelease release = (await service.CheckAsync(UpdateComponent.Core, "0.5.1", TestContext.Current.CancellationToken)).Release;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationBeforeDownloadDoesNotCreateStage()
    {
        await using var server = new LoopbackReleaseServer(Archive());
        using var client = new HttpClient(server.CreateHandler());
        using var service = new UpdateService(client, "linux-x64", new UpdateOptions());
        UpdateRelease release = (await service.CheckAsync(UpdateComponent.Core, "0.5.1", TestContext.Current.CancellationToken)).Release;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(release, cancellationToken: cancelled.Token));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task ProxyRedirectCannotEscapeToThirdPartyOrPlainHttp()
    {
        using var client = new HttpClient(new RedirectHandler());
        using var service = new UpdateService(client, "linux-x64", new UpdateOptions());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync(UpdateComponent.Core, "0.5.1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConfiguredHttpsProxyPrefixRoutesMetadataChecksumsAndArchiveTogether()
    {
        const string prefix = "https://mirror.example:8443/github/";
        await using var server = new LoopbackReleaseServer(Archive());
        using var client = new HttpClient(server.CreateHandler());
        using var service = new UpdateService(client, "linux-x64", new UpdateOptions { ProxyUrl = prefix });
        UpdateRelease release = (await service.CheckAsync(UpdateComponent.Core, "0.5.1", TestContext.Current.CancellationToken)).Release;
        using PreparedUpdate prepared = await service.DownloadAsync(release, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, server.Requests.Count);
        Assert.All(server.Requests, address => Assert.StartsWith(prefix, address));
    }

    [Fact]
    public async Task PublicReleaseCannotSelectBundleOpenWrtOrAnotherArchitecture()
    {
        await using var server = new LoopbackReleaseServer(Archive());
        using var client = new HttpClient(server.CreateHandler());
        using var service = new UpdateService(client, "linux-x64", new UpdateOptions());
        UpdateRelease release = (await service.CheckAsync(UpdateComponent.Core, "0.5.1", TestContext.Current.CancellationToken)).Release;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release with { AssetName = "drcom4scut-desktop-core-0.6.0-gui-3.2.0-linux-x64.tar.gz" }, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release with { Runtime = "linux-arm64" }, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release with { AssetUrl = new Uri("https://example.org/drcom4scut") }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("branch-target", true)]
    [InlineData("tag-source", false)]
    public async Task BranchTargetDefersExactTagResolutionUntilDownload(string fault, bool valid)
    {
        await using var server = new LoopbackReleaseServer(Archive(), fault);
        using var client = new HttpClient(server.CreateHandler());
        using var service = new UpdateService(client, "linux-x64", new UpdateOptions());
        UpdateRelease release = (await service.CheckAsync(UpdateComponent.Core, "0.5.1", TestContext.Current.CancellationToken)).Release;
        Assert.Null(release.SourceCommit);
        Assert.Single(server.Requests);
        if (valid)
        {
            using PreparedUpdate prepared = await service.DownloadAsync(release, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Commit, prepared.Release.SourceCommit);
        }
        else await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static byte[] Archive(string fault = "")
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            byte[] binary = new byte[64];
            new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1 }.CopyTo(binary, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(16), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(18), (ushort)(fault == "architecture" ? 183 : 62));
            Add(tar, Stem + "/drcom4scut", binary, fault == "execute-mode" ? (UnixFileMode)0x180 : (UnixFileMode)0x1ed);
            Add(tar, Stem + "/BUILD-INFO.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = fault == "version" ? "0.7.0" : "0.6.0", target = "x86_64-unknown-linux-gnu",
                source_commit = fault == "source" ? new string('b', 40) : Commit,
                source_dirty = fault == "dirty", openwrt = fault == "openwrt"
            }));
            if (fault == "traversal") Add(tar, Stem + "/../escape", [1]);
            if (fault == "absolute") Add(tar, "/tmp/drcom-update-escape", [1]);
            if (fault == "private-config") Add(tar, Stem + "/core.yml", [1]);
            if (fault == "duplicate") Add(tar, Stem + "/drcom4scut", [1]);
            if (fault == "case-collision") Add(tar, Stem + "/DRCOM4SCUT", [1]);
            if (fault is "symlink" or "hardlink")
                tar.WriteEntry(new PaxTarEntry(fault == "symlink" ? TarEntryType.SymbolicLink : TarEntryType.HardLink, Stem + "/escape") { LinkName = "/tmp/escape" });
        }
        byte[] result = bytes.ToArray();
        if (fault == "gzip-crc") result[^8] ^= 1;
        if (fault == "gzip-truncated-footer") return result[..^1];
        if (fault == "trailing-tar-garbage")
        {
            using var decoded = new MemoryStream();
            using (var source = new GZipStream(new MemoryStream(result), CompressionMode.Decompress)) source.CopyTo(decoded);
            decoded.WriteByte(1);
            using var malformed = new MemoryStream();
            using (var target = new GZipStream(malformed, CompressionLevel.Fastest, leaveOpen: true)) target.Write(decoded.ToArray());
            return malformed.ToArray();
        }
        return result;
    }

    private static void Add(TarWriter tar, string path, byte[] bytes, UnixFileMode mode = (UnixFileMode)0x1a4)
    {
        using var data = new MemoryStream(bytes);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = data, Mode = mode });
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request };
            response.Headers.Location = new Uri("https://example.org/releases/latest");
            return Task.FromResult(response);
        }
    }

    /// <summary>A real loopback HTTP server; requests still enter production code as fixed HTTPS proxy URLs.</summary>
    private sealed class LoopbackReleaseServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task run;
        private readonly byte[] archive;
        private readonly string fault;
        public List<string> Requests { get; } = [];
        public LoopbackReleaseServer(byte[] archive, string fault = "")
        {
            this.archive = archive;
            this.fault = fault;
            listener.Start();
            run = ServeAsync();
        }
        public HttpMessageHandler CreateHandler() => new LoopbackHandler(((IPEndPoint)listener.LocalEndpoint).Port);
        private async Task ServeAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using TcpClient connection = await listener.AcceptTcpClientAsync(lifetime.Token);
                    using NetworkStream stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                    string first = await reader.ReadLineAsync(lifetime.Token) ?? "";
                    while (await reader.ReadLineAsync(lifetime.Token) is { Length: > 0 }) { }
                    string address = Uri.UnescapeDataString(first.Split(' ')[1].TrimStart('/'));
                    Requests.Add(address);
                    byte[] body = Response(address);
                    byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, lifetime.Token);
                    await stream.WriteAsync(body, lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
        }
        private byte[] Response(string address)
        {
            string sha = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            byte[] sums = Encoding.UTF8.GetBytes($"{(fault == "sums-disagreement" ? new string('0', 64) : sha)}  {Asset}\n");
            if (address.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                string root = "https://github.com/SeaLoong/drcom4scut/releases/download/v0.6.0/";
                object payload = new { name = Asset, size = archive.Length, state = "uploaded", browser_download_url = fault == "asset-origin" ? "https://example.org/" + Asset : root + Asset,
                    digest = fault == "missing-digest" ? null : "sha256:" + sha };
                object checksum = new { name = "SHA256SUMS", size = sums.Length, state = "uploaded", browser_download_url = root + "SHA256SUMS", digest = fault == "missing-sums-digest" ? null : "sha256:" + Convert.ToHexString(SHA256.HashData(sums)).ToLowerInvariant() };
                return JsonSerializer.SerializeToUtf8Bytes(new
                {
                    id = 42, url = fault == "repository" ? "https://api.github.com/repos/other/project/releases/42" : "https://api.github.com/repos/SeaLoong/drcom4scut/releases/42",
                    html_url = "https://github.com/SeaLoong/drcom4scut/releases/tag/v0.6.0",
                    target_commitish = fault is "branch-target" or "tag-source" ? "master" : Commit,
                    tag_name = fault == "tag" ? "preview-42-1-aaaaaaaa" : "v0.6.0", draft = fault == "draft", prerelease = fault == "prerelease",
                    assets = fault == "missing-sums" ? new[] { payload } : fault == "duplicate-asset" ? new[] { payload, checksum, payload } : new[] { payload, checksum }
                });
            }
            if (address.Contains("/git/ref/tags/", StringComparison.Ordinal))
                return JsonSerializer.SerializeToUtf8Bytes(new { @ref = fault == "tag-source" ? "refs/tags/other" : "refs/tags/v0.6.0", @object = new { type = "commit", sha = Commit } });
            if (address.EndsWith("/SHA256SUMS", StringComparison.Ordinal)) return fault == "sums-corruption" ? [1, 2, 3] : sums;
            if (address.EndsWith("/" + Asset, StringComparison.Ordinal))
            {
                if (fault == "truncated-download") return archive[..^1];
                if (fault == "payload-corruption") { byte[] damaged = archive.ToArray(); damaged[^1] ^= 1; return damaged; }
                return archive;
            }
            throw new InvalidOperationException("Unexpected loopback request: " + address);
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            listener.Stop();
            await run;
            lifetime.Dispose();
        }
        private sealed class LoopbackHandler(int port) : HttpMessageHandler
        {
            private readonly HttpClient transport = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                HttpResponseMessage response = await transport.GetAsync($"http://127.0.0.1:{port}/{Uri.EscapeDataString(request.RequestUri!.AbsoluteUri)}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.RequestMessage = request;
                return response;
            }
            protected override void Dispose(bool disposing) { if (disposing) transport.Dispose(); base.Dispose(disposing); }
        }
    }
}
