using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ZKeyAudioLink.Core;
using ZKeyAudioLink.Core.Audio;
using ZKeyAudioLink.Core.Configuration;
using ZKeyAudioLink.Core.Diagnostics;
using ZKeyAudioLink.Core.Hotkeys;
using ZKeyAudioLink.Core.Media;
using ZKeyAudioLink.Core.Playback;

namespace ZKeyAudioLink.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AudioEngine _engine;
    private readonly PlaybackController _controller;
    private readonly MediaLibrary _library;
    private readonly SettingsStore _store;
    private readonly AppLog _log;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _bannerTimer;
    private readonly DispatcherTimer _startApplyTimer;

    private MediaItemViewModel? _selectedItem;
    private DeviceOption? _selectedMicrophone;
    private DeviceOption? _selectedMonitor;
    private DeviceOption? _selectedVirtual;
    private string _statusText = "正在启动…";
    private string _statusDetail = string.Empty;
    private Brush _statusBrush = UiBrushes.Busy;
    private string _playButtonText = "播放";
    private string _playGlyph = "▶";
    private string _selectedName = "未选择音频";
    private string _selectedMeta = "导入一段 WAV 或 MP3 开始";
    private string _startTimeText = "00:00.000";
    private string _startTimeError = string.Empty;
    private string _positionText = "—";
    private double _startSeconds;
    private double _startSliderMax = 1;
    private bool _hasBanner;
    private string _bannerText = string.Empty;
    private Brush _bannerBrush = UiBrushes.Warning;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private bool _hotkeyEnabled = true;
    private HotkeyKey _selectedHotkeyKey = HotkeyKeys.Default;
    private HotkeyKey _selectedStopHotkeyKey = HotkeyKeys.StopDefault;
    private bool _engineCanPlay;
    private bool _canPlay;
    private bool _canStop;
    private double _micGainPercent = 100;
    private double _sendGainPercent = 100;
    private double _monitorGainPercent = 80;
    private double _micLevel;
    private double _materialLevel;
    private double _mixLevel;
    private string _micLevelText = "0%";
    private string _materialLevelText = "0%";
    private string _mixLevelText = "0%";
    private string _deviceHint = "正在读取音频设备…";
    private double _positionSeconds;
    private string _routeHint = "QQ / OOPZ 的麦克风（输入）选择 CABLE Output；扬声器（输出）保持你的耳机。";

    private bool _suppressSelection;
    private bool _suppressDeviceChange;
    private bool _suppressStartText;
    private bool _initialized;
    private bool _disposed;

    public MainViewModel(
        AudioEngine engine,
        PlaybackController controller,
        MediaLibrary library,
        SettingsStore store,
        AppLog log,
        Dispatcher dispatcher)
    {
        _engine = engine;
        _controller = controller;
        _library = library;
        _store = store;
        _log = log;
        _dispatcher = dispatcher;

        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsBusy);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => SelectedItem is not null && !IsBusy);
        PlayCommand = new AsyncRelayCommand(PlayOrReplayAsync, () => CanPlay);
        StopCommand = new AsyncRelayCommand(StopPlaybackAsync, () => CanStop);
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAndEngineAsync, () => !IsBusy);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync, () => !IsBusy);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        OpenDriverPageCommand = new RelayCommand(OpenDriverPage);
        DismissBannerCommand = new RelayCommand(() => HasBanner = false, () => HasBanner);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTimerTick;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _store.Save(); };
        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _bannerTimer.Tick += (_, _) => { _bannerTimer.Stop(); HasBanner = false; };
        _startApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _startApplyTimer.Tick += async (_, _) =>
        {
            _startApplyTimer.Stop();
            await ApplyStartOffsetAsync();
        };

        _engine.StatusChanged += OnEngineStatusChanged;
        _engine.DevicesUpdated += OnDevicesUpdated;
        _controller.PhaseChanged += OnPhaseChanged;
        _controller.Error += OnControllerError;
        _controller.Notice += OnControllerNotice;

        _hotkeyEnabled = store.Settings.HotkeyEnabled;
        _selectedHotkeyKey = HotkeyKeys.GetOrDefault(store.Settings.HotkeyKey);
        _selectedStopHotkeyKey = HotkeyKeys.GetStopOrDefault(store.Settings.StopHotkeyKey);
        LoadMediaList(store.Settings.SelectedMediaId);
        LoadVolumes();
        _initialized = true;
        _timer.Start();
    }

    public ObservableCollection<MediaItemViewModel> MediaItems { get; } = new();
    public ObservableCollection<DeviceOption> Microphones { get; } = new();
    public ObservableCollection<DeviceOption> MonitorDevices { get; } = new();
    public ObservableCollection<DeviceOption> VirtualDevices { get; } = new();

    public bool HasMedia => MediaItems.Count > 0;

    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand PlayCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand RefreshDevicesCommand { get; }
    public AsyncRelayCommand ReconnectCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand OpenDriverPageCommand { get; }
    public RelayCommand DismissBannerCommand { get; }

    public event Action<bool>? HotkeyEnabledChanged;
    public event Action<HotkeyKey>? HotkeyKeyChanged;
    public event Action<HotkeyKey>? StopHotkeyKeyChanged;

    public IReadOnlyList<HotkeyKey> HotkeyOptions => HotkeyKeys.All;

    public HotkeyKey SelectedHotkeyKey
    {
        get => _selectedHotkeyKey;
        set
        {
            var key = value ?? HotkeyKeys.Default;
            if (key == _selectedStopHotkeyKey)
            {
                OnPropertyChanged(nameof(SelectedHotkeyKey));
                ShowBanner("播放快捷键不能与停止快捷键相同，请先改另一个。", error: true, autoHide: false);
                return;
            }

            if (!SetProperty(ref _selectedHotkeyKey, key))
            {
                return;
            }

            OnPropertyChanged(
                nameof(HotkeyDisplayName),
                nameof(HotkeyHeaderText),
                nameof(HotkeyHintText),
                nameof(PlayKeyHintText),
                nameof(HotkeyStatusText));

            if (!_initialized)
            {
                return;
            }

            _store.Settings.HotkeyKey = key.Name;
            _store.Save();
            HotkeyKeyChanged?.Invoke(key);
            ShowBanner($"播放快捷键已改为 {key.DisplayName}。", error: false, autoHide: true);
        }
    }

    public HotkeyKey SelectedStopHotkeyKey
    {
        get => _selectedStopHotkeyKey;
        set
        {
            var key = value ?? HotkeyKeys.StopDefault;
            if (key == _selectedHotkeyKey)
            {
                OnPropertyChanged(nameof(SelectedStopHotkeyKey));
                ShowBanner("停止快捷键不能与播放快捷键相同，请先改另一个。", error: true, autoHide: false);
                return;
            }

            if (!SetProperty(ref _selectedStopHotkeyKey, key))
            {
                return;
            }

            OnPropertyChanged(
                nameof(StopHotkeyDisplayName),
                nameof(HotkeyHeaderText),
                nameof(HotkeyHintText),
                nameof(StopKeyHintText),
                nameof(HotkeyStatusText));

            if (!_initialized)
            {
                return;
            }

            _store.Settings.StopHotkeyKey = key.Name;
            _store.Save();
            StopHotkeyKeyChanged?.Invoke(key);
            ShowBanner($"停止快捷键已改为 {key.DisplayName}。", error: false, autoHide: true);
        }
    }

    public string HotkeyDisplayName => _selectedHotkeyKey.DisplayName;
    public string StopHotkeyDisplayName => _selectedStopHotkeyKey.DisplayName;
    public string HotkeyHeaderText => $"播放 {HotkeyDisplayName} · 停止 {StopHotkeyDisplayName}";
    public string HotkeyHintText => $"{HotkeyDisplayName} 播放/重播 · {StopHotkeyDisplayName} 停止";
    public string PlayKeyHintText => $"（{HotkeyDisplayName}）";
    public string StopKeyHintText => $"（{StopHotkeyDisplayName}）";

    public MediaItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelection), nameof(CanDelete), nameof(SelectedName), nameof(SelectedMeta));
            if (!_suppressSelection && _initialized)
            {
                _ = OnSelectionChangedAsync();
            }
            else
            {
                UpdateSelectionFields();
            }
        }
    }

    public bool HasSelection => SelectedItem is not null;

    public DeviceOption? SelectedMicrophone
    {
        get => _selectedMicrophone;
        set
        {
            if (SetProperty(ref _selectedMicrophone, value) && _initialized && !_suppressDeviceChange)
            {
                _ = ApplyDeviceSelectionAsync();
            }
        }
    }

    public DeviceOption? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (SetProperty(ref _selectedMonitor, value) && _initialized && !_suppressDeviceChange)
            {
                _ = ApplyDeviceSelectionAsync();
            }
        }
    }

    public DeviceOption? SelectedVirtual
    {
        get => _selectedVirtual;
        set
        {
            if (SetProperty(ref _selectedVirtual, value) && _initialized && !_suppressDeviceChange)
            {
                _ = ApplyDeviceSelectionAsync();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set => SetProperty(ref _statusBrush, value);
    }

    public string PlayButtonText
    {
        get => _playButtonText;
        private set => SetProperty(ref _playButtonText, value);
    }

    public string PlayGlyph
    {
        get => _playGlyph;
        private set => SetProperty(ref _playGlyph, value);
    }

    public string SelectedName
    {
        get => _selectedName;
        private set => SetProperty(ref _selectedName, value);
    }

    public string SelectedMeta
    {
        get => _selectedMeta;
        private set => SetProperty(ref _selectedMeta, value);
    }

    public string StartTimeText
    {
        get => _startTimeText;
        set => SetProperty(ref _startTimeText, value);
    }

    public string StartTimeError
    {
        get => _startTimeError;
        private set
        {
            if (SetProperty(ref _startTimeError, value))
            {
                OnPropertyChanged(nameof(HasStartTimeError));
                UpdateCanPlay();
            }
        }
    }

    public bool HasStartTimeError => !string.IsNullOrEmpty(StartTimeError);

    public string PositionText
    {
        get => _positionText;
        private set => SetProperty(ref _positionText, value);
    }

    public double StartSeconds
    {
        get => _startSeconds;
        set
        {
            if (!SetProperty(ref _startSeconds, value))
            {
                return;
            }

            if (_initialized && !_suppressStartText)
            {
                _startApplyTimer.Stop();
                _startApplyTimer.Start();
            }
        }
    }

    public double StartSliderMax
    {
        get => _startSliderMax;
        private set => SetProperty(ref _startSliderMax, value);
    }

    public bool HasBanner
    {
        get => _hasBanner;
        private set => SetProperty(ref _hasBanner, value);
    }

    public string BannerText
    {
        get => _bannerText;
        private set => SetProperty(ref _bannerText, value);
    }

    public Brush BannerBrush
    {
        get => _bannerBrush;
        private set => SetProperty(ref _bannerBrush, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle), nameof(CanDelete));
                ImportCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
                RefreshDevicesCommand.RaiseCanExecuteChanged();
                ReconnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsIdle => !IsBusy;

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public bool HotkeyEnabled
    {
        get => _hotkeyEnabled;
        set
        {
            if (!SetProperty(ref _hotkeyEnabled, value) || !_initialized)
            {
                return;
            }

            _store.Settings.HotkeyEnabled = value;
            _store.Save();
            HotkeyEnabledChanged?.Invoke(value);
            OnPropertyChanged(nameof(HotkeyStatusText));
            ShowBanner(
                value
                    ? $"全局快捷键已启用：{HotkeyDisplayName} 播放/重播，{StopHotkeyDisplayName} 停止。"
                    : "全局快捷键已关闭，仍可用界面按钮播放/重播和停止。",
                error: false,
                autoHide: true);
        }
    }

    public string HotkeyStatusText => HotkeyEnabled
        ? $"播放 {HotkeyDisplayName} / 停止 {StopHotkeyDisplayName}：已启用"
        : $"播放 {HotkeyDisplayName} / 停止 {StopHotkeyDisplayName}：已关闭";

    public bool CanPlay
    {
        get => _canPlay;
        private set
        {
            if (SetProperty(ref _canPlay, value))
            {
                PlayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanStop
    {
        get => _canStop;
        private set
        {
            if (SetProperty(ref _canStop, value))
            {
                StopCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanDelete => SelectedItem is not null && !IsBusy;

    public double MicGainPercent
    {
        get => _micGainPercent;
        set
        {
            if (!SetProperty(ref _micGainPercent, value))
            {
                return;
            }

            _engine.SetGains((float)(value / 100.0), (float)(SendGainPercent / 100.0), (float)(MonitorGainPercent / 100.0));
            ScheduleSave();
        }
    }

    public double SendGainPercent
    {
        get => _sendGainPercent;
        set
        {
            if (!SetProperty(ref _sendGainPercent, value))
            {
                return;
            }

            _engine.SetGains((float)(MicGainPercent / 100.0), (float)(value / 100.0), (float)(MonitorGainPercent / 100.0));
            ScheduleSave();
        }
    }

    public double MonitorGainPercent
    {
        get => _monitorGainPercent;
        set
        {
            if (!SetProperty(ref _monitorGainPercent, value))
            {
                return;
            }

            _engine.SetGains((float)(MicGainPercent / 100.0), (float)(SendGainPercent / 100.0), (float)(value / 100.0));
            ScheduleSave();
        }
    }

    public double MicLevel
    {
        get => _micLevel;
        private set => SetProperty(ref _micLevel, value);
    }

    public double MaterialLevel
    {
        get => _materialLevel;
        private set => SetProperty(ref _materialLevel, value);
    }

    public double MixLevel
    {
        get => _mixLevel;
        private set => SetProperty(ref _mixLevel, value);
    }

    public string MicLevelText
    {
        get => _micLevelText;
        private set => SetProperty(ref _micLevelText, value);
    }

    public string MaterialLevelText
    {
        get => _materialLevelText;
        private set => SetProperty(ref _materialLevelText, value);
    }

    public string MixLevelText
    {
        get => _mixLevelText;
        private set => SetProperty(ref _mixLevelText, value);
    }

    public string DeviceHint
    {
        get => _deviceHint;
        private set => SetProperty(ref _deviceHint, value);
    }

    public string RouteHint
    {
        get => _routeHint;
        private set => SetProperty(ref _routeHint, value);
    }

    public string AppVersion => "1.1.0";

    public void InitializeAfterEngineStart()
    {
        RefreshDevices();
        UpdateStatus(_engine.Status);
        OnPhaseChanged(_controller.Phase);
        UpdateSelectionFields();
    }

    public void ShowExternalError(string message) => ShowBanner(message, error: true, autoHide: false);

    public void OnHotkeyRegistrationFailed(string message)
    {
        HotkeyEnabled = false;
        ShowBanner(message, error: true, autoHide: false);
    }

    private void LoadMediaList(Guid? selectedId)
    {
        _suppressSelection = true;
        try
        {
            MediaItems.Clear();
            foreach (var entry in _library.Entries.OrderBy(e => e.ImportedAt))
            {
                MediaItems.Add(new MediaItemViewModel(entry));
            }

            var selected = selectedId is { } id ? MediaItems.FirstOrDefault(m => m.Id == id) : null;
            SelectedItem = selected ?? MediaItems.FirstOrDefault();
        }
        finally
        {
            _suppressSelection = false;
        }

        OnPropertyChanged(nameof(HasMedia));
        UpdateSelectionFields();
    }

    private void LoadVolumes()
    {
        var settings = _store.Settings;
        _micGainPercent = Math.Clamp(settings.MicrophoneGain * 100.0, 0, 300);
        _sendGainPercent = Math.Clamp(settings.SendGain * 100.0, 0, 200);
        _monitorGainPercent = Math.Clamp(settings.MonitorGain * 100.0, 0, 200);
        OnPropertyChanged(nameof(MicGainPercent), nameof(SendGainPercent), nameof(MonitorGainPercent));
    }

    private async Task OnSelectionChangedAsync()
    {
        try
        {
            await _controller.StopForEditAsync();
            if (SelectedItem is not null)
            {
                _library.SetSelected(SelectedItem.Id);
            }
            else
            {
                _library.SetSelected(null);
            }

            UpdateSelectionFields();
            UpdateCanPlay();
        }
        catch (Exception ex)
        {
            _log.Error("selection change failed", ex);
        }
    }

    private void UpdateSelectionFields()
    {
        var entry = SelectedItem?.Entry;
        if (entry is null)
        {
            SelectedName = "未选择音频";
            SelectedMeta = "导入一段 WAV 或 MP3 开始";
            StartTimeText = "00:00.000";
            StartTimeError = string.Empty;
            PositionText = "—";
            StartSliderMax = 1;
            _suppressStartText = true;
            StartSeconds = 0;
            _suppressStartText = false;
            UpdateCanPlay();
            return;
        }

        SelectedName = entry.DisplayName;
        SelectedMeta = $"时长 {TimeCode.Format(entry.Duration)} · 文件 {entry.SourceFileName}";
        StartSliderMax = Math.Max(0.001, entry.Duration.TotalSeconds);
        _suppressStartText = true;
        StartSeconds = Math.Clamp(entry.StartOffset.TotalSeconds, 0, Math.Max(0, entry.Duration.TotalSeconds - 0.001));
        StartTimeText = TimeCode.Format(entry.StartOffset);
        _suppressStartText = false;
        StartTimeError = string.Empty;
        if (_controller.Phase != PlaybackPhase.Playing)
        {
            PositionText = "—";
        }

        UpdateCanPlay();
    }

    public void CommitStartTime()
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (!TimeCode.TryParse(StartTimeText, out var parsed))
        {
            StartTimeError = "请输入 mm:ss.fff 格式，例如 01:23.500。";
            return;
        }

        var duration = SelectedItem.Entry.Duration;
        if (parsed < TimeSpan.Zero || parsed >= duration)
        {
            StartTimeError = $"开始时间必须在 0 与 {TimeCode.Format(duration)} 之间（不含总时长）。";
            return;
        }

        StartTimeError = string.Empty;
        _suppressStartText = true;
        StartSeconds = parsed.TotalSeconds;
        StartTimeText = TimeCode.Format(parsed);
        _suppressStartText = false;
        _startApplyTimer.Stop();
        _ = ApplyStartOffsetAsync();
    }

    private async Task ApplyStartOffsetAsync()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        var seconds = Math.Clamp(StartSeconds, 0, Math.Max(0, item.Entry.Duration.TotalSeconds - 0.001));
        var offset = TimeSpan.FromSeconds(seconds);
        try
        {
            await _controller.StopForEditAsync();
            if (!_library.TrySetStartOffset(item.Id, offset, out var error))
            {
                StartTimeError = error;
                return;
            }

            StartTimeError = string.Empty;
            _suppressStartText = true;
            StartTimeText = TimeCode.Format(offset);
            _suppressStartText = false;
            item.Refresh();
            UpdateCanPlay();
        }
        catch (Exception ex)
        {
            _log.Error("start offset update failed", ex);
            ShowBanner($"保存开始时间失败：{ex.Message}", error: true);
        }
    }

    public async Task PlayOrReplayAsync()
    {
        try
        {
            // 只有 Idle 的首次播放需要校验/应用自定义起点；重播固定回 00:00。
            if (_controller.Phase == PlaybackPhase.Idle && SelectedItem is { } item)
            {
                if (!TimeCode.TryParse(StartTimeText, out var parsed) || parsed < TimeSpan.Zero || parsed >= item.Entry.Duration)
                {
                    StartTimeError = $"开始时间必须在 0 与 {TimeCode.Format(item.Entry.Duration)} 之间（不含总时长）。";
                    return;
                }

                if (_startApplyTimer.IsEnabled)
                {
                    _startApplyTimer.Stop();
                    await ApplyStartOffsetAsync();
                }
            }

            await _controller.PlayOrReplayAsync();
        }
        catch (Exception ex)
        {
            _log.Error("play/replay failed", ex);
            ShowBanner($"播放/重播失败：{ex.Message}", error: true);
        }
    }

    public async Task StopPlaybackAsync()
    {
        try
        {
            await _controller.StopAsync();
        }
        catch (Exception ex)
        {
            _log.Error("stop playback failed", ex);
            ShowBanner($"停止播放失败：{ex.Message}", error: true);
        }
    }

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入音频",
            Filter = "音频文件 (*.wav;*.mp3)|*.wav;*.mp3|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        BusyText = "正在导入音频…";
        var imported = new List<MediaEntry>();
        var problems = new List<string>();
        try
        {
            foreach (var file in dialog.FileNames)
            {
                var result = await _library.ImportAsync(file);
                switch (result.Status)
                {
                    case ImportStatus.Imported when result.Entry is not null:
                        imported.Add(result.Entry);
                        break;
                    case ImportStatus.Duplicate:
                        problems.Add(result.Message);
                        break;
                    default:
                        problems.Add($"{Path.GetFileName(file)}：{result.Message}");
                        break;
                }
            }

            if (imported.Count > 0)
            {
                var last = imported[^1];
                _library.SetSelected(last.Id);
                LoadMediaList(last.Id);
                ShowBanner($"已导入 {imported.Count} 个音频文件。", error: false, autoHide: true);
                await _controller.StopForEditAsync();
                UpdateSelectionFields();
            }

            if (problems.Count > 0)
            {
                MessageBox.Show(
                    string.Join(Environment.NewLine, problems),
                    "部分文件没有导入",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _log.Error("import failed", ex);
            ShowBanner($"导入失败：{ex.Message}", error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteAsync()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"从应用库中删除《{item.DisplayName}》？\n只删除应用管理的副本，不会删除你原来的文件。",
            "删除音频",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _controller.StopForEditAsync();
            if (!_library.Remove(item.Id, out var error))
            {
                ShowBanner(error, error: true);
                return;
            }

            LoadMediaList(_library.Selected?.Id);
            ShowBanner("已从应用库中删除。", error: false, autoHide: true);
            UpdateSelectionFields();
        }
        catch (Exception ex)
        {
            _log.Error("delete failed", ex);
            ShowBanner($"删除失败：{ex.Message}", error: true);
        }
    }

    private void RefreshDevices()
    {
        var settings = _store.Settings;
        var micId = SelectedMicrophone?.Id ?? settings.MicrophoneDeviceId;
        var monitorId = SelectedMonitor?.Id ?? settings.MonitorDeviceId;
        var virtualId = SelectedVirtual?.Id ?? settings.VirtualDeviceId;

        _suppressDeviceChange = true;
        try
        {
            var captures = _engine.Devices.GetCaptureDevices();
            var renders = _engine.Devices.GetRenderDevices();

            Fill(Microphones, captures, d => d.IsActive && !d.IsCableOutput,
                micId, "（没有可用的录音设备）");
            Fill(MonitorDevices, renders, d => d.IsActive && !d.IsVirtualCable && !d.IsCableSixteenChannel,
                monitorId, "（没有可用的播放设备）");
            Fill(VirtualDevices, renders, d => d.IsActive && !d.IsCableSixteenChannel,
                virtualId, "（未检测到虚拟声卡 CABLE Input）", markVirtual: true);

            if (VirtualDevices.All(d => !d.IsVirtual && !string.IsNullOrEmpty(d.Id)) &&
                !VirtualDevices.Any(d => d.Id == virtualId))
            {
                VirtualDevices.Insert(0, new DeviceOption(string.Empty, "（未检测到虚拟声卡 CABLE Input）", isActive: false, isPlaceholder: true));
            }

            SelectedMicrophone = Microphones.FirstOrDefault(d => d.Id == micId) ?? Microphones.FirstOrDefault();
            SelectedMonitor = MonitorDevices.FirstOrDefault(d => d.Id == monitorId) ?? MonitorDevices.FirstOrDefault();
            SelectedVirtual = VirtualDevices.FirstOrDefault(d => d.Id == virtualId) ??
                              VirtualDevices.FirstOrDefault(d => d.IsVirtual) ??
                              VirtualDevices.FirstOrDefault(d => string.IsNullOrEmpty(d.Id)) ??
                              VirtualDevices.FirstOrDefault();

            UpdateDeviceHint();
        }
        finally
        {
            _suppressDeviceChange = false;
        }
    }

    private static void Fill(
        ObservableCollection<DeviceOption> target,
        IReadOnlyList<AudioDeviceInfo> allDevices,
        Func<AudioDeviceInfo, bool> filter,
        string? selectedId,
        string emptyLabel,
        bool markVirtual = false)
    {
        target.Clear();
        var options = allDevices.Where(filter).OrderBy(d => d.Name).Select(d =>
            new DeviceOption(d.Id, d.Name, d.IsActive, markVirtual && d.IsVirtualCable)).ToList();

        if (selectedId is not null && options.All(o => o.Id != selectedId))
        {
            var saved = allDevices.FirstOrDefault(d => d.Id == selectedId);
            if (saved is not null)
            {
                options.Insert(0, new DeviceOption(saved.Id, saved.Name, saved.IsActive,
                    markVirtual && saved.IsVirtualCable));
            }
        }

        if (options.Count == 0)
        {
            target.Add(new DeviceOption(string.Empty, emptyLabel, isActive: false, isPlaceholder: true));
            return;
        }

        foreach (var option in options)
        {
            target.Add(option);
        }
    }

    private async Task ApplyDeviceSelectionAsync()
    {
        if (_suppressDeviceChange || IsBusy)
        {
            return;
        }

        var mic = SelectedMicrophone;
        var monitor = SelectedMonitor;
        var virtualDevice = SelectedVirtual;
        if ((mic is null || !mic.IsActive) && (monitor is null || !monitor.IsActive) &&
            (virtualDevice is null || !virtualDevice.IsActive))
        {
            return;
        }

        IsBusy = true;
        BusyText = "正在切换设备…";
        try
        {
            await _controller.StopForEditAsync();
            await _engine.ApplyDevicesAsync(mic?.Id, monitor?.Id, virtualDevice?.Id);
            RefreshDevices();
            UpdateStatus(_engine.Status);
        }
        catch (Exception ex)
        {
            _log.Error("apply devices failed", ex);
            ShowBanner($"切换设备失败：{ex.Message}", error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshDevicesAndEngineAsync()
    {
        try
        {
            RefreshDevices();
            _engine.RefreshDeviceStatus();
            UpdateStatus(_engine.Status);
        }
        catch (Exception ex)
        {
            ShowBanner($"刷新设备失败：{ex.Message}", error: true);
        }

        await Task.CompletedTask;
    }

    private async Task ReconnectAsync()
    {
        IsBusy = true;
        BusyText = "正在重新连接音频设备…";
        try
        {
            await _controller.StopForEditAsync();
            await _engine.ReconnectAsync();
            RefreshDevices();
            UpdateStatus(_engine.Status);
        }
        catch (Exception ex)
        {
            ShowBanner($"重新连接失败：{ex.Message}", error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnEngineStatusChanged(EngineStatus status)
    {
        _dispatcher.BeginInvoke(() =>
        {
            UpdateStatus(status);
            if (status.State == EngineState.Ready)
            {
                RefreshDevices();
            }
        });
    }

    private void UpdateStatus(EngineStatus status)
    {
        _engineCanPlay = status.CanPlayMaterial;
        switch (status.State)
        {
            case EngineState.Ready:
                StatusText = "音频引擎就绪";
                StatusBrush = UiBrushes.Ready;
                break;
            case EngineState.Starting:
                StatusText = "正在初始化音频引擎";
                StatusBrush = UiBrushes.Busy;
                break;
            case EngineState.Degraded:
                StatusText = "音频引擎部分可用";
                StatusBrush = UiBrushes.Warning;
                break;
            case EngineState.Faulted:
                StatusText = "音频引擎不可用";
                StatusBrush = UiBrushes.Danger;
                break;
            default:
                StatusText = "音频引擎已停止";
                StatusBrush = UiBrushes.Muted;
                break;
        }

        StatusDetail = status.Message;
        UpdateDeviceHint();
        UpdateCanPlay();
    }

    private void UpdateDeviceHint()
    {
        var selectedVirtual = SelectedVirtual;
        var hasVirtual = selectedVirtual is { IsActive: true } &&
                         !string.IsNullOrEmpty(selectedVirtual.Id) &&
                         (selectedVirtual.IsVirtual || _engine.Status.SendAvailable);
        DeviceHint = hasVirtual
            ? "QQ / OOPZ 的麦克风（输入）选 CABLE Output；扬声器（输出）保持耳机。"
            : "未检测到可用虚拟发送端。请安装基础版 VB-CABLE 后点击“重新连接”。";
    }

    private void OnDevicesUpdated()
    {
        _dispatcher.BeginInvoke(() =>
        {
            RefreshDevices();
            UpdateStatus(_engine.Status);
        });
    }

    private void OnPhaseChanged(PlaybackPhase phase)
    {
        _dispatcher.BeginInvoke(() =>
        {
            switch (phase)
            {
                case PlaybackPhase.Idle:
                    PlayButtonText = "播放";
                    PlayGlyph = "▶";
                    if (SelectedItem is not null)
                    {
                        PositionText = "—";
                    }

                    break;
                case PlaybackPhase.Preparing:
                    PlayButtonText = "准备中…";
                    PlayGlyph = "…";
                    PositionText = "准备中…";
                    break;
                case PlaybackPhase.Playing:
                    PlayButtonText = "重播";
                    PlayGlyph = "↻";
                    break;
            }

            UpdateCanPlay();
            UpdateCanStop();
        });
    }

    private void OnControllerError(string message)
    {
        _dispatcher.BeginInvoke(() => ShowBanner(message, error: true));
    }

    private void OnControllerNotice(string message)
    {
        _dispatcher.BeginInvoke(() => ShowBanner(message, error: false, autoHide: true));
    }

    private void UpdateCanPlay()
    {
        var phase = _controller.Phase;
        var selected = SelectedItem;
        var validStart = selected is not null && !HasStartTimeError &&
                         selected.Entry.StartOffset < selected.Entry.Duration;
        CanPlay = phase != PlaybackPhase.Idle ||
                  (selected is not null && !selected.IsMissing && validStart && _engineCanPlay);
        DeleteCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanDelete));
    }

    private void UpdateCanStop()
    {
        CanStop = _controller.Phase != PlaybackPhase.Idle;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var metrics = _engine.Metrics;
        var levels = metrics.Levels;
        MicLevel = Smooth(MicLevel, Math.Clamp(levels.MicPeak, 0f, 1.5f), attack: 0.55, release: 0.12);
        MaterialLevel = Smooth(MaterialLevel, Math.Clamp(levels.MaterialPeak, 0f, 1.5f), attack: 0.55, release: 0.12);
        MixLevel = Smooth(MixLevel, Math.Clamp(levels.SendPeak, 0f, 1.5f), attack: 0.55, release: 0.12);
        MicLevelText = $"{Math.Round(Math.Min(1, MicLevel) * 100):0}%";
        MaterialLevelText = $"{Math.Round(Math.Min(1, MaterialLevel) * 100):0}%";
        MixLevelText = $"{Math.Round(Math.Min(1, MixLevel) * 100):0}%";

        if (_controller.Phase == PlaybackPhase.Playing && SelectedItem is not null)
        {
            _positionSeconds = metrics.PositionSeconds;
            PositionText = $"{TimeCode.Format(TimeSpan.FromSeconds(Math.Max(0, _positionSeconds)))} / " +
                           $"{TimeCode.Format(SelectedItem.Entry.Duration)}";
        }
        else if (_controller.Phase == PlaybackPhase.Preparing)
        {
            PositionText = "准备中…";
        }
    }

    private static double Smooth(double current, double target, double attack, double release)
    {
        var factor = target > current ? attack : release;
        return current + (target - current) * factor;
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void ShowBanner(string message, bool error, bool autoHide = false)
    {
        BannerText = message;
        BannerBrush = error ? UiBrushes.Danger : UiBrushes.Warning;
        HasBanner = true;
        DismissBannerCommand.RaiseCanExecuteChanged();
        _bannerTimer.Stop();
        if (autoHide)
        {
            _bannerTimer.Start();
        }

        if (error)
        {
            _log.Warning("ui banner", new { message });
        }
    }

    private void OpenLogs()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = AppPaths.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowBanner($"无法打开日志文件夹：{ex.Message}", error: true);
        }
    }

    private void OpenDriverPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://vb-audio.com/Cable/",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowBanner($"无法打开浏览器：{ex.Message}", error: true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _saveTimer.Stop();
        _bannerTimer.Stop();
        _startApplyTimer.Stop();
        _engine.StatusChanged -= OnEngineStatusChanged;
        _engine.DevicesUpdated -= OnDevicesUpdated;
        _controller.PhaseChanged -= OnPhaseChanged;
        _controller.Error -= OnControllerError;
        _controller.Notice -= OnControllerNotice;
    }
}



