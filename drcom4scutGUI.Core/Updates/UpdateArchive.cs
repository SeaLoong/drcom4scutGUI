using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace drcom4scutGUI;

internal static class UpdateArchive
{
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 25000;
    private static readonly HashSet<string> PrivateNames = new(StringComparer.OrdinalIgnoreCase)
    { "gui.json", "drcom4scutgui.json", "config.json", "accounts.json", "config.yml", "config.yaml", "core.yml", "core.yaml" };

    internal static async Task<string> ExtractAsync(string archive, string destination, UpdateRelease release, CancellationToken cancellationToken)
    {
        string stem = release.AssetName.EndsWith(".zip", StringComparison.Ordinal) ? release.AssetName[..^4] : release.AssetName[..^7];
        string destinationRoot = Path.GetFullPath(destination + Path.DirectorySeparatorChar);
        Directory.CreateDirectory(destinationRoot);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        int entries = 0;
        string Member(string name, bool directory, long length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entries > MaximumEntries || length < 0 || length > MaximumExpandedBytes || (total = checked(total + length)) > MaximumExpandedBytes)
                throw new InvalidDataException("更新归档包含过多文件或解压大小超过限制。");
            if (name.Length > 1024 || name.Contains('\\') || name.Contains(':') || name.StartsWith("/", StringComparison.Ordinal)
                || name.Any(character => character < 32 || character == 127))
                throw new InvalidDataException("更新归档包含无效路径。");
            string normalized = directory ? name.TrimEnd('/') : name;
            string[] parts = normalized.Split('/');
            if (parts.Length == 0 || parts[0] != stem || parts.Any(part => part.Length == 0 || part is "." or ".."
                || part.EndsWith(' ') || part.EndsWith('.') || IsWindowsDevice(part)) || !names.Add(normalized))
                throw new InvalidDataException("更新归档包含重复、越界或不安全路径。");
            if (parts.Any(PrivateNames.Contains)) throw new InvalidDataException("更新归档不能包含用户账号或核心配置。");
            if (parts.Any(UpdateInstaller.IsInstallationControlName))
                throw new InvalidDataException("更新归档不能包含内部安装控制路径。");
            if (release.Component == UpdateComponent.Gui && parts.Any(part => part.Equals("drcom4scut", StringComparison.OrdinalIgnoreCase)
                || part.Equals("drcom4scut.exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("GUI 更新必须使用不含核心的独立组件包。");
            // Keep an explicit canonical containment check as a second boundary,
            // even after validating every archive path segment above.
            string target = Path.GetFullPath(Path.Combine(destinationRoot, Path.Combine(parts)));
            if (!target.StartsWith(destinationRoot, StringComparison.Ordinal))
                throw new InvalidDataException("更新归档路径离开了解压目录。");
            return target;
        }

        await using FileStream input = new(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (release.AssetName.EndsWith(".zip", StringComparison.Ordinal))
        {
            await PreflightZipAsync(input, cancellationToken).ConfigureAwait(false);
            input.Position = 0;
            using ZipArchive zip = new(input, ZipArchiveMode.Read, leaveOpen: true);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                int kind = (entry.ExternalAttributes >> 16) & 0xf000;
                bool directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                if (kind != 0 && kind != (directory ? 0x4000 : 0x8000))
                    throw new InvalidDataException("更新 ZIP 包包含符号链接或特殊文件。");
                if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("更新 ZIP 包包含重解析点。");
                string target = Member(entry.FullName, directory, entry.Length);
                if (directory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using Stream source = entry.Open();
                await CopyAsync(source, target, entry.Length, cancellationToken, entry.Crc32).ConfigureAwait(false);
            }
        }
        else
        {
            // TarReader parses PAX attributes before returning an entry. Bound their raw
            // lengths first so an untrusted header cannot request a huge allocation.
            await PreflightTarAsync(input, cancellationToken).ConfigureAwait(false);
            input.Position = 0;
            await using GZipStream gzip = new(input, CompressionMode.Decompress, leaveOpen: true);
            using TarReader reader = new(gzip, leaveOpen: true);
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)) is not null)
            {
                bool directory = entry.EntryType == TarEntryType.Directory;
                if (!directory && entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("更新 tar 包包含链接、设备或特殊文件。");
                if ((entry.Mode & (UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit)) != 0)
                    throw new InvalidDataException("更新 tar 包包含特殊权限。");
                string target = Member(entry.Name, directory, entry.Length);
                if (directory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (entry.DataStream is null && entry.Length != 0) throw new InvalidDataException("更新归档文件缺少数据。");
                await CopyAsync(entry.DataStream ?? Stream.Null, target, entry.Length, cancellationToken).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, entry.Mode & (UnixFileMode)0x1ff);
            }
        }
        string root = Path.Combine(destinationRoot, stem);
        if (!Directory.Exists(root)) throw new InvalidDataException("更新归档缺少组件目录。");
        return root;
    }

    private static async Task PreflightZipAsync(Stream input, CancellationToken cancellationToken)
    {
        int length = (int)Math.Min(input.Length, 65557);
        byte[] tail = new byte[length];
        input.Position = input.Length - length;
        await input.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
        for (int index = length - 22; index >= 0; index--)
        {
            ReadOnlySpan<byte> entry = tail.AsSpan(index);
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != 0x06054b50
                || index + 22 + BinaryPrimitives.ReadUInt16LittleEndian(entry[20..]) != length) continue;
            ushort disk = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
            ushort centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            ushort diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(entry[8..]);
            ushort allEntries = BinaryPrimitives.ReadUInt16LittleEndian(entry[10..]);
            uint centralSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            uint centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
            if (disk != 0 || centralDisk != 0 || diskEntries != allEntries || allEntries == 0 || allEntries > MaximumEntries
                || centralSize > 16 * 1024 * 1024 || (long)centralOffset + centralSize > input.Length)
                throw new InvalidDataException("ZIP 目录条目过多或不支持分卷/ZIP64 包。");
            return;
        }
        throw new InvalidDataException("ZIP 包缺少有效的中央目录。");
    }

    private static async Task PreflightTarAsync(Stream input, CancellationToken cancellationToken)
    {
        if (input.Length < 18) throw new InvalidDataException("gzip 更新包不完整。");
        byte[] footer = new byte[8];
        input.Position = input.Length - 8;
        await input.ReadExactlyAsync(footer, cancellationToken).ConfigureAwait(false);
        input.Position = 0;
        await using GZipStream gzip = new(input, CompressionMode.Decompress, leaveOpen: true);
        byte[] header = new byte[512];
        byte[] scratch = new byte[65536];
        long total = 0;
        long expanded = 0;
        uint crc = uint.MaxValue;
        void Track(ReadOnlySpan<byte> data)
        {
            expanded = checked(expanded + data.Length);
            if (expanded > MaximumExpandedBytes + MaximumEntries * 2048L) throw new InvalidDataException("gzip 解压大小超过限制。");
            foreach (byte value in data) crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 0xff];
        }
        for (int count = 0; count <= MaximumEntries * 2; count++)
        {
            await gzip.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            Track(header);
            if (header.All(value => value == 0))
            {
                int read;
                while ((read = await gzip.ReadAsync(scratch, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    Track(scratch.AsSpan(0, read));
                    if (scratch.AsSpan(0, read).ContainsAnyExcept((byte)0)) throw new InvalidDataException("tar 结束后包含额外数据。");
                }
                if ((crc ^ uint.MaxValue) != BinaryPrimitives.ReadUInt32LittleEndian(footer)
                    || (uint)expanded != BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(4)))
                    throw new InvalidDataException("gzip 更新包的 CRC 或完整性校验失败。");
                return;
            }
            string octal = Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ');
            if (octal.Length == 0 || octal.Any(value => value is < '0' or > '7')) throw new InvalidDataException("tar 包含无效文件长度。");
            long size;
            try { size = Convert.ToInt64(octal, 8); }
            catch (Exception error) when (error is OverflowException or FormatException) { throw new InvalidDataException("tar 文件长度超过限制。", error); }
            bool metadata = header[156] is (byte)'x' or (byte)'g';
            if (size > MaximumExpandedBytes || (metadata && size > 65536)
                || header[156] is (byte)'L' or (byte)'K') throw new InvalidDataException("tar 扩展元数据或文件长度超过限制。");
            long padded = checked((size + 511) / 512 * 512);
            total = checked(total + padded + 512);
            if (total > MaximumExpandedBytes + MaximumEntries * 2048L) throw new InvalidDataException("tar 解压大小超过限制。");
            for (long remaining = padded; remaining > 0;)
            {
                int take = (int)Math.Min(remaining, scratch.Length);
                await gzip.ReadExactlyAsync(scratch.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
                Track(scratch.AsSpan(0, take));
                remaining -= take;
            }
        }
        throw new InvalidDataException("tar 包含过多条目。");
    }

    private static readonly uint[] CrcTable = MakeCrcTable();
    private static uint[] MakeCrcTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320U : 0);
            table[index] = value;
        }
        return table;
    }

    private static bool IsWindowsDevice(string part)
    {
        string name = part.Split('.')[0];
        return Regex.IsMatch(name, @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static async Task CopyAsync(Stream source, string target, long length, CancellationToken cancellationToken, uint? expectedCrc = null)
    {
        await using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        byte[] buffer = new byte[65536];
        long written = 0;
        uint crc = uint.MaxValue;
        while (true)
        {
            int count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            written = checked(written + count);
            if (written > length) throw new InvalidDataException("归档文件解压大小不一致。");
            if (expectedCrc is not null)
                for (int index = 0; index < count; index++) crc = (crc >> 8) ^ CrcTable[(crc ^ buffer[index]) & 0xff];
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        if (written != length) throw new InvalidDataException("归档文件数据不完整。");
        if (expectedCrc is { } expected && (crc ^ uint.MaxValue) != expected) throw new InvalidDataException("ZIP 文件 CRC 校验失败。");
    }

    internal static async Task VerifyAsync(string root, UpdateRelease release, CancellationToken cancellationToken)
    {
        UpdateService.ValidateRelease(release);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新暂存目录包含符号链接。");
        using JsonDocument document = await ReadJsonAsync(Path.Combine(root, "BUILD-INFO.json"), cancellationToken).ConfigureAwait(false);
        JsonElement metadata = document.RootElement;
        if (UpdateService.String(metadata, "version") != release.Version || UpdateService.String(metadata, "source_commit") != release.SourceCommit
            || metadata.GetProperty("source_dirty").ValueKind != JsonValueKind.False
            || !Regex.IsMatch(UpdateService.String(metadata, "source_commit"), @"^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("更新包版本或源码来源信息无效。");
        bool windows = release.Runtime.StartsWith("win-", StringComparison.Ordinal);
        bool mac = release.Runtime.StartsWith("osx-", StringComparison.Ordinal);
        if (release.Component == UpdateComponent.Core)
        {
            if (UpdateService.String(metadata, "target") != UpdateService.CoreTarget(release.Runtime) || metadata.GetProperty("openwrt").ValueKind != JsonValueKind.False)
                throw new InvalidDataException("核心更新包的平台或 OpenWrt 标记不匹配。");
            await VerifyExecutableAsync(Path.Combine(root, windows ? "drcom4scut.exe" : "drcom4scut"), release.Runtime, executable: true, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (UpdateService.String(metadata, "runtime") != release.Runtime || metadata.GetProperty("self_contained").ValueKind != JsonValueKind.True
            || metadata.GetProperty("core_included").ValueKind != JsonValueKind.False
            || UpdateService.String(metadata, "macos_signing") != (mac ? "ad-hoc" : "none")
            || UpdateService.ParseVersion(UpdateService.String(metadata, "minimum_core_version")).Prerelease.Length != 0)
            throw new InvalidDataException("GUI 更新包必须是匹配本机的自包含独立组件。");
        string payload = mac ? Path.Combine(root, "drcom4scutGUI.app", "Contents", "Resources", "publish") : root;
        string[] required = ["drcom4scutGUI.dll", "drcom4scutGUI.Core.dll", "drcom4scutGUI.runtimeconfig.json", "drcom4scutGUI.deps.json",
            "Avalonia.Base.dll", "Avalonia.Controls.dll", "Avalonia.Themes.Fluent.dll", "Avalonia.Fonts.Inter.dll"];
        if (required.Any(file => !File.Exists(Path.Combine(payload, file)))) throw new InvalidDataException("GUI 更新包缺少程序或运行时文件。");
        using JsonDocument deps = await ReadJsonAsync(Path.Combine(payload, "drcom4scutGUI.deps.json"), cancellationToken).ConfigureAwait(false);
        if (!UpdateService.String(deps.RootElement.GetProperty("runtimeTarget"), "name").EndsWith("/" + release.Runtime, StringComparison.Ordinal)
            || !deps.RootElement.GetProperty("libraries").TryGetProperty("drcom4scutGUI/" + release.Version, out _))
            throw new InvalidDataException("GUI 程序依赖信息的版本或平台不符。");
        await VerifyExecutableAsync(Path.Combine(payload, windows ? "drcom4scutGUI.exe" : "drcom4scutGUI"), release.Runtime, true, cancellationToken).ConfigureAwait(false);
        foreach (string name in windows ? new[] { "coreclr.dll", "hostfxr.dll" } : mac ? new[] { "libcoreclr.dylib", "libhostfxr.dylib" } : new[] { "libcoreclr.so", "libhostfxr.so" })
            await VerifyExecutableAsync(Path.Combine(payload, name), release.Runtime, false, cancellationToken).ConfigureAwait(false);
        if (mac)
        {
            await VerifyExecutableAsync(Path.Combine(root, "drcom4scutGUI.app", "Contents", "MacOS", "drcom4scutGUI"), release.Runtime, true, cancellationToken).ConfigureAwait(false);
            using JsonDocument embedded = await ReadJsonAsync(Path.Combine(root, "drcom4scutGUI.app", "Contents", "Resources", "BUILD-INFO.json"), cancellationToken).ConfigureAwait(false);
            if (embedded.RootElement.GetRawText() != metadata.GetRawText()) throw new InvalidDataException("macOS 应用内外的来源信息不一致。");
            if (OperatingSystem.IsMacOS())
            {
                ProcessStartInfo start = new("/usr/bin/codesign") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                start.ArgumentList.Add("--verify");
                start.ArgumentList.Add("--deep");
                start.ArgumentList.Add("--strict");
                start.ArgumentList.Add(Path.Combine(root, "drcom4scutGUI.app"));
                using Process process = Process.Start(start) ?? throw new IOException("无法验证 macOS 应用签名。");
                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromMinutes(1));
                Task output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
                Task error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
                try { await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).ConfigureAwait(false); }
                catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
                if (process.ExitCode != 0) throw new InvalidDataException("macOS 更新应用的签名完整性验证失败。");
            }
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("更新包缺少有效的构建信息。");
        await using FileStream input = File.OpenRead(path);
        return await JsonDocument.ParseAsync(input, new JsonDocumentOptions { MaxDepth = 64 }, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task VerifyExecutableAsync(string path, string runtime, bool executable, CancellationToken cancellationToken)
    {
        await using FileStream input = File.OpenRead(path);
        byte[] data = new byte[Math.Min(input.Length, 65536)];
        await input.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        bool arm = runtime.EndsWith("-arm64", StringComparison.Ordinal);
        bool valid;
        if (runtime.StartsWith("win-", StringComparison.Ordinal))
        {
            valid = data.Length >= 64 && data[0] == 'M' && data[1] == 'Z';
            if (valid)
            {
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(60));
                valid = offset <= (uint)data.Length - 26 && data.AsSpan((int)offset, 4).SequenceEqual("PE\0\0"u8)
                    && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)offset + 4)) == (arm ? 0xaa64 : 0x8664)
                    && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)offset + 24)) == 0x20b;
            }
        }
        else if (runtime.StartsWith("linux-", StringComparison.Ordinal))
            valid = data.Length >= 64 && data.AsSpan(0, 6).SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1 })
                && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18)) == (arm ? 183 : 62)
                && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(16)) is 2 or 3;
        else
            valid = data.Length >= 32 && data.AsSpan(0, 4).SequenceEqual(new byte[] { 0xcf, 0xfa, 0xed, 0xfe })
                && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) == (arm ? 0x100000c : 0x1000007)
                && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)) == (executable ? 2 : 6);
        if (!valid) throw new InvalidDataException("更新程序的二进制格式或处理器架构不匹配。");
        if (executable && !runtime.StartsWith("win-", StringComparison.Ordinal) && !OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new InvalidDataException("更新程序缺少执行权限。");
    }
}
