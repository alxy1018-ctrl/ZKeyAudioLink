using ZKeyAudioLink.Core.Configuration;
using ZKeyAudioLink.Core.Diagnostics;
using ZKeyAudioLink.Core.Media;
using NAudio.CoreAudioApi;
using System.Collections.Concurrent;

namespace ZKeyAudioLink.Core.Audio;

public enum EngineState
{
    Stopped,
    Starting,
    Ready,
    Degraded,
    Faulted
}

public sealed class EngineStatus
{
    public EngineState State { get; init; } = EngineState.Stopped;
    public string Message { get; init; } = string.Empty;
    public bool MicrophoneAvailable { get; init; }
    public bool SendAvailable { get; init; }
    public bool MonitorAvailable { get; init; }
    public string? MicrophoneName { get; init; }
    public string? SendName { get; init; }
    public string? MonitorName { get; init; }
    public string? VirtualCableName { get; init; }

    public bool CanPlayMaterial => SendAvailable && MonitorAvailable;
}

public sealed class EngineNotReadyException : Exception
{
    public EngineNotReadyException(string message) : base(message)
    {
    }
}

/// <summary>
/// Owns the device lifetime and ties capture, mixer and both outputs together.
/// Device selection is by endpoint id; a missing required device degrades the
/// engine and stops material, but never switches to a different device silently.
/// The microphone forwarding path is independent of material playback.
/// </summary>
public sealed class AudioEngine : IAsyncDisposable, IPlaybackEngine
{
    private readonly SettingsStore _store;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _sourceGate = new();
    private readonly Dictionary<int, FileMaterialSource> _sources = new();
    private readonly BlockingCollection<FileMaterialSource> _disposeQueue = new(new ConcurrentQueue<FileMaterialSource>());
    private readonly Thread _disposeThread;

    private CaptureService? _capture;
    private MixEngine? _mix;
    private RenderSink? _sendSink;
    private RenderSink? _monitorSink;
    private string? _micError;
    private string? _sendError;
    private string? _monitorError;
    private volatile EngineStatus _status = new();
    private bool _disposed;

    public AudioEngine(SettingsStore store, AppLog log)
    {
        _store = store;
        _log = log;
        Devices = new DeviceService(log);
        Devices.DevicesChanged += OnDevicesChanged;
        _disposeThread = new Thread(DisposeWorker)
        {
            Name = "ZKeyAudioLink.SourceCleanup",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _disposeThread.Start();
    }

    public DeviceService Devices { get; }

    public EngineStatus Status => _status;
    public bool IsRunning => _mix is not null;
    public MixMetrics Metrics => _mix?.Metrics ?? new MixMetrics();
    public MixLevels Levels => Metrics.Levels;
    public double PositionSeconds => Metrics.PositionSeconds;
    public bool IsMaterialActive => Metrics.MaterialActive;

    public event Action<EngineStatus>? StatusChanged;
    public event Action? DevicesUpdated;
    public event Action<MaterialCompletion>? MaterialCompleted;

    public AppSettings Settings => _store.Settings;

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_mix is not null || _sendSink is not null || _monitorSink is not null)
            {
                return;
            }

            _status = new EngineStatus { State = EngineState.Starting, Message = "正在初始化音频引擎…" };
            RaiseStatus();
            await Task.Run(StartCore).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void StartCore()
    {
        var settings = _store.Settings;
        ResolveDefaults(settings);
        _micError = null;
        _sendError = null;
        _monitorError = null;

        var micInfo = Devices.Find(settings.MicrophoneDeviceId, DataFlow.Capture);
        var monitorInfo = Devices.Find(settings.MonitorDeviceId, DataFlow.Render);
        var virtualInfo = Devices.Find(settings.VirtualDeviceId, DataFlow.Render);
        if (virtualInfo is null)
        {
            virtualInfo = Devices.FindVirtualCable();
            if (virtualInfo is not null)
            {
                settings.VirtualDeviceId = virtualInfo.Id;
            }
        }

        if (monitorInfo is not null && virtualInfo is not null && monitorInfo.Id == virtualInfo.Id)
        {
            _monitorError = "耳机输出不能与虚拟发送端是同一个设备。";
            monitorInfo = null;
        }

        // --- monitor (headphones) ---
        _monitorSink = new RenderSink("耳机监听", isMonitor: true, latencyMs: 60, _log);
        if (monitorInfo is not null)
        {
            try
            {
                using var device = Devices.TryGetDevice(monitorInfo.Id) ?? throw new InvalidOperationException("设备未就绪。");
                _monitorSink.Start(device);
            }
            catch (Exception ex)
            {
                _monitorError = $"耳机输出无法打开：{ex.Message}";
                _log.Error("monitor sink start failed", ex);
            }
        }
        else if (_monitorError is null)
        {
            _monitorError = "耳机输出不可用，素材播放已阻止；麦克风转发仍可继续。";
        }

        // --- virtual cable send ---
        _sendSink = new RenderSink("虚拟声卡发送", isMonitor: false, latencyMs: 60, _log);
        if (virtualInfo is not null)
        {
            try
            {
                using var device = Devices.TryGetDevice(virtualInfo.Id) ?? throw new InvalidOperationException("设备未就绪。");
                _sendSink.Start(device);
            }
            catch (Exception ex)
            {
                _sendError = $"虚拟发送端无法打开：{ex.Message}";
                _log.Error("send sink start failed", ex);
            }
        }
        else
        {
            _sendError = "未找到虚拟声卡 CABLE Input。请安装基础版 VB-CABLE 并重启，然后在设备区选择虚拟发送端。";
        }

        if (!_sendSink.IsStarted)
        {
            _sendSink.Dispose();
            _sendSink = null;
        }

        if (!_monitorSink.IsStarted)
        {
            _monitorSink.Dispose();
            _monitorSink = null;
        }

        _monitorSink?.Failed += (sink, ex) => OnSinkFailed(sink, ex, send: false);
        _sendSink?.Failed += (sink, ex) => OnSinkFailed(sink, ex, send: true);

        // --- microphone (only useful while the send path exists) ---
        if (_sendSink is not null && micInfo is not null)
        {
            _capture = new CaptureService(_log);
            _capture.Failed += OnCaptureFailed;
            try
            {
                using var device = Devices.TryGetDevice(micInfo.Id) ?? throw new InvalidOperationException("设备未就绪。");
                _capture.Start(device);
            }
            catch (Exception ex)
            {
                _micError = $"麦克风不可用，对方只能听到素材：{ex.Message}";
                _log.Error("microphone start failed", ex);
                _capture.Dispose();
                _capture = null;
            }
        }
        else if (_sendSink is not null)
        {
            _micError = "麦克风不可用，对方只能听到素材。";
        }

        // --- mixer ---
        if (_sendSink is not null)
        {
            IMicSource mic = _capture is not null ? _capture : new SilenceMicSource();
            IAudioSink monitorSink = _monitorSink is not null ? _monitorSink : new NullAudioSink();
            _mix = new MixEngine(mic, _sendSink, monitorSink, _log);
            _mix.MaterialCompletion += OnMixMaterialCompletion;
            _mix.Failed += OnMixFailed;
            _mix.SetGains(settings.MicrophoneGain, settings.SendGain, settings.MonitorGain);
            _mix.Start();
        }

        _store.Save();
        SetStatusFromAvailability();
        _log.Info("audio engine started", new
        {
            mic = micInfo?.Name,
            monitor = monitorInfo?.Name,
            virtualCable = virtualInfo?.Name,
            send = _sendSink?.IsStarted == true,
            monitorOk = _monitorSink?.IsStarted == true,
            micOk = _capture?.IsRunning == true
        });
    }

    private void ResolveDefaults(AppSettings settings)
    {
        if (settings.MicrophoneDeviceId is null)
        {
            var defaultMic = Devices.GetDefault(DataFlow.Capture, Role.Communications)
                             ?? Devices.GetCaptureDevices().FirstOrDefault(d => d.IsActive);
            settings.MicrophoneDeviceId = defaultMic?.Id;
        }

        if (settings.VirtualDeviceId is null)
        {
            var cable = Devices.FindVirtualCable();
            settings.VirtualDeviceId = cable?.Id;
        }

        if (settings.MonitorDeviceId is null)
        {
            var defaultRender = Devices.GetDefault(DataFlow.Render, Role.Multimedia)
                                ?? Devices.GetRenderDevices().FirstOrDefault(d => d.IsActive && !d.IsVirtualCable);
            if (defaultRender is not null && defaultRender.Id != settings.VirtualDeviceId)
            {
                settings.MonitorDeviceId = defaultRender.Id;
            }
        }
    }

    private void SetStatusFromAvailability()
    {
        var micOk = _capture?.IsRunning == true;
        var sendOk = _sendSink?.IsStarted == true;
        var monitorOk = _monitorSink?.IsStarted == true;

        var message = _sendError ?? _monitorError ?? _micError ?? string.Empty;
        var state = sendOk && monitorOk && micOk
            ? EngineState.Ready
            : sendOk || monitorOk ? EngineState.Degraded : EngineState.Faulted;

        _status = new EngineStatus
        {
            State = state,
            Message = state == EngineState.Ready ? "音频引擎就绪，麦克风正在转发。" : message,
            MicrophoneAvailable = micOk,
            SendAvailable = sendOk,
            MonitorAvailable = monitorOk,
            MicrophoneName = _capture?.DeviceName,
            SendName = _sendSink?.DeviceName,
            MonitorName = _monitorSink?.DeviceName,
            VirtualCableName = Devices.FindCableOutput()?.Name
        };
        RaiseStatus();
    }

    public async Task ApplyDevicesAsync(string? microphoneId, string? monitorId, string? virtualId)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            StopMaterialInternal();
            StopCore();
            var settings = _store.Settings;
            settings.MicrophoneDeviceId = microphoneId;
            settings.MonitorDeviceId = monitorId;
            settings.VirtualDeviceId = virtualId;
            _store.Save();
            _status = new EngineStatus { State = EngineState.Starting, Message = "正在切换设备…" };
            RaiseStatus();
            await Task.Run(StartCore).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task ReconnectAsync()
    {
        var settings = _store.Settings;
        await ApplyDevicesAsync(settings.MicrophoneDeviceId, settings.MonitorDeviceId, settings.VirtualDeviceId).ConfigureAwait(false);
    }

    public void SetGains(float microphone, float send, float monitor)
    {
        var settings = _store.Settings;
        settings.MicrophoneGain = Math.Clamp(microphone, 0f, 4f);
        settings.SendGain = Math.Clamp(send, 0f, 2f);
        settings.MonitorGain = Math.Clamp(monitor, 0f, 2f);
        _mix?.SetGains(settings.MicrophoneGain, settings.SendGain, settings.MonitorGain);
    }

    public async Task PlayMaterialAsync(MediaEntry entry, int generation, CancellationToken cancellationToken)
    {
        if (_mix is null || _sendSink?.IsStarted != true)
        {
            throw new EngineNotReadyException(_sendError ?? _status.Message ?? "音频引擎未就绪，无法播放素材。");
        }

        if (_monitorSink?.IsStarted != true)
        {
            throw new EngineNotReadyException(_monitorError ?? "耳机输出不可用，已阻止播放以避免只有对方听到声音。");
        }

        var path = entry.GetLibraryPath();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("素材文件不存在，可能已被删除或移动。", path);
        }

        if (entry.StartOffset < TimeSpan.Zero || entry.StartOffset >= entry.Duration)
        {
            throw new EngineNotReadyException("开始时间超出音频时长，无法播放。");
        }

        var source = new FileMaterialSource(path, entry.StartOffset, _log);
        try
        {
            await source.PrefillAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            source.Dispose();
            throw;
        }

        lock (_sourceGate)
        {
            _sources[generation] = source;
        }

        _mix.StartMaterial(generation, source, entry.StartOffset);
        _log.Info("material playback requested", new
        {
            file = entry.DisplayName,
            offset = entry.StartOffset.TotalSeconds,
            generation
        });
    }

    public void StopMaterial(int generation)
    {
        if (_mix is null)
        {
            DisposeSource(generation);
            return;
        }

        _mix.StopMaterial(generation);
    }

    private void OnMixMaterialCompletion(MaterialCompletion completion)
    {
        DisposeSource(completion.Generation);
        if (completion.Reason == MaterialStopReason.Failed)
        {
            _log.Error("material failed during playback", new { message = completion.Message });
        }

        try
        {
            MaterialCompleted?.Invoke(completion);
        }
        catch (Exception ex)
        {
            _log.Error("material completion subscriber failed", ex);
        }
    }

    private void OnMixFailed(Exception exception)
    {
        _sendError = $"混音线程停止：{exception.Message}。请重新连接设备。";
        _log.Error("mix thread failed", exception);
        SetStatusFromAvailability();
    }

    private void DisposeSource(int generation)
    {
        FileMaterialSource? source;
        lock (_sourceGate)
        {
            _sources.Remove(generation, out source);
        }

        EnqueueDispose(source);
    }

    private void DisposeAllSources()
    {
        List<FileMaterialSource> sources;
        lock (_sourceGate)
        {
            sources = _sources.Values.ToList();
            _sources.Clear();
        }

        foreach (var source in sources)
        {
            EnqueueDispose(source);
        }
    }

    private void EnqueueDispose(FileMaterialSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            _disposeQueue.Add(source);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The cleanup queue is already complete (shutdown); disposing inline
            // is still safer than leaking the decoder thread.
            try
            {
                source.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    private void DisposeWorker()
    {
        foreach (var source in _disposeQueue.GetConsumingEnumerable())
        {
            try
            {
                source.Dispose();
            }
            catch (Exception ex)
            {
                _log?.Warning("material source cleanup failed", new { error = ex.Message });
            }
        }
    }

    private void OnCaptureFailed(Exception? exception)
    {
        _micError = exception is null
            ? "麦克风已断开，对方将只听到素材。"
            : $"麦克风出错，对方将只听到素材：{exception.Message}";
        _log.Warning("microphone capture failed", new { error = exception?.Message });
        SetStatusFromAvailability();
    }

    private void OnSinkFailed(RenderSink sink, Exception? exception, bool send)
    {
        var detail = exception?.Message ?? "设备已停止";
        if (send)
        {
            _sendError = $"虚拟发送端已停止：{detail}。QQ/OOPZ 将听不到你的声音和素材。";
        }
        else
        {
            _monitorError = $"耳机输出已停止：{detail}。素材播放已阻止。";
        }

        _mix?.StopAllMaterials();
        SetStatusFromAvailability();
    }

    private void OnDevicesChanged()
    {
        var settings = _store.Settings;
        var mic = Devices.Find(settings.MicrophoneDeviceId, DataFlow.Capture);
        var monitor = Devices.Find(settings.MonitorDeviceId, DataFlow.Render);
        var virtualCable = Devices.Find(settings.VirtualDeviceId, DataFlow.Render);

        if (_status.State is EngineState.Ready or EngineState.Degraded)
        {
            var missing = new List<string>();
            if (mic is null || !mic.IsActive)
            {
                missing.Add("麦克风");
            }

            if (monitor is null || !monitor.IsActive)
            {
                missing.Add("耳机输出");
            }

            if (virtualCable is null || !virtualCable.IsActive)
            {
                missing.Add("虚拟发送端");
            }

            if (missing.Count > 0)
            {
                _mix?.StopAllMaterials();
                _status = new EngineStatus
                {
                    State = EngineState.Degraded,
                    Message = $"设备状态变化：{string.Join("、", missing)}不可用。请重新连接后在设备区重新应用。",
                    MicrophoneAvailable = mic?.IsActive == true,
                    SendAvailable = virtualCable?.IsActive == true && _sendSink?.IsStarted == true,
                    MonitorAvailable = monitor?.IsActive == true && _monitorSink?.IsStarted == true,
                    MicrophoneName = _capture?.DeviceName,
                    SendName = _sendSink?.DeviceName,
                    MonitorName = _monitorSink?.DeviceName,
                    VirtualCableName = Devices.FindCableOutput()?.Name
                };
                RaiseStatus();
            }
        }

        try
        {
            DevicesUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error("devices updated subscriber failed", ex);
        }
    }

    public void RefreshDeviceStatus() => OnDevicesChanged();

    private void RaiseStatus()
    {
        try
        {
            StatusChanged?.Invoke(_status);
        }
        catch (Exception ex)
        {
            _log.Error("status subscriber failed", ex);
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            StopMaterialInternal();
            StopCore();
            _status = new EngineStatus { State = EngineState.Stopped, Message = "音频引擎已停止。" };
            RaiseStatus();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void StopMaterialInternal()
    {
        if (_mix is null)
        {
            return;
        }

        _mix.StopAllMaterials();
        Thread.Sleep(40);
    }

    private void StopCore()
    {
        _mix?.Dispose();
        _mix = null;
        _capture?.Dispose();
        _capture = null;
        _sendSink?.Dispose();
        _sendSink = null;
        _monitorSink?.Dispose();
        _monitorSink = null;
        DisposeAllSources();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        Devices.DevicesChanged -= OnDevicesChanged;
        Devices.Dispose();
        try
        {
            _disposeQueue.CompleteAdding();
        }
        catch
        {
            // ignore
        }

        if (_disposeThread.IsAlive && !_disposeThread.Join(TimeSpan.FromSeconds(3)))
        {
            _log.Warning("material cleanup thread did not stop in time");
        }

        _disposeQueue.Dispose();
        _lifecycle.Dispose();
    }
}

internal sealed class SilenceMicSource : IMicSource
{
    public int BufferedFrames => 0;

    public int Read(Span<float> destination)
    {
        destination.Clear();
        return 0;
    }
}
