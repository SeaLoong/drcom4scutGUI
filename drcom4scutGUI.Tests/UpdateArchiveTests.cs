using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class UpdateArchiveTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("", true)]
    [InlineData("traversal", false)]
    [InlineData("symlink", false)]
    [InlineData("case-collision", false)]
    [InlineData("device-name", false)]
    [InlineData("backslash", false)]
    [InlineData("private-config", false)]
    [InlineData("architecture", false)]
    [InlineData("crc", false)]
    public async Task WindowsZipValidationUsesSafeNamesAndPeArchitecture(string fault, bool valid)
    {
        using var directory = new TemporaryDirectory();
        const string stem = "drcom4scut-0.6.0-x86_64-pc-windows-msvc";
        string archive = directory.File(stem + ".zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            byte[] binary = new byte[256];
            binary[0] = (byte)'M'; binary[1] = (byte)'Z';
            BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(60), 128);
            "PE\0\0"u8.CopyTo(binary.AsSpan(128));
            BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(132), (ushort)(fault == "architecture" ? 0xaa64 : 0x8664));
            BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(152), 0x20b);
            Add(zip, stem + "/drcom4scut.exe", binary);
            Add(zip, stem + "/BUILD-INFO.json", JsonSerializer.SerializeToUtf8Bytes(new { version = "0.6.0", target = "x86_64-pc-windows-msvc", source_commit = Commit, source_dirty = false, openwrt = false }));
            if (fault == "traversal") Add(zip, stem + "/../escape", [1]);
            if (fault == "case-collision") Add(zip, stem + "/DRCOM4SCUT.exe", [1]);
            if (fault == "device-name") Add(zip, stem + "/NUL.txt", [1]);
            if (fault == "backslash") Add(zip, stem + "/docs\\escape", [1]);
            if (fault == "private-config") Add(zip, stem + "/accounts.json", [1]);
            if (fault == "symlink")
            {
                ZipArchiveEntry entry = zip.CreateEntry(stem + "/escape");
                entry.ExternalAttributes = 0xa1ff << 16;
                using Stream output = entry.Open(); output.Write("/tmp/escape"u8);
            }
        }
        if (fault == "crc")
        {
            byte[] malformed = File.ReadAllBytes(archive);
            for (int index = 0; index < malformed.Length - 20; index++)
                if (malformed.AsSpan(index, 4).SequenceEqual("PK\u0001\u0002"u8)) { malformed[index + 16] ^= 1; break; }
            malformed[14] ^= 1;
            File.WriteAllBytes(archive, malformed);
        }
        var release = new UpdateRelease(UpdateComponent.Core, "0.6.0", "v0.6.0", "win-x64", new Uri("https://github.com/SeaLoong/drcom4scut/releases/tag/v0.6.0"),
            stem + ".zip", new Uri("https://github.com/SeaLoong/drcom4scut/releases/download/v0.6.0/" + stem + ".zip"), new FileInfo(archive).Length,
            "sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archive, TestContext.Current.CancellationToken))).ToLowerInvariant()) { SourceCommit = Commit };
        async Task Validate()
        {
            string extracted = await UpdateArchive.ExtractAsync(archive, directory.File("extracted"), release, CancellationToken.None);
            await UpdateArchive.VerifyAsync(extracted, release, CancellationToken.None);
        }
        if (valid) await Validate();
        else await Assert.ThrowsAsync<InvalidDataException>(Validate);
        Assert.False(File.Exists(directory.File("escape")));
    }

    [Fact]
    public async Task GiantPaxMetadataIsRejectedBeforeTarReaderAllocatesItsDeclaredLength()
    {
        using var directory = new TemporaryDirectory();
        string archive = directory.File("huge.tar.gz");
        using (var output = File.Create(archive))
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            byte[] header = new byte[512];
            "pax"u8.CopyTo(header);
            "77777777777\0"u8.CopyTo(header.AsSpan(124));
            header[156] = (byte)'x';
            gzip.Write(header);
        }
        var release = new UpdateRelease(UpdateComponent.Core, "0.6.0", "v0.6.0", "linux-x64", new Uri("https://github.com/SeaLoong/drcom4scut/releases/tag/v0.6.0"),
            "drcom4scut-0.6.0-x86_64-unknown-linux-gnu.tar.gz", new Uri("https://github.com/SeaLoong/drcom4scut/releases/download/v0.6.0/drcom4scut-0.6.0-x86_64-unknown-linux-gnu.tar.gz"),
            new FileInfo(archive).Length, "sha256:" + new string('0', 64)) { SourceCommit = Commit };
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateArchive.ExtractAsync(archive, directory.File("stage"), release, CancellationToken.None));
    }

    [Theory]
    [InlineData("zip", "component/../escape", false)]
    [InlineData("zip", "component/../../escape", false)]
    [InlineData("zip", "component/../stage-sibling/escape", false)]
    [InlineData("zip", "component/../escape/", true)]
    [InlineData("zip", "/escape", false)]
    [InlineData("zip", "C:/escape", false)]
    [InlineData("zip", "component\\..\\escape", false)]
    [InlineData("zip", "component/.drcom-update.lock", false)]
    [InlineData("zip", "component/.drcom-update-transaction/", true)]
    [InlineData("zip", "component/.drcom4scut-update-stage/backup", false)]
    [InlineData("zip", "component/.drcom-gui-new-fake.app/payload", false)]
    [InlineData("zip", "component/.drcom-gui-old-fake.app/payload", false)]
    [InlineData("tar.gz", "component/../escape", false)]
    [InlineData("tar.gz", "component/../../escape", false)]
    [InlineData("tar.gz", "component/../stage-sibling/escape", false)]
    [InlineData("tar.gz", "component/../escape/", true)]
    [InlineData("tar.gz", "/escape", false)]
    [InlineData("tar.gz", "C:/escape", false)]
    [InlineData("tar.gz", "component\\..\\escape", false)]
    [InlineData("tar.gz", "component/.DRCOM-UPDATE.LOCK", false)]
    [InlineData("tar.gz", "component/.drcom-update-transaction/", true)]
    [InlineData("tar.gz", "component/.drcom4scut-update-stage/backup", false)]
    [InlineData("tar.gz", "component/.drcom-gui-new-fake.app/payload", false)]
    [InlineData("tar.gz", "component/.drcom-gui-old-fake.app/payload", false)]
    public async Task MaliciousArchiveMembersCannotCreateFilesOrDirectoriesOutsideTheirComponent(
        string format, string member, bool isDirectory)
    {
        using var directory = new TemporaryDirectory();
        string archive = directory.File("component." + format);
        CreateArchive(archive, format, member, isDirectory);
        var release = ArchiveRelease(archive);
        string destination = directory.File("stage");

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateArchive.ExtractAsync(
            archive, destination, release, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(directory.File("escape")));
        Assert.False(Directory.Exists(directory.File("stage-sibling")));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("tar.gz")]
    public async Task CanonicalExtractionRootAllowsNestedFilesWithOrWithoutTrailingSeparator(string format)
    {
        using var directory = new TemporaryDirectory();
        string archive = directory.File("component." + format);
        CreateArchive(archive, format, "component/docs/readme.txt", false);
        var release = ArchiveRelease(archive);
        foreach (bool trailingSeparator in new[] { false, true })
        {
            string destination = directory.File(trailingSeparator ? "with-separator" : "without-separator");
            string root = await UpdateArchive.ExtractAsync(archive,
                destination + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : ""),
                release, TestContext.Current.CancellationToken);
            Assert.Equal(Path.Combine(destination, "component"), root);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(
                Path.Combine(root, "docs", "readme.txt"), TestContext.Current.CancellationToken));
        }
    }

    private static UpdateRelease ArchiveRelease(string archive) => new(UpdateComponent.Core, "0.6.0", "v0.6.0", "win-x64",
        new Uri("https://github.com/SeaLoong/drcom4scut/releases/tag/v0.6.0"), Path.GetFileName(archive),
        new Uri("https://github.com/SeaLoong/drcom4scut/releases/download/v0.6.0/" + Path.GetFileName(archive)),
        new FileInfo(archive).Length, "sha256:" + new string('0', 64)) { SourceCommit = Commit };

    private static void CreateArchive(string archive, string format, string member, bool isDirectory)
    {
        if (format == "zip")
        {
            using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
            if (isDirectory) zip.CreateEntry(member);
            else Add(zip, member, [1, 2, 3]);
        }
        else
        {
            using FileStream file = File.Create(archive);
            using GZipStream gzip = new(file, CompressionLevel.Fastest);
            using TarWriter writer = new(gzip);
            PaxTarEntry entry = new(isDirectory ? TarEntryType.Directory : TarEntryType.RegularFile, member);
            using MemoryStream contents = new([1, 2, 3]);
            if (!isDirectory) entry.DataStream = contents;
            writer.WriteEntry(entry);
        }
    }

    private static void Add(ZipArchive archive, string path, byte[] bytes)
    {
        using Stream output = archive.CreateEntry(path).Open();
        output.Write(bytes);
    }
}
