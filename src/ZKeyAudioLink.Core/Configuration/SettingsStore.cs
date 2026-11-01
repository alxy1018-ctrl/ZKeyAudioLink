using System.Text.Json;
using System.Text.Json.Serialization;
using ZKeyAudioLink.Core.Configuration;
using ZKeyAudioLink.Core.Diagnostics;
using ZKeyAudioLink.Core.Media;

namespace ZKeyAudioLink.Core.Configuration;

/// <summary>
/// Loads and atomically persists settings. A corrupt file is preserved (never
/// deleted) and the previous good copy is tried before falling back to defaults.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _saveGate = new();
    private readonly string _file;
    private readonly string _backupFile;
    private readonly AppLog? _log;

    public SettingsStore(string? file = null, string? backupFile = null, AppLog? log = null)
    {
        _file = file ?? AppPaths.SettingsFile;
        _backupFile = backupFile ?? _file + ".bak";
        _log = log;
        Settings = new AppSettings();
    }

    public AppSettings Settings { get; private set; }
    public string? LoadWarning { get; private set; }

    public AppSettings Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);

        var primary = TryRead(_file);
        if (primary.Ok)
        {
            Settings = Normalize(primary.Value!);
            LoadWarning = null;
            return Settings;
        }

        if (primary.Error is not null)
        {
            var corrupt = $"{_file}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(_file, corrupt, overwrite: true);
            }
            catch
            {
                // keep going; the file is still there but unreadable
            }

            var backup = TryRead(_backupFile);
            if (backup.Ok)
            {
                Settings = Normalize(backup.Value!);
                LoadWarning = $"配置文件损坏，已从备份恢复。损坏文件保留为 {Path.GetFileName(corrupt)}。";
                _log?.Warning("settings restored from backup", new { corrupt = Path.GetFileName(corrupt) });
                TrySave(Settings);
                return Settings;
            }

            Settings = new AppSettings();
            LoadWarning = $"配置文件损坏且没有可用备份，已使用默认配置。损坏文件保留为 {Path.GetFileName(corrupt)}。";
            _log?.Error("settings corrupt; defaults used", new { error = primary.Error });
            return Settings;
        }

        Settings = new AppSettings();
        LoadWarning = null;
        return Settings;
    }

    public bool Save()
    {
        try
        {
            TrySave(Settings);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Error("settings save failed", ex);
            return false;
        }
    }

    private void TrySave(AppSettings settings)
    {
        lock (_saveGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_file))
            {
                File.Replace(temp, _file, _backupFile, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _file);
            }
        }
    }

    private static (bool Ok, AppSettings? Value, string? Error) TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return (false, null, null);
        }

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                return (false, null, "empty settings document");
            }

            return (true, settings, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Version = Math.Max(settings.Version, 1);
        settings.HotkeyKey = ZKeyAudioLink.Core.Hotkeys.HotkeyKeys.GetOrDefault(settings.HotkeyKey).Name;
        settings.Media ??= new List<MediaEntry>();
        foreach (var entry in settings.Media)
        {
            if (entry.Duration < TimeSpan.Zero)
            {
                entry.Duration = TimeSpan.Zero;
            }

            if (entry.StartOffset < TimeSpan.Zero || entry.StartOffset >= entry.Duration)
            {
                entry.StartOffset = TimeSpan.Zero;
            }

            if (entry.Id == Guid.Empty)
            {
                entry.Id = Guid.NewGuid();
            }

            if (entry.LibraryFileName is null)
            {
                entry.LibraryFileName = string.Empty;
            }
        }

        settings.MicrophoneGain = Sanitize(settings.MicrophoneGain, 0f, 4f, 1f);
        settings.SendGain = Sanitize(settings.SendGain, 0f, 2f, 1f);
        settings.MonitorGain = Sanitize(settings.MonitorGain, 0f, 2f, 0.8f);

        if (!double.IsFinite(settings.WindowWidth) || settings.WindowWidth < 0)
        {
            settings.WindowWidth = 0;
        }

        if (!double.IsFinite(settings.WindowHeight) || settings.WindowHeight < 0)
        {
            settings.WindowHeight = 0;
        }

        if (settings.WindowLeft is { } left && !double.IsFinite(left))
        {
            settings.WindowLeft = null;
        }

        if (settings.WindowTop is { } top && !double.IsFinite(top))
        {
            settings.WindowTop = null;
        }

        if (settings.SelectedMediaId is { } id && settings.Media.All(m => m.Id != id))
        {
            settings.SelectedMediaId = null;
        }

        return settings;
    }

    private static float Sanitize(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
