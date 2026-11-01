using ZKeyAudioLink.Core.Audio;
using ZKeyAudioLink.Core.Diagnostics;
using ZKeyAudioLink.Core.Media;

namespace ZKeyAudioLink.Core.Playback;

public enum PlaybackPhase
{
    Idle,
    Preparing,
    Playing
}

/// <summary>
/// Serializes every play/stop/switch command and keeps a generation id so a
/// slow prepare that was cancelled can never start playing over a newer action.
/// </summary>
public sealed class PlaybackController : IDisposable
{
    private readonly IPlaybackEngine _engine;
    private readonly Func<MediaEntry?> _selectedEntry;
    private readonly AppLog _log;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _toggleGate = new(1, 1);

    private PlaybackPhase _phase = PlaybackPhase.Idle;
    private int _generation;
    private CancellationTokenSource? _prepareCts;
    private Task? _prepareTask;
    private bool _disposed;

    public PlaybackController(IPlaybackEngine engine, Func<MediaEntry?> selectedEntry, AppLog log)
    {
        _engine = engine;
        _selectedEntry = selectedEntry;
        _log = log;
        _engine.MaterialCompleted += OnMaterialCompleted;
    }

    public PlaybackPhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    public int Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public event Action<PlaybackPhase>? PhaseChanged;
    public event Action<string>? Error;
    public event Action<string>? Notice;

    public async Task ToggleAsync()
    {
        await _toggleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            PrepareStartResult startResult = PrepareStartResult.Started;
            var changed = false;
            lock (_gate)
            {
                switch (_phase)
                {
                    case PlaybackPhase.Idle:
                        startResult = StartPrepareLocked();
                        changed = startResult == PrepareStartResult.Started;
                        break;
                    case PlaybackPhase.Preparing:
                        StopLocked();
                        changed = true;
                        break;
                    case PlaybackPhase.Playing:
                        StopLocked();
                        changed = true;
                        break;
                }
            }

            if (changed)
            {
                RaisePhase();
            }
            else if (startResult == PrepareStartResult.NoSelection)
            {
                Notice?.Invoke("请先导入并选中一段音频。");
            }
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _toggleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var changed = false;
            lock (_gate)
            {
                if (_phase != PlaybackPhase.Idle)
                {
                    StopLocked();
                    changed = true;
                }
            }

            if (changed)
            {
                RaisePhase();
            }
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    /// <summary>Called before switching selection or editing the start offset.</summary>
    public async Task StopForEditAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private PrepareStartResult StartPrepareLocked()
    {
        var entry = _selectedEntry();
        if (entry is null || entry.Duration <= TimeSpan.Zero)
        {
            return PrepareStartResult.NoSelection;
        }

        if (entry.StartOffset < TimeSpan.Zero || entry.StartOffset >= entry.Duration)
        {
            Error?.Invoke("开始时间超出范围，无法播放。");
            return PrepareStartResult.Invalid;
        }

        _generation++;
        var generation = _generation;
        _prepareCts?.Dispose();
        var cts = new CancellationTokenSource();
        _prepareCts = cts;
        _phase = PlaybackPhase.Preparing;
        _prepareTask = PrepareAndPlayAsync(entry, generation, cts.Token);
        return PrepareStartResult.Started;
    }

    private async Task PrepareAndPlayAsync(MediaEntry entry, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await _engine.PlayMaterialAsync(entry, generation, cancellationToken).ConfigureAwait(false);
            var started = false;
            lock (_gate)
            {
                if (generation == _generation && _phase == PlaybackPhase.Preparing)
                {
                    _phase = PlaybackPhase.Playing;
                    started = true;
                }
            }

            if (started)
            {
                RaisePhase();
            }
            else
            {
                _engine.StopMaterial(generation);
            }
        }
        catch (OperationCanceledException)
        {
            _log.Info("play prepare cancelled", new { generation });
        }
        catch (EngineNotReadyException ex)
        {
            Fail(generation, ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error("play prepare failed", ex);
            Fail(generation, $"无法播放：{ex.Message}");
        }
    }

    private void Fail(int generation, string message)
    {
        var changed = false;
        var report = false;
        lock (_gate)
        {
            report = generation == _generation;
            if (report && _phase == PlaybackPhase.Preparing)
            {
                _phase = PlaybackPhase.Idle;
                changed = true;
            }
        }

        if (changed)
        {
            RaisePhase();
        }

        if (report)
        {
            Error?.Invoke(message);
        }
    }

    private void StopLocked()
    {
        _prepareCts?.Cancel();
        if (_phase != PlaybackPhase.Idle)
        {
            _engine.StopMaterial(_generation);
        }

        _phase = PlaybackPhase.Idle;
        _log.Info("playback stopped", new { generation = _generation });
    }

    private void OnMaterialCompleted(MaterialCompletion completion)
    {
        var changed = false;
        var current = false;
        lock (_gate)
        {
            current = completion.Generation == _generation;
            if (current && _phase != PlaybackPhase.Idle)
            {
                _phase = PlaybackPhase.Idle;
                changed = true;
            }

            if (completion.Generation > _generation)
            {
                _generation = completion.Generation;
            }
        }

        if (changed)
        {
            RaisePhase();
        }

        if (current && completion.Reason == MaterialStopReason.Failed)
        {
            Error?.Invoke(completion.Message ?? "素材解码失败。");
        }
    }

    private void RaisePhase()
    {
        PlaybackPhase phase;
        lock (_gate)
        {
            phase = _phase;
        }

        try
        {
            PhaseChanged?.Invoke(phase);
        }
        catch (Exception ex)
        {
            _log.Error("phase subscriber failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.MaterialCompleted -= OnMaterialCompleted;
        lock (_gate)
        {
            _prepareCts?.Cancel();
            _prepareCts?.Dispose();
            _prepareCts = null;
        }

        try
        {
            _prepareTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore
        }

        _toggleGate.Dispose();
    }

    private enum PrepareStartResult
    {
        Started,
        NoSelection,
        Invalid
    }
}
