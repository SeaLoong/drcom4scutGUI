using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class UpdateInstallerTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task GuiTransactionReplacesOnlyManifestFilesAndPreservesCoreAndPrivateConfiguration()
    {
        using var directory = new TemporaryDirectory();
        string source = directory.File("new"), target = directory.File("installed");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "drcom4scutGUI.dll"), "new GUI");
        File.WriteAllText(Path.Combine(source, "new-runtime.dll"), "new runtime");
        File.WriteAllText(Path.Combine(target, "drcom4scutGUI.dll"), "old GUI");
        Dictionary<string, string> kept = new() { ["drcom4scut"] = "existing core", ["drcom4scut.exe"] = "existing Windows core",
            ["accounts.json"] = "synthetic encrypted credentials", ["config.yml"] = "private network configuration", ["custom.txt"] = "user notes" };
        foreach (var item in kept) File.WriteAllText(Path.Combine(target, item.Key), item.Value);
        List<UpdateFile> files = await UpdateInstaller.DescribeTreeAsync(source, TestContext.Current.CancellationToken);
        await UpdateInstaller.ApplyFilesAsync(source, target, files, null, null, TestContext.Current.CancellationToken);
        Assert.Equal("new GUI", File.ReadAllText(Path.Combine(target, "drcom4scutGUI.dll")));
        Assert.Equal("new runtime", File.ReadAllText(Path.Combine(target, "new-runtime.dll")));
        foreach (var item in kept) Assert.Equal(item.Value, File.ReadAllText(Path.Combine(target, item.Key)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerificationOrRestartFailureRollsBackAllReplacedFilesAndRemovesNewFiles(bool restart)
    {
        using var directory = new TemporaryDirectory();
        string source = directory.File("new"), target = directory.File("installed");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "a.dll"), "new");
        File.WriteAllText(Path.Combine(source, "b.dll"), "new-only");
        File.WriteAllText(Path.Combine(target, "a.dll"), "old");
        UnixFileMode? oldMode = null;
        if (!OperatingSystem.IsWindows())
        {
            oldMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(Path.Combine(target, "a.dll"), oldMode.Value);
        }
        Func<Task> fail = () => throw new IOException("Synthetic transactional failure");
        await Assert.ThrowsAsync<IOException>(() => UpdateInstaller.ApplyFilesAsync(source, target,
            UpdateInstaller.DescribeTreeAsync(source, TestContext.Current.CancellationToken).GetAwaiter().GetResult(), restart ? null : fail,
            restart ? fail : null, TestContext.Current.CancellationToken));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "a.dll")));
        Assert.False(File.Exists(Path.Combine(target, "b.dll")));
        Assert.Empty(Directory.GetDirectories(target, ".drcom-update-*"));
        if (!OperatingSystem.IsWindows()) Assert.Equal(oldMode, File.GetUnixFileMode(Path.Combine(target, "a.dll")));
    }

    [Fact]
    public async Task FailurePartwayThroughFileMovesRestoresAlreadyReplacedFiles()
    {
        using var directory = new TemporaryDirectory();
        string source = directory.File("new"), target = directory.File("installed");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "a.dll"), "new");
        File.WriteAllText(Path.Combine(source, "z.dll"), "new");
        File.WriteAllText(Path.Combine(target, "a.dll"), "old");
        Directory.CreateDirectory(Path.Combine(target, "z.dll"));
        List<UpdateFile> files = await UpdateInstaller.DescribeTreeAsync(source, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<IOException>(() => UpdateInstaller.ApplyFilesAsync(source, target, files, null, null, TestContext.Current.CancellationToken));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "a.dll")));
        Assert.True(Directory.Exists(Path.Combine(target, "z.dll")));
    }

    [Fact]
    public async Task ChangedStagingFileAndConcurrentTransactionFailBeforeReplacingInstalledFiles()
    {
        using var directory = new TemporaryDirectory();
        string source = directory.File("new"), target = directory.File("installed");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "app.dll"), "new");
        File.WriteAllText(Path.Combine(target, "app.dll"), "old");
        List<UpdateFile> files = await UpdateInstaller.DescribeTreeAsync(source, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(source, "app.dll"), "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.ApplyFilesAsync(source, target, files, null, null, TestContext.Current.CancellationToken));
        using (UpdateInstaller.OpenInstallLock(target))
            await Assert.ThrowsAsync<IOException>(() => UpdateInstaller.ApplyFilesAsync(source, target, files, null, null, TestContext.Current.CancellationToken));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "app.dll")));
    }

    [Fact]
    public async Task DownloadedPayloadCannotReplaceThePersistentCrossClientInstallationLock()
    {
        using var directory = new TemporaryDirectory();
        string source = directory.File("new"), target = directory.File("installed");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        const string name = ".drcom-update.lock";
        byte[] replacement = "untrusted downloaded lock replacement"u8.ToArray();
        File.WriteAllBytes(Path.Combine(source, name), replacement);
        File.WriteAllText(Path.Combine(target, name), "persistent lock identity");

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.DescribeTreeAsync(source, TestContext.Current.CancellationToken));
        // Also defend a supplied transaction manifest, rather than relying only
        // on the earlier archive/tree validation to protect the held lock inode.
        UpdateFile entry = new(name, replacement.Length, Convert.ToHexString(SHA256.HashData(replacement)).ToLowerInvariant(), 0x180);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.ApplyFilesAsync(
            source, target, [entry], null, null, TestContext.Current.CancellationToken));

        Assert.Equal("persistent lock identity", File.ReadAllText(Path.Combine(target, name)));
        Assert.Empty(Directory.EnumerateDirectories(target));
        using FileStream gate = UpdateInstaller.OpenInstallLock(target);
    }

    [Fact]
    public async Task CancellationBeforeActivationRestoresOriginalFiles()
    {
        using var directory = new TemporaryDirectory();
        string source = directory.File("new"), target = directory.File("installed");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "app.dll"), "new");
        File.WriteAllText(Path.Combine(target, "app.dll"), "old");
        List<UpdateFile> files = await UpdateInstaller.DescribeTreeAsync(source, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateInstaller.ApplyFilesAsync(source, target, files,
            () => { cancellation.Cancel(); return Task.CompletedTask; }, null, cancellation.Token));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "app.dll")));
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows file sharing is enforced by the native OS")]
    public async Task LockedWindowsExecutableIsPreservedAndNoPartialUpdateRemains()
    {
        using var directory = new TemporaryDirectory();
        string source = directory.File("new"), target = directory.File("installed");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "drcom4scutGUI.exe"), "new");
        string executable = Path.Combine(target, "drcom4scutGUI.exe");
        File.WriteAllText(executable, "old");
        List<UpdateFile> files = await UpdateInstaller.DescribeTreeAsync(source, TestContext.Current.CancellationToken);
        using (FileStream locked = new(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => UpdateInstaller.ApplyFilesAsync(source, target, files, null, null, TestContext.Current.CancellationToken));
        Assert.Equal("old", File.ReadAllText(executable));
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows DACL preservation requires the native OS")]
    [SupportedOSPlatform("windows")]
    public async Task WindowsReplacementPreservesExistingDaclAndNewFilesInheritInstallationParent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        string source = directory.File("new"), target = directory.File("installed");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "app.dll"), "new");
        File.WriteAllText(Path.Combine(source, "new.dll"), "new file");
        string installed = Path.Combine(target, "app.dll");
        File.WriteAllText(installed, "old");
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        FileSecurity security = new FileInfo(installed).GetAccessControl();
        security.SetAccessRuleProtection(true, true);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ReadAndExecute | FileSystemRights.Write | FileSystemRights.Delete, AccessControlType.Allow));
        new FileInfo(installed).SetAccessControl(security);
        string before = new FileInfo(installed).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        await UpdateInstaller.ApplyFilesAsync(source, target, await UpdateInstaller.DescribeTreeAsync(source, TestContext.Current.CancellationToken), null, null, TestContext.Current.CancellationToken);
        Assert.Equal(before, new FileInfo(installed).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group));
        FileSecurity added = new FileInfo(Path.Combine(target, "new.dll")).GetAccessControl();
        Assert.False(added.AreAccessRulesProtected);
        Assert.Contains(added.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule => rule.IsInherited);
    }

    [Fact]
    public async Task UnixReplacementPreservesExecutableModeOwnerAndRejectsSymlinkTargets()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        string source = directory.File("new"), target = directory.File("installed");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        string incoming = Path.Combine(source, "app"), installed = Path.Combine(target, "app");
        File.WriteAllText(incoming, "new"); File.WriteAllText(installed, "old");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead;
        File.SetUnixFileMode(incoming, mode);
        UpdateInstaller.UnixIdentity owner = UpdateInstaller.ReadIdentity(installed)!;
        List<UpdateFile> files = [new("app", new FileInfo(incoming).Length, await UpdateInstaller.HashAsync(incoming, TestContext.Current.CancellationToken), (int)mode)];
        await UpdateInstaller.ApplyFilesAsync(source, target, files, null, null, TestContext.Current.CancellationToken, owner);
        Assert.Equal(mode, File.GetUnixFileMode(installed));
        UpdateInstaller.UnixIdentity after = UpdateInstaller.ReadIdentity(installed)!;
        Assert.Equal(owner.User, after.User); Assert.Equal(owner.Group, after.Group);
        string outside = directory.File("outside"); File.WriteAllText(outside, "untouched");
        File.Delete(installed); File.CreateSymbolicLink(installed, outside);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.ApplyFilesAsync(source, target, files, null, null, TestContext.Current.CancellationToken));
        Assert.Equal("untouched", File.ReadAllText(outside));
    }

    [Theory]
    [InlineData("../accounts.json")]
    [InlineData("/tmp/escape")]
    [InlineData("docs/../../escape")]
    [InlineData("docs\\..\\escape")]
    [InlineData("accounts.json")]
    [InlineData("docs/config.yml")]
    [InlineData("drcom4scut")]
    [InlineData("drcom4scut.exe")]
    public void GuiManifestCannotAddressPrivateFilesCoreOrEscapingPaths(string path)
    {
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.ValidateGuiRelativePath(path));
    }

    [Fact]
    public void HelperHasExactFixedArgumentsAndRestartNeverReplaysAuthenticationOrResumeFlags()
    {
        using var directory = new TemporaryDirectory();
        UpdatePlan plan = Plan(directory);
        ProcessStartInfo helper = UpdateInstaller.HelperStartInfo(directory.Path, plan);
        Assert.False(helper.UseShellExecute);
        Assert.Equal(new[] { UpdateHelper.Switch, Path.Combine(directory.Path, "plan.json"), plan.Nonce }, helper.ArgumentList);
        Assert.Empty(UpdateInstaller.RestartStartInfo(plan.TargetExecutable).ArgumentList);
        Assert.Equal(plan.TargetExecutable, UpdateInstaller.RestartStartInfo(plan.TargetExecutable).FileName);
        Assert.True(UpdateHelper.TryRun([UpdateHelper.Switch], out int invalid)); Assert.Equal(2, invalid);
        Assert.True(UpdateHelper.TryRun([UpdateHelper.CapabilitySwitch, "arbitrary", "arguments"], out int denied)); Assert.Equal(1, denied);
    }

    [Fact]
    public void PlansRejectStalePidWrongNonceUnknownJsonAndNullOrPrivilegedFileEntries()
    {
        using var directory = new TemporaryDirectory();
        UpdatePlan plan = Plan(directory);
        UpdateHelper.ValidatePlan(plan, plan.Nonce);
        Assert.Throws<InvalidDataException>(() => UpdateHelper.ValidateParentIdentity(plan.ParentId, plan.ParentStartUtcTicks + 1, plan));
        Assert.Throws<InvalidDataException>(() => UpdateHelper.ValidateParentIdentity(plan.ParentId + 1, plan.ParentStartUtcTicks, plan));
        Assert.Throws<InvalidDataException>(() => UpdateHelper.ValidatePlan(plan, new string('1', 64)));
        string serialized = JsonSerializer.Serialize(plan);
        foreach (string malformed in new[] { "null", serialized.Replace("\"Files\":[", "\"Files\":null,\"Discard\":["),
            serialized.Replace("\"Files\":[", "\"Files\":[null,"), serialized[..^1] + ",\"Command\":\"shell\"}" })
        {
            string path = directory.File("malformed.json"); File.WriteAllText(path, malformed);
            Assert.ThrowsAny<Exception>(() => { UpdatePlan parsed = UpdateHelper.ReadPlan(path); UpdateHelper.ValidatePlan(parsed, plan.Nonce); });
        }
        UpdatePlan privileged = Clone(plan, files: [plan.Files[0] with { Mode = 0xfff }]);
        Assert.Throws<InvalidDataException>(() => UpdateHelper.ValidatePlan(privileged, privileged.Nonce));
        UpdatePlan stale = Clone(plan, created: DateTime.UtcNow.AddHours(-1).Ticks);
        Assert.Throws<InvalidDataException>(() => UpdateHelper.ValidatePlan(stale, stale.Nonce));
    }

    [Fact]
    public async Task FinishedStageCleanupPreservesDiagnosticsLiveHelpersUnsafePathsAndInstallationBackups()
    {
        using var directory = new TemporaryDirectory();
        string root = directory.File("updates"); UpdateInstaller.CreatePrivateDirectory(root);
        UpdatePlan plan = Plan(directory);
        string rollback = directory.File(Path.Combine("installed", ".drcom-update-" + Guid.NewGuid().ToString("N"), "backup"));
        Directory.CreateDirectory(rollback);
        File.WriteAllText(Path.Combine(rollback, "drcom4scutGUI.dll"), "original rollback payload");
        string helper = Path.Combine(AppContext.BaseDirectory, "helpers", OperatingSystem.IsWindows() ? "FakeCore.exe" : "FakeCore");
        using Process exited = new() { StartInfo = new(helper)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        exited.StartInfo.ArgumentList.Add("--version");
        Assert.True(exited.Start());
        await exited.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, exited.ExitCode);

        string Stage(string? status, int pid, bool recent = false, string? name = null)
        {
            string stage = Path.Combine(root, name ?? "update-" + Guid.NewGuid().ToString("N"));
            UpdateInstaller.CreatePrivateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "plan.json"), JsonSerializer.Serialize(plan));
            foreach (string payload in new[] { "new", "helper" })
            {
                Directory.CreateDirectory(Path.Combine(stage, payload));
                File.WriteAllText(Path.Combine(stage, payload, "runtime.dll"), "staged runtime payload");
            }
            if (status is not null)
            {
                string marker = Path.Combine(stage, status);
                File.WriteAllText(marker, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.SetLastWriteTimeUtc(marker, recent ? DateTime.UtcNow : DateTime.UtcNow.AddMinutes(-10));
            }
            return stage;
        }

        string unsafeName = Stage("failed", exited.Id, name: "update-invalid");
        string failed = Stage("failed", exited.Id), complete = Stage("complete", exited.Id);
        string recent = Stage("failed", exited.Id, recent: true), running = Stage("failed", Environment.ProcessId);
        string pending = Stage(null, exited.Id), invalidPid = Stage("failed", 0);
        List<string> retained = [unsafeName, recent, running, pending, invalidPid];
        if (!OperatingSystem.IsWindows())
        {
            string publicStage = Stage("failed", exited.Id);
            File.SetUnixFileMode(publicStage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            retained.Add(publicStage);
            string linkedMarker = Stage("failed", exited.Id);
            string outsideMarker = directory.File("outside-status");
            File.Move(Path.Combine(linkedMarker, "failed"), outsideMarker);
            File.CreateSymbolicLink(Path.Combine(linkedMarker, "failed"), outsideMarker);
            retained.Add(linkedMarker);
            string linkedStage = Stage("failed", exited.Id);
            string outsideStage = directory.File("outside-stage");
            Directory.Move(linkedStage, outsideStage);
            Directory.CreateSymbolicLink(linkedStage, outsideStage);
            retained.Add(linkedStage);
        }

        UpdateHelper.CleanupCompleted(root);

        Assert.False(Directory.Exists(failed));
        Assert.False(Directory.Exists(complete));
        foreach (string stage in retained)
        {
            Assert.True(Directory.Exists(stage), stage);
            Assert.True(File.Exists(Path.Combine(stage, "new", "runtime.dll")), stage);
        }
        Assert.Equal("original rollback payload", File.ReadAllText(Path.Combine(rollback, "drcom4scutGUI.dll")));
    }

    [Fact]
    public async Task HelperDetectsAddedPayloadFilesAndTamperedContents()
    {
        using var directory = new TemporaryDirectory();
        string source = directory.File("new"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "drcom4scutGUI.dll"), "verified");
        UpdatePlan plan = Clone(Plan(directory), files: await UpdateInstaller.DescribeTreeAsync(source, TestContext.Current.CancellationToken));
        await UpdateHelper.VerifyPayloadAsync(source, plan, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(source, "injected.dll"), "unexpected");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateHelper.VerifyPayloadAsync(source, plan, TestContext.Current.CancellationToken));
        File.Delete(Path.Combine(source, "injected.dll"));
        File.WriteAllText(Path.Combine(source, "drcom4scutGUI.dll"), "modified");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateHelper.VerifyPayloadAsync(source, plan, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CapabilityValidationPreservesOnlyTheKnownNetRawSetAndRefusesSilentDowngrades()
    {
        byte[] capability = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(capability, 0x02000001);
        BinaryPrimitives.WriteUInt32LittleEndian(capability.AsSpan(4), 1u << 13);
        Assert.True(UpdateInstaller.ValidateRawCapability(capability));
        BinaryPrimitives.WriteUInt32LittleEndian(capability.AsSpan(4), (1u << 13) | (1u << 12));
        Assert.Throws<IOException>(() => UpdateInstaller.ValidateRawCapability(capability));
        Assert.False(UpdateInstaller.ValidateRawCapability([]));
        Assert.Throws<IOException>(() => UpdateInstaller.ValidateRawCapability([1, 2, 3]));
    }

    [Fact]
    public async Task GuiPackageRaisingMinimumCoreVersionIsRejectedBeforeShutdownOrAnyInstalledFileChanges()
    {
        using var directory = new TemporaryDirectory();
        UpdatePlan plan = Plan(directory);
        string source = directory.File("new"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "BUILD-INFO.json"), "{\"minimum_core_version\":\"0.6.0\"}");
        string core = directory.File(UpdateInstaller.CoreName(UpdateService.NativeRuntime));
        File.WriteAllText(core, "old core");
        File.WriteAllText(directory.File("accounts.json"), "existing private account sentinel");
        using var update = new PreparedUpdate(plan.Release, source, source);
        string? probed = null;
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.ValidateGuiInstallAsync(update, plan.TargetExecutable,
            (path, _) => { probed = path; return Task.FromResult("drcom4scut 0.5.1"); }, TestContext.Current.CancellationToken));
        Assert.Equal(core, probed);
        Assert.Contains("先检查核心更新", failure.Message);
        Assert.Equal("old GUI", File.ReadAllText(plan.TargetExecutable));
        Assert.Equal("old core", File.ReadAllText(core));
        Assert.Equal("existing private account sentinel", File.ReadAllText(directory.File("accounts.json")));
        Assert.Empty(Directory.GetDirectories(directory.Path, ".drcom-update-*"));
    }

    [Fact]
    public async Task GuiHelperChecksCoreCompatibilityUnderTheSharedLockAfterAnotherUpdateRollsBack()
    {
        using var directory = new TemporaryDirectory();
        UpdatePlan plan = Plan(directory);
        string source = directory.File("new"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "BUILD-INFO.json"), "{\"minimum_core_version\":\"0.6.0\"}");
        string core = directory.File(UpdateInstaller.CoreName(UpdateService.NativeRuntime));
        File.WriteAllText(core, "drcom4scut 0.8.0");
        bool probed = false, activated = false;
        Task Activate() { activated = true; return Task.CompletedTask; }
        Task<string> Probe(string path, CancellationToken _)
        {
            probed = true;
            Assert.Throws<IOException>(() => UpdateInstaller.OpenInstallLock(directory.Path));
            return Task.FromResult(File.ReadAllText(path));
        }
        using (UpdateInstaller.OpenInstallLock(directory.Path))
            await Assert.ThrowsAsync<IOException>(() => UpdateHelper.ApplyGuiUpdateAsync(source, plan, Activate, Probe));
        Assert.False(probed); // Never inspect a core installed by an unfinished transaction.

        File.WriteAllText(core, "drcom4scut 0.5.1"); // The other updater failed verification and rolled back.
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateHelper.ApplyGuiUpdateAsync(source, plan, Activate, Probe));
        Assert.True(probed);
        Assert.Contains("先检查核心更新", failure.Message);
        Assert.False(activated);
        Assert.Equal("old GUI", File.ReadAllText(plan.TargetExecutable));
        Assert.Equal("drcom4scut 0.5.1", File.ReadAllText(core));
        Assert.Empty(Directory.GetDirectories(directory.Path, ".drcom-update-*"));
    }

    [Fact]
    public async Task MacWholeAppTransactionPreservesAdjacentCoreAndRollsBackFailedRestart()
    {
        // This structural transaction test runs off macOS too; native signature checks
        // are covered by the actual signed package test on the macOS matrix.
        if (OperatingSystem.IsMacOS()) return;
        using var directory = new TemporaryDirectory();
        string source = directory.File("new"), target = directory.File("installed");
        string relative = Path.Combine("drcom4scutGUI.app", "Contents", "MacOS", "drcom4scutGUI");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, relative))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, relative))!);
        File.WriteAllText(Path.Combine(source, relative), "new signed app fixture");
        File.WriteAllText(Path.Combine(target, relative), "old signed app fixture");
        File.WriteAllText(Path.Combine(target, "drcom4scut"), "adjacent core");
        await Assert.ThrowsAsync<IOException>(() => UpdateHelper.ApplyMacAppAsync(source, Path.Combine(target, relative), () => throw new IOException("restart failed")));
        Assert.Equal("old signed app fixture", File.ReadAllText(Path.Combine(target, relative)));
        Assert.Equal("adjacent core", File.ReadAllText(Path.Combine(target, "drcom4scut")));
    }

    public static bool HasNativePackage => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DRCOM_GUI_UPDATE_TEST_ARCHIVE"));

    [Fact(SkipUnless = nameof(HasNativePackage), Skip = "The native CI packaging job supplies its exact archive")]
    public async Task NativePackagedGuiUpdateInstallsAndPreservesCoreAndConfiguration()
    {
        string archive = Path.GetFullPath(Environment.GetEnvironmentVariable("DRCOM_GUI_UPDATE_TEST_ARCHIVE")!);
        string commit = Environment.GetEnvironmentVariable("DRCOM_GUI_UPDATE_TEST_SOURCE") ?? throw new InvalidOperationException("Missing package source commit");
        string runtime = UpdateService.NativeRuntime;
        string name = Path.GetFileName(archive), suffix = "-" + runtime + (runtime.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz");
        Assert.StartsWith("drcom4scutGUI-", name); Assert.EndsWith(suffix, name);
        string version = name["drcom4scutGUI-".Length..^suffix.Length];
        UpdateRelease release = Release(version, runtime, "sha256:" + (await UpdateInstaller.HashAsync(archive, TestContext.Current.CancellationToken)).ToLowerInvariant()) with
        { AssetSize = new FileInfo(archive).Length, SourceCommit = commit };
        UpdateService.ValidateRelease(release);
        using var directory = new TemporaryDirectory();
        string source = await UpdateArchive.ExtractAsync(archive, directory.File("extracted"), release, TestContext.Current.CancellationToken);
        await UpdateArchive.VerifyAsync(source, release, TestContext.Current.CancellationToken);
        string target = directory.File("installed");
        await UpdateInstaller.CopyTreeAsync(source, target, TestContext.Current.CancellationToken);
        string core = Path.Combine(target, UpdateInstaller.CoreName(runtime)), accounts = Path.Combine(target, "accounts.json"), config = Path.Combine(target, "config.yml");
        File.WriteAllText(core, "existing unrelated core sentinel");
        File.WriteAllText(accounts, "synthetic encrypted account sentinel");
        File.WriteAllText(config, "existing private configuration sentinel");
        List<UpdateFile> files = await UpdateInstaller.DescribeTreeAsync(source, TestContext.Current.CancellationToken);
        if (runtime.StartsWith("osx-", StringComparison.Ordinal))
            await UpdateHelper.ApplyMacAppAsync(source, UpdateInstaller.GuiLaunchPath(target, runtime), () => Task.CompletedTask);
        else
            await UpdateInstaller.ApplyFilesAsync(source, target, files, null, null, TestContext.Current.CancellationToken);
        await UpdateArchive.VerifyAsync(target, release, TestContext.Current.CancellationToken);
        Assert.Equal("existing unrelated core sentinel", File.ReadAllText(core));
        Assert.Equal("synthetic encrypted account sentinel", File.ReadAllText(accounts));
        Assert.Equal("existing private configuration sentinel", File.ReadAllText(config));
        Assert.True(File.Exists(UpdateInstaller.GuiPayloadPath(target, runtime)));
    }

    private static UpdateRelease Release(string version, string runtime, string? digest = null)
    {
        string name = UpdateService.ArchiveName(UpdateComponent.Gui, version, runtime);
        return new(UpdateComponent.Gui, version, "v" + version, runtime,
            new Uri("https://github.com/SeaLoong/drcom4scutGUI/releases/tag/v" + version), name,
            new Uri("https://github.com/SeaLoong/drcom4scutGUI/releases/download/v" + version + "/" + name), 100,
            digest ?? "sha256:" + new string('a', 64)) { SourceCommit = Commit };
    }

    private static UpdatePlan Plan(TemporaryDirectory directory)
    {
        string executable = UpdateInstaller.GuiLaunchPath(directory.Path, UpdateService.NativeRuntime);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!); File.WriteAllText(executable, "old GUI");
        return new() { Release = Release("3.2.1", UpdateService.NativeRuntime), Nonce = new string('a', 64), ParentId = Environment.ProcessId + 1,
            ParentStartUtcTicks = DateTime.UtcNow.Ticks, CreatedUtcTicks = DateTime.UtcNow.Ticks, TargetExecutable = executable,
            TargetSha256 = new string('b', 64), Files = [new(Path.GetRelativePath(".", UpdateInstaller.GuiLaunchPath(".", UpdateService.NativeRuntime)), 1, new string('c', 64), 0x1ed)] };
    }

    private static UpdatePlan Clone(UpdatePlan plan, List<UpdateFile>? files = null, long? created = null) => new()
    {
        Release = plan.Release, Nonce = plan.Nonce, ParentId = plan.ParentId, ParentStartUtcTicks = plan.ParentStartUtcTicks,
        CreatedUtcTicks = created ?? plan.CreatedUtcTicks, TargetExecutable = plan.TargetExecutable, TargetSha256 = plan.TargetSha256, Files = files ?? plan.Files
    };
}
