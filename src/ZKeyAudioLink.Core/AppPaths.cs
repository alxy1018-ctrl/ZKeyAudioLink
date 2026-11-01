namespace ZKeyAudioLink.Core;

/// <summary>
/// All mutable application data lives under %LOCALAPPDATA%\ZKeyAudioLink by
/// default, so the program folder can stay read-only. If that folder is not
/// writable (policy, sandbox or antivirus), a portable folder next to the
/// executable and finally the user temp folder are tried, in that order.
/// </summary>
public static class AppPaths
{
    private static readonly object Gate = new();
    private static string? _root;

    public static string Root
    {
        get
        {
            if (_root is not null)
            {
                return _root;
            }

            lock (Gate)
            {
                _root ??= DetermineRoot();
                return _root;
            }
        }
    }

    public static bool IsPortable { get; private set; }

    public static string MediaDirectory => Path.Combine(Root, "Media");
    public static string LogDirectory => Path.Combine(Root, "Logs");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string SettingsBackupFile => Path.Combine(Root, "settings.json.bak");
    public static string LockFile => Path.Combine(Root, ".lock");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MediaDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    private static string DetermineRoot()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZKeyAudioLink"),
            Path.Combine(AppContext.BaseDirectory, "Data"),
            Path.Combine(Path.GetTempPath(), "ZKeyAudioLink")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".write-test-" + Environment.ProcessId);
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                IsPortable = !candidate.StartsWith(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    StringComparison.OrdinalIgnoreCase);
                return candidate;
            }
            catch
            {
                // try the next candidate
            }
        }

        // Last resort: the temp path itself (Directory.CreateDirectory will
        // throw a clear error if even this is unusable).
        return Path.Combine(Path.GetTempPath(), "ZKeyAudioLink");
    }
}
