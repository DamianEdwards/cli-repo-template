using TemplateCli.Commands;
using TemplateCli.Infrastructure;
using TemplateCli.Models;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace TemplateCli.Services;

/// <summary>
/// Result of checking for an available update.
/// </summary>
public sealed record UpdateCheckResult(
    string CurrentVersion,
    string AvailableVersion,
    string ReleaseTag,
    bool IsDevBuild);

public sealed record StartupRepairResult(
    bool Succeeded,
    string Message,
    bool RelaunchRequired = false);

/// <summary>
/// Orchestrates the self-update lifecycle: check → download → verify → stage → install.
/// </summary>
public sealed class UpdateService
{
    private readonly StateStore _stateStore;
    private readonly GitHubReleaseService _releaseService;
    private readonly ProvenanceVerifier _provenanceVerifier;
    private readonly ILogger<UpdateService> _logger;

    /// <summary>Minimum interval between automatic update checks.</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(1);

    /// <summary>Maximum time to wait for the daemon to exit during staged install.</summary>
    private static readonly TimeSpan DaemonExitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Maximum consecutive failures before backing off significantly.</summary>
    private const int MaxBackoffFailures = 5;

    public UpdateService(
        StateStore stateStore,
        GitHubReleaseService releaseService,
        ProvenanceVerifier provenanceVerifier,
        ILogger<UpdateService> logger)
    {
        _stateStore = stateStore;
        _releaseService = releaseService;
        _provenanceVerifier = provenanceVerifier;
        _logger = logger;
    }

    public static bool HasUsableStagedUpdate(UpdateState state)
        => (state.Status == UpdateStatus.Staged || state.Status == UpdateStatus.WaitingForExit)
           && ((!string.IsNullOrEmpty(state.StagedDirectory) && Directory.Exists(state.StagedDirectory))
               || (!string.IsNullOrEmpty(state.StagedPath) && File.Exists(state.StagedPath)));

    public bool TryScheduleDeferredInstall(int waitForPid, DateTimeOffset waitForStartTime, int watcherPid, DateTimeOffset? watcherStartTime)
    {
        if (!_stateStore.TryAcquireUpdateLock())
        {
            _logger.LogDebug("Another update operation is in progress, cannot record deferred installer state");
            return false;
        }

        try
        {
            var state = _stateStore.LoadUpdateState();
            if (!HasUsableStagedUpdate(state))
            {
                _logger.LogDebug("No staged update is available to defer");
                return false;
            }

            state.Status = UpdateStatus.WaitingForExit;
            state.WaitForPid = waitForPid;
            state.WaitForStartTime = waitForStartTime;
            state.WatcherPid = watcherPid;
            state.WatcherStartTime = watcherStartTime;
            state.ErrorMessage = null;
            _stateStore.SaveUpdateState(state);
            return true;
        }
        finally
        {
            _stateStore.ReleaseUpdateLock();
        }
    }

    public async Task<StartupRepairResult?> RepairInterruptedInstallAsync(bool skipProvenance, CancellationToken ct)
    {
        if (!_stateStore.TryAcquireUpdateLock())
        {
            return new StartupRepairResult(
                false,
                $"A self-update is already in progress. Wait for it to finish before starting {AppIdentity.CommandName}.");
        }

        try
        {
        var currentExePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExePath))
            return null;

        var installDir = Path.GetDirectoryName(currentExePath)!;
        var binaryName = AppIdentity.GetExecutableFileName();
        var oldPath = Path.Combine(installDir, $"{binaryName}.old");
        var defaultStagedPath = Path.Combine(installDir, $"{binaryName}.staged");

        var state = _stateStore.LoadUpdateState();
        if (state.Status != UpdateStatus.Installing)
        {
            if (!File.Exists(oldPath))
                return null;

            CleanupOldBinary();
            return File.Exists(oldPath)
                ? null
                : new StartupRepairResult(true, "Cleaned up leftover backup binary from a previous self-update.");
        }

        if (string.IsNullOrWhiteSpace(state.StagedPath))
        {
            ClearInterruptedInstallArtifactsCore(defaultStagedPath);
            CleanupOldBinary();
            return new StartupRepairResult(
                true,
                "Cleared interrupted self-update state because the staged path metadata was missing.");
        }

        var stagedPath = state.StagedPath;
        if (!File.Exists(stagedPath))
        {
            ClearInterruptedInstallArtifactsCore(stagedPath);
            CleanupOldBinary();
            return new StartupRepairResult(
                true,
                "Cleared interrupted self-update state because the staged binary was missing.");
        }

        var currentVersionString = VersionHelper.GetCurrentVersion();
        if (currentVersionString is null || !VersionHelper.TryParse(currentVersionString, out var currentVersion))
        {
            ClearInterruptedInstallArtifactsCore(stagedPath);
            CleanupOldBinary();
            return new StartupRepairResult(
                true,
                "Cleared interrupted self-update state because the current binary version could not be determined safely.");
        }

        if (string.IsNullOrWhiteSpace(state.StagedVersion)
            || !VersionHelper.TryParse(state.StagedVersion, out var stagedVersion))
        {
            ClearInterruptedInstallArtifactsCore(stagedPath);
            CleanupOldBinary();
            return new StartupRepairResult(
                true,
                "Cleared interrupted self-update state because the staged version metadata was invalid.");
        }

        if (stagedVersion.CompareTo(currentVersion) <= 0)
        {
            ClearInterruptedInstallArtifactsCore(stagedPath);
            CleanupOldBinary();
            return new StartupRepairResult(
                true,
                $"Discarded stale staged update {state.StagedVersion} because the current binary is already {currentVersionString} or newer.");
        }

        _logger.LogWarning(
            "Resuming interrupted self-update install of {StagedVersion} over current binary {CurrentVersion}",
            state.StagedVersion,
            currentVersionString);

        using var installWindowLock = _stateStore.TryAcquireInstallWindowLock();
        if (installWindowLock is null)
        {
            return new StartupRepairResult(
                false,
                $"Another {AppIdentity.CommandName} instance started while startup repair was running. Retry the command after it exits.");
        }

        var installed = await InstallStagedCoreAsync(
            skipProvenance,
            waitForPid: null,
            waitForStartTime: null,
            allowDaemonShutdown: true,
            ct);

        if (installed)
        {
            return new StartupRepairResult(
                true,
                $"Recovered interrupted self-update install and applied staged version {state.StagedVersion}.",
                RelaunchRequired: true);
        }

        var failedState = _stateStore.LoadUpdateState();
        return new StartupRepairResult(
            false,
            $"Detected interrupted self-update install but automatic recovery failed: {failedState.ErrorMessage ?? "unknown error"}.");
        }
        finally
        {
            _stateStore.ReleaseUpdateLock();
        }
    }

    /// <summary>
    /// Checks whether an update is available from GitHub Releases.
    /// </summary>
    public UpdateCheckResult? CheckForUpdate(bool allowPreRelease, bool stableOnly = false)
    {
        ThrowIfDisabled();
        var currentVersionStr = VersionHelper.GetCurrentVersion();
        if (currentVersionStr is null || !VersionHelper.TryParse(currentVersionStr, out var currentVersion))
        {
            _logger.LogWarning("Could not determine current version");
            return null;
        }

        var assetName = GitHubReleaseService.GetPlatformAssetName();
        var release = _releaseService.GetLatestRelease(
            currentVersion,
            allowPreRelease,
            stableOnly,
            assetName);
        if (release is null)
        {
            _logger.LogDebug("No matching release found");
            return null;
        }

        var candidateVersionStr = release.TagName;

        if (!VersionHelper.TryParse(candidateVersionStr, out var candidateVersion))
        {
            _logger.LogDebug("Could not parse candidate version '{Version}' from release tag", candidateVersionStr);
            return null;
        }

        _logger.LogInformation("Update available: {Current} → {Available}", currentVersionStr, candidateVersionStr);
        return new UpdateCheckResult(currentVersionStr, candidateVersionStr, release.TagName,
            VersionHelper.IsDevBuild(candidateVersion));
    }

    /// <summary>
    /// Downloads, verifies, and stages an update binary for later installation.
    /// </summary>
    public async Task<bool> DownloadAndStageAsync(UpdateCheckResult update, bool skipProvenance, CancellationToken ct)
    {
        var state = _stateStore.LoadUpdateState();
        state.Status = UpdateStatus.Downloading;
        state.AvailableVersion = update.AvailableVersion;
        state.CurrentReleaseTag = update.ReleaseTag;
        _stateStore.SaveUpdateState(state);

        var assetName = GitHubReleaseService.GetPlatformAssetName();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{AppIdentity.CommandName}-update-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempRoot);

            // Download the main asset
            if (!_releaseService.DownloadReleaseAsset(update.ReleaseTag, assetName, tempRoot))
            {
                RecordFailure(state, "Failed to download release asset");
                return false;
            }

            var archivePath = Path.Combine(tempRoot, assetName);
            var extractPath = Path.Combine(tempRoot, "extract");

            // Always verify checksums — they protect against download corruption
            if (!_releaseService.DownloadReleaseAsset(update.ReleaseTag, "checksums.txt", tempRoot))
            {
                RecordFailure(state, "Failed to download checksums.txt");
                return false;
            }

            var checksumsPath = Path.Combine(tempRoot, "checksums.txt");
            var (checksumOk, checksumErr) = _provenanceVerifier.VerifyChecksum(archivePath, checksumsPath, assetName);
            if (!checksumOk)
            {
                RecordFailure(state, checksumErr ?? "Checksum verification failed");
                return false;
            }

            if (!_releaseService.DownloadReleaseAsset(update.ReleaseTag, "release-metadata.json", tempRoot))
            {
                RecordFailure(state, "Failed to download release-metadata.json");
                return false;
            }

            var metadataPath = Path.Combine(tempRoot, "release-metadata.json");
            var expectedHash = GetExpectedHash(checksumsPath, assetName);
            var (metaOk, windowsPayloadSigned, metaErr) = _provenanceVerifier.ValidateReleaseMetadata(
                metadataPath,
                assetName,
                expectedHash,
                update.AvailableVersion);
            if (!metaOk)
            {
                RecordFailure(state, metaErr ?? "Release metadata validation failed");
                return false;
            }

            // On Windows, Authenticode applies to the extracted executable rather than the ZIP container.
            // On Linux/macOS, official release archives are attested in the tag-bound release workflow.
            if (!update.IsDevBuild
                && !skipProvenance
                && (!OperatingSystem.IsWindows() || !windowsPayloadSigned))
            {
                if (!_releaseService.DownloadReleaseAsset(
                        update.ReleaseTag,
                        "attestations.jsonl",
                        tempRoot))
                {
                    RecordFailure(state, "Release does not contain attestations.jsonl");
                    return false;
                }

                var (archiveTrustOk, archiveTrustErr) =
                    await _provenanceVerifier.VerifyArchiveAttestationAsync(
                        archivePath,
                        $"refs/tags/{update.ReleaseTag}",
                        Path.Combine(tempRoot, "attestations.jsonl"),
                        ct);
                if (!archiveTrustOk)
                {
                    RecordFailure(state, archiveTrustErr ?? "Archive provenance verification failed");
                    return false;
                }
            }

            // Extract archive
            var extractedBinary = GitHubReleaseService.ExtractReleaseArchive(archivePath, extractPath);
            PayloadInstaller.ValidateManifest(extractPath);

            // Verify every executable payload file on Windows.
            if (OperatingSystem.IsWindows()
                && windowsPayloadSigned
                && !update.IsDevBuild
                && !skipProvenance)
            {
                var (trustOk, trustErr) =
                    await _provenanceVerifier.VerifyWindowsPayloadAsync(extractPath, ct);
                if (!trustOk)
                {
                    RecordFailure(state, trustErr ?? "Provenance verification failed");
                    return false;
                }
            }

            await PayloadInstaller.VerifyInstalledPayloadAsync(
                extractPath,
                update.AvailableVersion,
                _logger,
                ct);

            // Stage the complete verified payload next to the install directory.
            var currentExePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine current executable path");
            var installDir = Path.GetDirectoryName(currentExePath)!;
            var binaryName = AppIdentity.GetExecutableFileName();
            var parentDir = Path.GetDirectoryName(installDir)!;
            var stagedDirectory = Path.Combine(
                parentDir,
                $".{Path.GetFileName(installDir)}.new-{Guid.NewGuid():N}");
            var payloadFiles = PayloadInstaller.ValidateManifest(extractPath);
            PayloadInstaller.CopyFiles(
                extractPath,
                stagedDirectory,
                payloadFiles.Append(PayloadInstaller.ManifestFileName));
            var stagedPath = Path.Combine(stagedDirectory, binaryName);

            state.Status = UpdateStatus.Staged;
            state.StagedVersion = update.AvailableVersion;
            state.StagedPath = stagedPath;
            state.StagedDirectory = stagedDirectory;
            state.BackupDirectory = Path.Combine(
                parentDir,
                $".{Path.GetFileName(installDir)}.old-{Guid.NewGuid():N}");
            state.WindowsPayloadSigned = windowsPayloadSigned;
            ClearDeferredInstallMetadata(state);
            state.LastCheckTime = DateTimeOffset.UtcNow;
            state.ErrorMessage = null;
            state.FailureCount = 0;
            _stateStore.SaveUpdateState(state);

            _logger.LogInformation("Update {Version} staged at '{Path}'", update.AvailableVersion, stagedPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download and stage update");
            RecordFailure(state, ex.Message);
            return false;
        }
        finally
        {
            // Clean up temp directory
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to clean up temp directory"); }
        }
    }

    /// <summary>
    /// Installs a previously staged binary.
    /// Waits for any running daemon to exit, then performs atomic binary replacement with rollback.
    /// </summary>
    public async Task<bool> InstallStagedAsync(
        bool skipProvenance,
        int? waitForPid,
        DateTimeOffset? waitForStartTime,
        bool allowDaemonShutdown,
        CancellationToken ct)
    {
        var passiveWaitTarget = default((int Pid, DateTimeOffset StartTime)?);
        if (!allowDaemonShutdown)
        {
            if (waitForPid is null || waitForStartTime is null)
            {
                _logger.LogWarning("Passive staged install requires an explicit daemon PID and start time");
                return false;
            }

            passiveWaitTarget = (waitForPid.Value, waitForStartTime.Value);
        }

        while (true)
        {
            if (passiveWaitTarget is { } waitTarget
                && !await WaitForDaemonChainToExitAsync(waitTarget.Pid, waitTarget.StartTime, ct))
            {
                return false;
            }

            if (!_stateStore.TryAcquireUpdateLock())
            {
                _logger.LogWarning("Another update operation is in progress, cannot install staged update");
                return false;
            }

            FileStream? installWindowLock = null;
            try
            {
                if (passiveWaitTarget is not null)
                {
                    installWindowLock = _stateStore.TryAcquireInstallWindowLock();
                    if (installWindowLock is null)
                    {
                        var activeDaemon = _stateStore.ReadDaemonPid();
                        if (activeDaemon is { } nextTarget)
                        {
                            _logger.LogInformation(
                                "Daemon PID {Pid} started before deferred install could begin, continuing to wait for it to exit naturally",
                                nextTarget.Pid);
                            passiveWaitTarget = (nextTarget.Pid, nextTarget.StartTime);
                        }
                        else
                        {
                            _logger.LogInformation("A daemon started before deferred install could begin, retrying wait for the active instance");
                        }

                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                        continue;
                    }
                }

                return await InstallStagedCoreAsync(skipProvenance, waitForPid, waitForStartTime, allowDaemonShutdown, ct);
            }
            finally
            {
                installWindowLock?.Dispose();
                _stateStore.ReleaseUpdateLock();
            }
        }
    }

    private async Task<bool> InstallStagedCoreAsync(
        bool skipProvenance,
        int? waitForPid,
        DateTimeOffset? waitForStartTime,
        bool allowDaemonShutdown,
        CancellationToken ct)
    {
        var state = _stateStore.LoadUpdateState();
        if ((state.Status != UpdateStatus.Staged
             && state.Status != UpdateStatus.WaitingForExit
             && state.Status != UpdateStatus.Installing)
            || string.IsNullOrEmpty(state.StagedDirectory))
        {
            _logger.LogWarning("No staged update found to install");
            return false;
        }

        if (!Directory.Exists(state.StagedDirectory))
        {
            _logger.LogWarning("Staged payload not found at '{Path}'", state.StagedDirectory);
            RecordFailure(state, "Staged payload missing");
            return false;
        }

        if (allowDaemonShutdown)
        {
            var daemonPid = waitForPid is not null && waitForStartTime is not null
                ? (Pid: waitForPid.Value, StartTime: waitForStartTime.Value, LogInstanceId: (string?)null)
                : _stateStore.ReadDaemonPid();

            if (daemonPid is not null)
            {
                _logger.LogInformation("Waiting for daemon (PID {Pid}) to exit...", daemonPid.Value.Pid);
                if (!await WaitForProcessExitAsync(daemonPid.Value.Pid, daemonPid.Value.StartTime, DaemonExitTimeout, ct))
                {
                    // Try graceful shutdown first
                    _logger.LogWarning("Daemon did not exit within timeout, attempting graceful shutdown");
                    if (!TryGracefulShutdown(daemonPid.Value.Pid))
                    {
                        // Final fallback: force kill
                        _logger.LogWarning("Graceful shutdown failed, force-killing daemon");
                        try
                        {
                            var proc = System.Diagnostics.Process.GetProcessById(daemonPid.Value.Pid);
                            proc.Kill(entireProcessTree: true);
                            proc.WaitForExit(5000);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to terminate daemon process");
                        }
                    }
                }
            }
        }

        state.Status = UpdateStatus.Installing;
        state.LastAttemptTime = DateTimeOffset.UtcNow;
        ClearDeferredInstallMetadata(state);
        _stateStore.SaveUpdateState(state);

        // Re-verify every staged executable before install on Windows.
        if (OperatingSystem.IsWindows()
            && state.WindowsPayloadSigned
            && !skipProvenance
            && state.StagedVersion is not null)
        {
            if (VersionHelper.TryParse(state.StagedVersion, out var stagedVer) && !VersionHelper.IsDevBuild(stagedVer))
            {
                var (trustOk, trustErr) =
                    await _provenanceVerifier.VerifyWindowsPayloadAsync(state.StagedDirectory, ct);
                if (!trustOk)
                {
                    RecordFailure(state, $"Pre-install provenance check failed: {trustErr}");
                    return false;
                }
            }
        }

        var currentExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine current executable path");
        var installDir = Path.GetDirectoryName(currentExePath)!;
        var stagedDirectory = state.StagedDirectory;
        var backupDirectory = state.BackupDirectory
            ?? Path.Combine(
                Path.GetDirectoryName(installDir)!,
                $".{Path.GetFileName(installDir)}.old-{Guid.NewGuid():N}");
        state.BackupDirectory = backupDirectory;
        _stateStore.SaveUpdateState(state);

        var newFiles = PayloadInstaller.ValidateManifest(stagedDirectory);
        var managedFiles = PayloadInstaller.ReadManagedFiles(installDir)
            .Concat(newFiles)
            .Append(PayloadInstaller.ManifestFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mutationStarted = false;

        try
        {
            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(backupDirectory);
            mutationStarted = true;
            PayloadInstaller.BackupFiles(installDir, backupDirectory, managedFiles);
            PayloadInstaller.DeleteFiles(installDir, managedFiles);
            PayloadInstaller.CopyFiles(
                stagedDirectory,
                installDir,
                newFiles.Append(PayloadInstaller.ManifestFileName));
            await PayloadInstaller.VerifyInstalledPayloadAsync(
                installDir,
                state.StagedVersion
                    ?? throw new UserFacingException("Staged update version is missing."),
                _logger,
                ct);

            _stateStore.ClearUpdateState();
            TryDeletePayloadDirectory(stagedDirectory);
            TryDeletePayloadDirectory(backupDirectory);
            _logger.LogInformation("Update to {Version} installed successfully", state.StagedVersion);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install staged binary, attempting rollback");

            try
            {
                if (mutationStarted)
                    PayloadInstaller.RestoreFiles(installDir, backupDirectory, managedFiles);
                _logger.LogInformation("Rollback succeeded - restored previous managed payload");
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(rollbackEx, "CRITICAL: Rollback failed! Manual intervention required.");
            }

            RecordFailure(state, $"Install failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Convenience method for daemon use: check for update + download + stage in one call.
    /// </summary>
    public async Task<bool> CheckAndStageAsync(bool allowPreRelease, bool skipProvenance, CancellationToken ct)
    {
        ThrowIfDisabled();
        var state = _stateStore.LoadUpdateState();

        // Don't re-check if already staged or waiting for a deferred install
        if (HasUsableStagedUpdate(state))
        {
            _logger.LogDebug("Update already staged, skipping check");
            return true;
        }

        // Backoff on repeated failures (takes priority over normal throttle)
        if (state.FailureCount > 0 && state.LastAttemptTime is not null)
        {
            var backoff = TimeSpan.FromMinutes(Math.Pow(2, Math.Min(state.FailureCount, MaxBackoffFailures)));
            if (DateTimeOffset.UtcNow - state.LastAttemptTime.Value < backoff)
            {
                _logger.LogDebug("Backing off update check (failure #{Count}, next attempt after {Backoff})",
                    state.FailureCount, backoff);
                return false;
            }
        }

        // Don't check too frequently (only applies when there are no failures)
        if (state.FailureCount == 0 && state.LastCheckTime is not null
            && DateTimeOffset.UtcNow - state.LastCheckTime.Value < UpdateCheckInterval)
        {
            _logger.LogDebug("Skipping update check — last check was {Ago} ago",
                DateTimeOffset.UtcNow - state.LastCheckTime.Value);
            return false;
        }

        if (!_stateStore.TryAcquireUpdateLock())
        {
            _logger.LogDebug("Another update operation is in progress");
            return false;
        }

        try
        {
            state.Status = UpdateStatus.Checking;
            _stateStore.SaveUpdateState(state);

            var update = CheckForUpdate(allowPreRelease);
            if (update is null)
            {
                state.Status = UpdateStatus.None;
                state.LastCheckTime = DateTimeOffset.UtcNow;
                _stateStore.SaveUpdateState(state);
                return false;
            }

            return await DownloadAndStageAsync(update, skipProvenance, ct);
        }
        finally
        {
            _stateStore.ReleaseUpdateLock();
        }
    }

    /// <summary>
    /// Cleans up old binary left over from a previous update.
    /// </summary>
    public void CleanupOldBinary()
    {
        var currentExePath = Environment.ProcessPath;
        if (currentExePath is null) return;

        var binaryName = AppIdentity.GetExecutableFileName();
        var oldPath = Path.Combine(Path.GetDirectoryName(currentExePath)!, $"{binaryName}.old");
        if (File.Exists(oldPath))
        {
            try
            {
                File.Delete(oldPath);
                _logger.LogInformation("Cleaned up old binary '{Path}'", oldPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clean up old binary (may still be in use)");
            }
        }

        var state = _stateStore.LoadUpdateState();
        var parentDirectory = Path.GetDirectoryName(Path.GetDirectoryName(currentExePath)!);
        if (parentDirectory is null)
            return;

        var installDirectoryName = Path.GetFileName(Path.GetDirectoryName(currentExePath)!);
        foreach (var pattern in new[] { $".{installDirectoryName}.new-*", $".{installDirectoryName}.old-*" })
        {
            foreach (var directory in Directory.EnumerateDirectories(parentDirectory, pattern))
            {
                if (PathsEqual(directory, state.StagedDirectory)
                    || PathsEqual(directory, state.BackupDirectory))
                {
                    continue;
                }

                TryDeletePayloadDirectory(directory);
            }
        }
    }

    private void RecordFailure(UpdateState state, string message)
    {
        state.Status = UpdateStatus.Failed;
        ClearDeferredInstallMetadata(state);
        state.ErrorMessage = message;
        state.FailureCount++;
        state.LastAttemptTime = DateTimeOffset.UtcNow;
        _stateStore.SaveUpdateState(state);
        _logger.LogWarning("Update failed (attempt #{Count}): {Message}", state.FailureCount, message);
    }

    private void ClearInterruptedInstallArtifacts(string stagedPath)
    {
        if (!_stateStore.TryAcquireUpdateLock())
        {
            _logger.LogWarning("Could not acquire the update lock to clear interrupted install artifacts");
            return;
        }

        try
        {
            var state = _stateStore.LoadUpdateState();
            TryDeletePayloadDirectory(state.StagedDirectory);
            TryDeletePayloadDirectory(state.BackupDirectory);
            try
            {
                if (File.Exists(stagedPath))
                    File.Delete(stagedPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete stale staged binary '{Path}'", stagedPath);
            }

            _stateStore.ClearUpdateState();
        }
        finally
        {
            _stateStore.ReleaseUpdateLock();
        }
    }

    private void ClearInterruptedInstallArtifactsCore(string stagedPath)
    {
        var state = _stateStore.LoadUpdateState();
        TryDeletePayloadDirectory(state.StagedDirectory);
        TryDeletePayloadDirectory(state.BackupDirectory);
        try
        {
            if (File.Exists(stagedPath))
                File.Delete(stagedPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete stale staged binary '{Path}'", stagedPath);
        }

        _stateStore.ClearUpdateState();
    }

    private void TryDeletePayloadDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        var currentExePath = Environment.ProcessPath;
        if (currentExePath is null)
            return;

        var installDirectory = Path.GetDirectoryName(currentExePath)!;
        var expectedParent = Path.GetDirectoryName(installDirectory);
        var directoryParent = Path.GetDirectoryName(Path.GetFullPath(directory));
        var directoryName = Path.GetFileName(directory);
        var expectedPrefix = $".{Path.GetFileName(installDirectory)}.";
        if (expectedParent is null
            || !string.Equals(expectedParent, directoryParent, StringComparison.OrdinalIgnoreCase)
            || (!directoryName.StartsWith($"{expectedPrefix}new-", StringComparison.Ordinal)
                && !directoryName.StartsWith($"{expectedPrefix}old-", StringComparison.Ordinal)))
        {
            _logger.LogWarning("Refusing to delete unexpected update payload directory '{Path}'", directory);
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Update payload directory '{Path}' remains in use and will be retried later", directory);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Update payload directory '{Path}' could not be removed and will be retried later", directory);
        }
    }

    private static bool PathsEqual(string path, string? other)
        => !string.IsNullOrWhiteSpace(other)
            && string.Equals(
                Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(other).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    private async Task<bool> WaitForDaemonChainToExitAsync(int initialPid, DateTimeOffset initialStartTime, CancellationToken ct)
    {
        var currentTarget = (Pid: initialPid, StartTime: initialStartTime);

        while (true)
        {
            _logger.LogInformation("Waiting for daemon (PID {Pid}) to exit naturally before installing staged update...", currentTarget.Pid);
            if (!await WaitForProcessExitAsync(currentTarget.Pid, currentTarget.StartTime, timeout: null, ct))
                return false;

            while (_stateStore.IsLockHeld())
            {
                var activeDaemon = _stateStore.ReadDaemonPid();
                if (activeDaemon is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                if (IsSameTrackedProcess(activeDaemon.Value.Pid, activeDaemon.Value.StartTime, currentTarget.Pid, currentTarget.StartTime))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                currentTarget = (activeDaemon.Value.Pid, activeDaemon.Value.StartTime);
                goto ContinueWaiting;
            }

            return true;

        ContinueWaiting:
            continue;
        }
    }

    /// <summary>
    /// Attempts graceful shutdown of the daemon.
    /// Windows: spawns a hidden <c>shutdown-instance</c> helper (required for console attachment).
    /// Unix: sends SIGINT directly via <c>libc kill()</c>.
    /// </summary>
    private bool TryGracefulShutdown(int pid)
    {
        if (!OperatingSystem.IsWindows())
            return TryGracefulShutdownUnix(pid);

        var processPath = Environment.ProcessPath;
        if (processPath is null)
        {
            _logger.LogDebug("Cannot determine executable path for graceful shutdown");
            return false;
        }

        _logger.LogDebug("Spawning shutdown-instance helper for PID {Pid}", pid);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = processPath,
                Arguments = $"shutdown-instance --pid {pid} --signal-profile daemon",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var helper = System.Diagnostics.Process.Start(psi);
            if (helper is null)
            {
                _logger.LogDebug("Failed to start shutdown-instance helper");
                return false;
            }

            if (helper.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                if (ShutdownInstanceCommand.IsSuccessExitCode(helper.ExitCode))
                {
                    var outcome = ShutdownInstanceCommand.DescribeExitCode(helper.ExitCode);
                    if (ShutdownInstanceCommand.UsedFallbackKillExitCode(helper.ExitCode))
                        _logger.LogWarning("Daemon PID {Pid} terminated after shutdown-instance {Outcome}", pid, outcome);
                    else
                        _logger.LogInformation("Daemon PID {Pid} terminated via graceful shutdown ({Outcome})", pid, outcome);
                    return true;
                }

                _logger.LogDebug("shutdown-instance exited with code {Code} ({Outcome})",
                    helper.ExitCode, ShutdownInstanceCommand.DescribeExitCode(helper.ExitCode));
            }
            else
            {
                _logger.LogDebug("shutdown-instance timed out");
                try { helper.Kill(); } catch { }
            }

            // Check if daemon actually exited
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(pid);
                if (proc.HasExited) return true;
                proc.Dispose();
            }
            catch (ArgumentException)
            {
                return true; // Process not found = exited
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Graceful shutdown attempt failed");
        }

        return false;
    }

    /// <summary>
    /// Sends SIGINT to the daemon process on Unix for graceful shutdown,
    /// followed by SIGKILL if the process doesn't exit promptly.
    /// </summary>
    private bool TryGracefulShutdownUnix(int pid)
    {
        _logger.LogDebug("Sending SIGINT to daemon PID {Pid}", pid);
        try
        {
            // Send SIGINT for graceful shutdown
            if (NativeInterop.sys_kill(pid, NativeInterop.SIGINT) != 0)
            {
                _logger.LogDebug("SIGINT failed for PID {Pid}", pid);
                return false;
            }

            // Wait up to 10 seconds for the process to exit
            for (var i = 0; i < 20; i++)
            {
                Thread.Sleep(500);
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    if (proc.HasExited)
                    {
                        _logger.LogInformation("Daemon PID {Pid} terminated via SIGINT", pid);
                        return true;
                    }
                    proc.Dispose();
                }
                catch (ArgumentException)
                {
                    // Process not found = already exited
                    _logger.LogInformation("Daemon PID {Pid} terminated via SIGINT", pid);
                    return true;
                }
            }

            // Fallback: SIGKILL
            _logger.LogWarning("Daemon did not exit after SIGINT, sending SIGKILL to PID {Pid}", pid);
            NativeInterop.sys_kill(pid, NativeInterop.SIGKILL);
            Thread.Sleep(1000);

            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(pid);
                if (proc.HasExited) return true;
                proc.Dispose();
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unix graceful shutdown attempt failed");
        }

        return false;
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid, DateTimeOffset expectedStartTime, TimeSpan? timeout, CancellationToken ct)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);

            // Verify PID hasn't been reused by comparing start time
            var actualStart = proc.StartTime.ToUniversalTime();
            var diff = Math.Abs((actualStart - expectedStartTime.UtcDateTime).TotalSeconds);
            if (diff > 5)
            {
                // PID was reused — daemon already exited
                return true;
            }

            if (timeout is { } finiteTimeout)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(finiteTimeout);
                await proc.WaitForExitAsync(cts.Token);
            }
            else
            {
                await proc.WaitForExitAsync(ct);
            }
            return true;
        }
        catch (ArgumentException)
        {
            // Process not found — already exited
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static bool IsSameTrackedProcess(int pid, DateTimeOffset startTime, int expectedPid, DateTimeOffset expectedStartTime)
        => pid == expectedPid && Math.Abs((startTime - expectedStartTime).TotalSeconds) <= 5;

    private static void ClearDeferredInstallMetadata(UpdateState state)
    {
        state.WaitForPid = null;
        state.WaitForStartTime = null;
        state.WatcherPid = null;
        state.WatcherStartTime = null;
    }

    private static string GetExpectedHash(string checksumsPath, string assetName)
    {
        foreach (var line in File.ReadLines(checksumsPath))
        {
            var parts = line.Split([' ', '*'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && parts[^1].Equals(assetName, StringComparison.OrdinalIgnoreCase)
                && parts[0].Length == 64
                && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        throw new UserFacingException(
            $"checksums.txt does not contain a valid SHA256 entry for '{assetName}'.");
    }

    private static void ThrowIfDisabled()
    {
        var value = Environment.GetEnvironmentVariable(AppIdentity.DisableSelfUpdatesEnvVar);
        if (value is not null
            && value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on")
        {
            throw new UserFacingException(
                $"Self-update is disabled by {AppIdentity.DisableSelfUpdatesEnvVar}.");
        }
    }
}
