using System.Text.Json;
using Microsoft.Extensions.Logging;
using TemplateCli.Models;

namespace TemplateCli.Infrastructure;

public sealed class StateStore
{
    private readonly string _configDir;
    private readonly string _configPath;
    private readonly string _updateStatePath;
    private readonly string _lockPath;
    private readonly string _updateLockPath;
    private readonly string _pidPath;
    private readonly ILogger<StateStore> _logger;
    private FileStream? _lockStream;
    private FileStream? _updateLockStream;

    private static readonly TimeSpan UpdateLockStaleThreshold = TimeSpan.FromMinutes(15);

    public StateStore(ILogger<StateStore> logger)
    {
        _logger = logger;
        _configDir = AppPaths.GetAppHomeDirectory();
        _configPath = AppPaths.GetConfigPath();
        _updateStatePath = Path.Combine(_configDir, "update-state.json");
        _lockPath = Path.Combine(_configDir, ".lock");
        _updateLockPath = Path.Combine(_configDir, ".update-lock");
        _pidPath = Path.Combine(_configDir, ".pid");
        Directory.CreateDirectory(_configDir);
    }

    public string ConfigDir => _configDir;
    public string ConfigPath => _configPath;

    public bool ConfigExists() => File.Exists(_configPath);

    public static TemplateCliConfig LoadBootstrapConfig()
        => LoadConfig(AppPaths.GetConfigPath(), logger: null);

    public TemplateCliConfig LoadConfig()
        => LoadConfig(_configPath, _logger);

    public void SaveConfig(TemplateCliConfig config)
    {
        var json = JsonSerializer.Serialize(config, TemplateCliJsonContext.Default.TemplateCliConfig);
        AtomicWrite(_configPath, json);
        _logger.LogDebug("Config saved to {Path}", _configPath);
    }

    public bool TryAcquireLock()
    {
        try
        {
            _lockStream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            WriteCurrentProcessIdentity();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void ReleaseLock()
    {
        ClearCurrentProcessIdentity();
        _lockStream?.Dispose();
        _lockStream = null;
        try { File.Delete(_lockPath); } catch { }
    }

    public bool IsLockHeld()
    {
        if (!File.Exists(_lockPath))
            return false;

        try
        {
            using var fs = new FileStream(_lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public FileStream? TryAcquireInstallWindowLock()
    {
        try
        {
            return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public (int Pid, DateTimeOffset StartTime, string? LogInstanceId)? ReadDaemonPid()
    {
        if (!File.Exists(_pidPath))
            return null;

        try
        {
            var lines = File.ReadAllLines(_pidPath);
            if (lines.Length >= 2
                && int.TryParse(lines[0].Trim(), out var pid)
                && DateTimeOffset.TryParse(lines[1].Trim(), out var startTime))
            {
                return (pid, startTime, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read process identity file");
        }

        return null;
    }

    public UpdateState LoadUpdateState()
    {
        if (!File.Exists(_updateStatePath))
        {
            _logger.LogDebug("No update state file found, returning defaults");
            return new UpdateState();
        }

        try
        {
            var json = File.ReadAllText(_updateStatePath);
            return JsonSerializer.Deserialize(json, TemplateCliJsonContext.Default.UpdateState)
                   ?? new UpdateState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update state file is corrupt or unreadable, returning defaults");
            return new UpdateState();
        }
    }

    public void SaveUpdateState(UpdateState state)
    {
        var json = JsonSerializer.Serialize(state, TemplateCliJsonContext.Default.UpdateState);
        AtomicWrite(_updateStatePath, json);
        _logger.LogDebug("Update state saved to {Path}", _updateStatePath);
    }

    public void ClearUpdateState()
    {
        try { File.Delete(_updateStatePath); } catch { }
        _logger.LogDebug("Update state cleared");
    }

    public bool TryAcquireUpdateLock()
    {
        try
        {
            _updateLockStream = new FileStream(_updateLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            WriteUpdateLockInfo();
            return true;
        }
        catch (IOException)
        {
            if (IsUpdateLockStale())
            {
                _logger.LogWarning("Detected stale update lock, recovering");
                try
                {
                    File.Delete(_updateLockPath);
                    _updateLockStream = new FileStream(_updateLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    WriteUpdateLockInfo();
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to recover stale update lock");
                }
            }

            return false;
        }
    }

    public void ReleaseUpdateLock()
    {
        _updateLockStream?.Dispose();
        _updateLockStream = null;
        try { File.Delete(_updateLockPath); } catch { }
    }

    public bool IsUpdateLockHeld()
    {
        if (!File.Exists(_updateLockPath))
            return false;

        try
        {
            using var fs = new FileStream(_updateLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return !IsUpdateLockStale();
        }
    }

    private static TemplateCliConfig LoadConfig(string configPath, ILogger? logger)
    {
        if (!File.Exists(configPath))
        {
            logger?.LogDebug("No config file found, returning defaults");
            return new TemplateCliConfig();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize(json, TemplateCliJsonContext.Default.TemplateCliConfig)
                   ?? new TemplateCliConfig();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Config file is corrupt or unreadable, returning defaults");
            return new TemplateCliConfig();
        }
    }

    private void WriteCurrentProcessIdentity()
    {
        try
        {
            var startTime = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            AtomicWrite(_pidPath, string.Join(Environment.NewLine, Environment.ProcessId, startTime.ToString("O")));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write process identity file");
        }
    }

    private void ClearCurrentProcessIdentity()
    {
        try { File.Delete(_pidPath); } catch { }
    }

    private void WriteUpdateLockInfo()
    {
        try
        {
            if (_updateLockStream is null)
                return;

            _updateLockStream.SetLength(0);
            using var writer = new StreamWriter(_updateLockStream, leaveOpen: true);
            writer.WriteLine(Environment.ProcessId);
            writer.WriteLine(DateTimeOffset.UtcNow.ToString("O"));
            writer.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write update lock info");
        }
    }

    private bool IsUpdateLockStale()
    {
        try
        {
            if (!File.Exists(_updateLockPath))
                return false;

            var lockAge = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_updateLockPath);
            if (lockAge > UpdateLockStaleThreshold)
                return true;

            var lines = File.ReadAllLines(_updateLockPath);
            if (lines.Length >= 1 && int.TryParse(lines[0].Trim(), out var pid))
            {
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById(pid);
                    return false;
                }
                catch (ArgumentException)
                {
                    return true;
                }
            }

            return lockAge > TimeSpan.FromMinutes(1);
        }
        catch
        {
            return false;
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp");
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
