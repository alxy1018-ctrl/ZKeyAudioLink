namespace ZKeyAudioLink.Core.Configuration;

using ZKeyAudioLink.Core.Media;

public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<MediaEntry> Media { get; set; } = new();
    public Guid? SelectedMediaId { get; set; }

    public string? MicrophoneDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }
    public string? VirtualDeviceId { get; set; }

    public float MicrophoneGain { get; set; } = 1.0f;
    public float SendGain { get; set; } = 1.0f;
    public float MonitorGain { get; set; } = 0.8f;

    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>Stable name from <see cref="ZKeyAudioLink.Core.Hotkeys.HotkeyKeys"/>, default Q.</summary>
    public string HotkeyKey { get; set; } = "Q";

    /// <summary>Independent stop key, default F8; missing in old configs falls back to F8.</summary>
    public string StopHotkeyKey { get; set; } = "F8";

    /// <summary>Persisted geometry of the main window.</summary>
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    public bool TrayHintShown { get; set; }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Media = Media.Select(m => m.Clone()).ToList();
        return copy;
    }
}
