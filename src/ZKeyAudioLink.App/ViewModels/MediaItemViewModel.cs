using System.Windows.Media;
using ZKeyAudioLink.Core;
using ZKeyAudioLink.Core.Audio;
using ZKeyAudioLink.Core.Media;

namespace ZKeyAudioLink.App.ViewModels;

public sealed class MediaItemViewModel : ObservableObject
{
    private string _startText = string.Empty;
    private bool _isMissing;

    public MediaItemViewModel(MediaEntry entry)
    {
        Entry = entry;
        Refresh();
    }

    public MediaEntry Entry { get; }

    public Guid Id => Entry.Id;
    public string DisplayName => Entry.DisplayName;
    public string DurationText => TimeCode.Format(Entry.Duration);
    public string SourceFileName => Entry.SourceFileName;

    public string StartText
    {
        get => _startText;
        private set => SetProperty(ref _startText, value);
    }

    public bool IsMissing
    {
        get => _isMissing;
        private set => SetProperty(ref _isMissing, value);
    }

    public string MissingText => IsMissing ? "文件缺失" : string.Empty;

    public void Refresh()
    {
        StartText = $"起点 {TimeCode.Format(Entry.StartOffset)}";
        IsMissing = !File.Exists(Entry.GetLibraryPath());
        OnPropertyChanged(nameof(StartText), nameof(IsMissing), nameof(MissingText));
    }
}

public sealed class DeviceOption
{
    public DeviceOption(string id, string name, bool isActive, bool isVirtual = false, bool isPlaceholder = false)
    {
        Id = id;
        Name = name;
        IsActive = isActive;
        IsVirtual = isVirtual;
        IsPlaceholder = isPlaceholder;
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsActive { get; }
    public bool IsVirtual { get; }
    public bool IsPlaceholder { get; }

    public string DisplayName => IsPlaceholder
        ? Name
        : IsActive
            ? (IsVirtual ? $"{Name}（虚拟线）" : Name)
            : $"{Name}（未连接）";

    public override string ToString() => DisplayName;
}

public static class UiBrushes
{
    public static readonly Brush Ready = new SolidColorBrush(Color.FromRgb(5, 150, 105));
    public static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(217, 119, 6));
    public static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(220, 38, 38));
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(102, 112, 133));
    public static readonly Brush Busy = new SolidColorBrush(Color.FromRgb(37, 99, 235));

    static UiBrushes()
    {
        Ready.Freeze();
        Warning.Freeze();
        Danger.Freeze();
        Muted.Freeze();
        Busy.Freeze();
    }
}
