using System.Collections.Concurrent;
using System.Diagnostics;
using ZKeyAudioLink.Core.Diagnostics;

namespace ZKeyAudioLink.Core.Audio;

/// <summary>
/// Single producer thread that renders the 48 kHz mix into the two output rings.
/// The send ring is the master clock: blocks are generated only while it is below
/// its target depth, so the virtual-cable device paces the whole timeline. The
/// monitor ring is filled from the same block and corrected by its own sink.
/// </summary>
public sealed class MixEngine : IDisposable
{
    private readonly IMicSource _mic;
    private readonly IAudioSink _send;
    private readonly IAudioSink _monitor;
    private readonly MixProcessor _processor = new();
    private readonly AppLog? _log;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<MixCommand> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly float[] _micBlock = new float[AudioConstants.BlockFrames * AudioConstants.Channels];
    private readonly float[] _materialBlock = new float[AudioConstants.BlockFrames * AudioConstants.Channels];
    private readonly float[] _sendBlock = new float[AudioConstants.BlockFrames * AudioConstants.Channels];
    private readonly float[] _monitorBlock = new float[AudioConstants.BlockFrames * AudioConstants.Channels];

    private volatile bool _running;
    private GainSnapshot _gains = GainSnapshot.Default;
    private volatile MixMetrics _metrics = new();
    private bool _disposed;

    // The following state is only touched by the mix thread.
    private IMaterialSource? _source;
    private int _generation;
    private bool _active;
    private bool _stopping;
    private bool _finishing;
    private float _envelope;
    private int _naturalFadeTotal;
    private int _naturalFadeDone;
    private TimeSpan _startOffset;
    private long _consumedFrames;
    private long _micUnderrunFrames;
    private long _materialUnderrunFrames;
    private long _blocks;
    private double _lastBlockMs;
    private double _maxBlockMs;
    private double _positionSeconds;
    private long _lastMetricsPublishMs;

    public MixEngine(IMicSource microphone, IAudioSink send, IAudioSink monitor, AppLog? log = null)
    {
        _mic = microphone ?? throw new ArgumentNullException(nameof(microphone));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _log = log;
        _thread = new Thread(ThreadMain)
        {
            Name = "ZKeyAudioLink.Mix",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
    }

    public bool IsRunning => _running;

    public MixMetrics Metrics => _metrics;

    public event Action<MaterialCompletion>? MaterialCompletion;
    public event Action<Exception>? Failed;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _thread.Start();
    }

    public void SetGains(float microphone, float send, float monitor)
    {
        Volatile.Write(ref _gains, new GainSnapshot(microphone, send, monitor));
    }

    public void StartMaterial(int generation, IMaterialSource source, TimeSpan startOffset)
    {
        _commands.Enqueue(MixCommand.Start(generation, source, startOffset));
        _wake.Set();
    }

    public void StopMaterial(int generation)
    {
        _commands.Enqueue(MixCommand.Stop(generation));
        _wake.Set();
    }

    public void StopAllMaterials()
    {
        _commands.Enqueue(MixCommand.StopAll());
        _wake.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running = false;
        _commands.Enqueue(MixCommand.Shutdown());
        _wake.Set();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(3)))
        {
            _log?.Warning("mix thread did not stop in time");
        }

        _wake.Dispose();
    }

    private void ThreadMain()
    {
        var stopwatch = new Stopwatch();
        try
        {
            while (_running)
            {
                ProcessCommands();
                if (!_running)
                {
                    break;
                }

                var gains = Volatile.Read(ref _gains);
                _processor.Configure(gains.Microphone, gains.Send, gains.Monitor);

                var generated = 0;
                // Bound the burst per iteration so a sink that always looks
                // empty cannot starve command handling, metrics or shutdown.
                while (_running && generated < 5 && _send.BufferedFrames < AudioConstants.SendTargetFrames)
                {
                    var frames = Math.Min(AudioConstants.BlockFrames, AudioConstants.SendTargetFrames - _send.BufferedFrames);
                    if (frames <= 0)
                    {
                        break;
                    }

                    stopwatch.Restart();
                    RenderBlock(frames);
                    stopwatch.Stop();
                    _lastBlockMs = stopwatch.Elapsed.TotalMilliseconds;
                    if (_lastBlockMs > _maxBlockMs)
                    {
                        _maxBlockMs = _lastBlockMs;
                    }

                    generated++;
                    ProcessCommands();
                }

                PublishMetricsIfDue();
                _wake.WaitOne(generated == 0 ? 2 : 1);
            }
        }
        catch (Exception ex)
        {
            _log?.Error("mix thread failed", ex);
            try
            {
                Failed?.Invoke(ex);
            }
            catch
            {
                // ignore subscriber failures during shutdown
            }
        }
        finally
        {
            _running = false;
            CompleteActive(MaterialStopReason.Shutdown, null);
            PublishMetrics();
        }
    }

    private void ProcessCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            switch (command.Kind)
            {
                case MixCommandKind.Start:
                    if (_active)
                    {
                        CompleteActive(MaterialStopReason.Superseded, null);
                    }

                    _source = command.Source;
                    _generation = command.Generation;
                    _startOffset = command.StartOffset;
                    _active = true;
                    _stopping = false;
                    _finishing = false;
                    _envelope = 0f;
                    _naturalFadeTotal = 0;
                    _naturalFadeDone = 0;
                    _consumedFrames = 0;
                    _positionSeconds = _startOffset.TotalSeconds;
                    _log?.Info("material started", new { generation = _generation });
                    break;

                case MixCommandKind.Stop:
                    if (_active && _generation == command.Generation && !_stopping)
                    {
                        _stopping = true;
                        _log?.Info("material stopping", new { generation = _generation });
                    }

                    break;

                case MixCommandKind.StopAll:
                    if (_active)
                    {
                        _stopping = true;
                    }

                    break;

                case MixCommandKind.Shutdown:
                    if (_active)
                    {
                        CompleteActive(MaterialStopReason.Shutdown, null);
                    }

                    _running = false;
                    break;
            }
        }
    }

    private void RenderBlock(int frames)
    {
        var samples = frames * AudioConstants.Channels;

        var micRead = _mic.Read(_micBlock.AsSpan(0, samples));
        if (micRead < frames)
        {
            Array.Clear(_micBlock, micRead * AudioConstants.Channels, (frames - micRead) * AudioConstants.Channels);
            _micUnderrunFrames += frames - micRead;
        }

        Array.Clear(_materialBlock, 0, samples);
        var reasonAfterBlock = (MaterialStopReason?)null;
        string? failureMessage = null;

        if (_active && _source is not null)
        {
            if (_source.HasFailed)
            {
                reasonAfterBlock = MaterialStopReason.Failed;
                failureMessage = _source.FailureMessage;
                _envelope = 0f;
                Array.Clear(_materialBlock, 0, samples);
            }
            else
            {
                if (_source.EndOfStream && _source.BufferedFrames == 0 && !_finishing)
                {
                    reasonAfterBlock = MaterialStopReason.EndOfStream;
                }
                else
                {
                    if (!_finishing && !_stopping && _source.EndOfStream &&
                        _source.BufferedFrames <= frames + AudioConstants.FadeFrames)
                    {
                        _finishing = true;
                        _naturalFadeTotal = _source.BufferedFrames;
                        _naturalFadeDone = 0;
                    }

                    var read = _source.Read(_materialBlock.AsSpan(0, samples));
                    _consumedFrames += read;

                    if (_finishing && _naturalFadeTotal > 0)
                    {
                        for (var frame = 0; frame < read; frame++)
                        {
                            var gain = 1f - ((_naturalFadeDone + frame) / (float)_naturalFadeTotal);
                            gain = Math.Clamp(gain, 0f, 1f);
                            _materialBlock[frame * 2] *= gain;
                            _materialBlock[frame * 2 + 1] *= gain;
                        }
                    }

                    if (read < frames && !_source.EndOfStream && !_finishing)
                    {
                        _materialUnderrunFrames += frames - read;
                    }

                    if (_finishing)
                    {
                        _naturalFadeDone += read;
                        if (_naturalFadeDone >= _naturalFadeTotal)
                        {
                            reasonAfterBlock = _stopping ? MaterialStopReason.UserStopped : MaterialStopReason.EndOfStream;
                        }
                    }
                }

                if (reasonAfterBlock is null)
                {
                    if (_stopping)
                    {
                        _envelope -= frames / (float)AudioConstants.FadeFrames;
                        if (_envelope <= 0f)
                        {
                            _envelope = 0f;
                            reasonAfterBlock = MaterialStopReason.UserStopped;
                        }
                    }
                    else if (!_finishing)
                    {
                        _envelope = Math.Min(1f, _envelope + frames / (float)AudioConstants.FadeFrames);
                    }
                }
            }
        }
        else
        {
            _envelope = 0f;
        }

        _processor.Process(
            _micBlock.AsSpan(0, samples),
            _materialBlock.AsSpan(0, samples),
            frames,
            _active ? _envelope : 0f,
            _sendBlock.AsSpan(0, samples),
            _monitorBlock.AsSpan(0, samples));

        _send.Write(_sendBlock.AsSpan(0, samples));
        _monitor.Write(_monitorBlock.AsSpan(0, samples));
        _blocks++;

        if (_active)
        {
            _positionSeconds = _startOffset.TotalSeconds + _consumedFrames / (double)AudioConstants.SampleRate;
        }

        if (reasonAfterBlock is { } reason)
        {
            CompleteActive(reason, failureMessage);
            PublishMetrics();
        }
    }

    private void CompleteActive(MaterialStopReason reason, string? message)
    {
        if (!_active)
        {
            return;
        }

        var generation = _generation;
        _active = false;
        _stopping = false;
        _finishing = false;
        _envelope = 0f;
        _source = null;
        _naturalFadeTotal = 0;
        _naturalFadeDone = 0;
        _log?.Info("material completed", new { generation, reason, message });

        var completion = new MaterialCompletion(generation, reason, message);
        // Never run subscriber cleanup (file handles, joins) on the mix thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                MaterialCompletion?.Invoke(completion);
            }
            catch (Exception ex)
            {
                _log?.Error("material completion handler failed", ex);
            }
        });
    }

    private void PublishMetricsIfDue()
    {
        var now = Environment.TickCount64;
        if (now - _lastMetricsPublishMs < 50)
        {
            return;
        }

        _lastMetricsPublishMs = now;
        PublishMetrics();
    }

    private void PublishMetrics()
    {
        _metrics = new MixMetrics
        {
            SendBufferedFrames = SafeBuffered(_send),
            MonitorBufferedFrames = SafeBuffered(_monitor),
            MicrophoneBufferedFrames = _mic.BufferedFrames,
            MaterialBufferedFrames = _source?.BufferedFrames ?? 0,
            MicUnderrunFrames = _micUnderrunFrames,
            MaterialUnderrunFrames = _materialUnderrunFrames,
            BlocksRendered = _blocks,
            LastBlockMilliseconds = _lastBlockMs,
            MaxBlockMilliseconds = _maxBlockMs,
            SendDroppedFrames = SafeDropped(_send),
            MonitorDroppedFrames = SafeDropped(_monitor),
            MaterialActive = _active,
            MaterialEndOfStream = _source?.EndOfStream ?? false,
            Generation = _generation,
            PositionSeconds = _positionSeconds,
            Levels = _processor.Levels
        };
    }

    private static int SafeBuffered(IAudioSink sink)
    {
        try
        {
            return sink.BufferedFrames;
        }
        catch
        {
            return 0;
        }
    }

    private static int SafeDropped(IAudioSink sink)
    {
        try
        {
            return (int)Math.Min(int.MaxValue, sink.DroppedFrames);
        }
        catch
        {
            return 0;
        }
    }

    private readonly record struct MixCommand(
        MixCommandKind Kind,
        int Generation,
        IMaterialSource? Source,
        TimeSpan StartOffset)
    {
        public static MixCommand Start(int generation, IMaterialSource source, TimeSpan offset) =>
            new(MixCommandKind.Start, generation, source, offset);

        public static MixCommand Stop(int generation) => new(MixCommandKind.Stop, generation, null, default);
        public static MixCommand StopAll() => new(MixCommandKind.StopAll, -1, null, default);
        public static MixCommand Shutdown() => new(MixCommandKind.Shutdown, -1, null, default);
    }

    private enum MixCommandKind
    {
        Start,
        Stop,
        StopAll,
        Shutdown
    }
}

/// <summary>A sink used when an optional output is unavailable; it consumes nothing.</summary>
public sealed class NullAudioSink : IAudioSink
{
    public string Name { get; init; } = "unavailable";
    public int BufferedFrames => 0;
    public int CapacityFrames => 0;
    public long DroppedFrames { get; private set; }

    public void Write(ReadOnlySpan<float> frames) => DroppedFrames += frames.Length / 2;
    public void Clear() { }
}
