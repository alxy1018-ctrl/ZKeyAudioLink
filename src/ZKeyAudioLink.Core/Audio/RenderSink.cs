using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ZKeyAudioLink.Core.Diagnostics;

namespace ZKeyAudioLink.Core.Audio;

/// <summary>
/// One WASAPI shared-mode output with its own bounded ring buffer. The player
/// always has data to pull (silence when empty), so it never interprets an
/// underrun as end-of-stream. Clock drift between the two output devices shows
/// up as ring depth changes; this sink nudges one frame when the depth leaves
/// its allowed band, keeping both paths aligned without touching the other path.
/// </summary>
public sealed class RenderSink : IAudioSink, IDisposable
{
    private readonly object _gate = new();
    private readonly FloatRingBuffer _ring = new(AudioConstants.SinkCapacityFrames);
    private readonly AppLog? _log;
    private readonly bool _isMonitor;
    private readonly int _latencyMs;
    private WasapiPlayer? _player;
    private bool _hasLastFrame;
    private float _lastLeft;
    private float _lastRight;
    private long _underrunFrames;
    private long _clockCorrections;
    private long _extraDroppedFrames;
    private bool _disposed;

    public RenderSink(string name, bool isMonitor, int latencyMs = 60, AppLog? log = null)
    {
        Name = name;
        _isMonitor = isMonitor;
        _latencyMs = latencyMs;
        _log = log;
    }

    public string Name { get; }
    public string? DeviceId { get; private set; }
    public string? DeviceName { get; private set; }
    public bool IsStarted { get; private set; }

    public int BufferedFrames => _ring.CountFrames;
    public int CapacityFrames => _ring.CapacityFrames;
    public long DroppedFrames => _ring.DroppedFrames + Interlocked.Read(ref _extraDroppedFrames);
    public long UnderrunFrames => Interlocked.Read(ref _underrunFrames);
    public long ClockCorrections => Interlocked.Read(ref _clockCorrections);

    public event Action<RenderSink, Exception?>? Failed;

    /// <summary>Opens the device and starts pulling silence. Throws on device errors.</summary>
    public void Start(MMDevice device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        DeviceId = device.ID;
        DeviceName = device.FriendlyName;

        var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(_latencyMs)
            .Build();

        var provider = new SinkWaveProvider(this);
        player.PlaybackStopped += OnPlaybackStopped;
        try
        {
            player.Init(provider);
            _ring.Clear();
            _hasLastFrame = false;
            lock (_gate)
            {
                _player = player;
                IsStarted = true;
            }

            player.Play();
            _log?.Info("render sink started", new
            {
                sink = Name,
                device = DeviceName,
                deviceId = DeviceId,
                latencyMs = _latencyMs,
                deviceMix = player.DeviceMixFormat.ToString()
            });
        }
        catch
        {
            player.PlaybackStopped -= OnPlaybackStopped;
            player.Dispose();
            throw;
        }
    }

    public void Write(ReadOnlySpan<float> frames)
    {
        if (frames.Length == 0)
        {
            return;
        }

        var written = _ring.Write(frames);
        var framesGiven = frames.Length / AudioConstants.Channels;
        if (written < framesGiven)
        {
            Interlocked.Add(ref _extraDroppedFrames, framesGiven - written);
        }
    }

    public void Clear()
    {
        _ring.Clear();
        _hasLastFrame = false;
    }

    private void ReadFrames(Span<float> destination)
    {
        var framesWanted = destination.Length / AudioConstants.Channels;
        if (framesWanted <= 0)
        {
            return;
        }

        var level = _ring.CountFrames;
        var highWater = _isMonitor ? 4800 : 7200;   // 100 ms / 150 ms
        var lowWater = _isMonitor ? 960 : 480;      // 20 ms / 10 ms

        if (level > highWater)
        {
            // The consumer is slower than the producer: shed one frame per pull.
            _ring.DiscardOldest(1);
            _clockCorrections++;
        }
        else if (level > 0 && level < lowWater && _hasLastFrame)
        {
            // The consumer is faster than the producer: hold the previous frame
            // for one frame. The ring level rises by one frame, so this branch
            // self-limits to the actual clock deficit instead of repeating every
            // pull. One 48 kHz frame is a much smaller artifact than a gap.
            destination[0] = _lastLeft;
            destination[1] = _lastRight;
            var readAfterDuplicate = _ring.Read(destination[2..]);
            var totalFrames = readAfterDuplicate + 1;
            if (readAfterDuplicate > 0)
            {
                _lastLeft = destination[2 + (readAfterDuplicate - 1) * AudioConstants.Channels];
                _lastRight = destination[2 + (readAfterDuplicate - 1) * AudioConstants.Channels + 1];
            }

            PadSilence(destination, totalFrames);
            Interlocked.Add(ref _underrunFrames, framesWanted - totalFrames);
            _clockCorrections++;
            return;
        }

        var read = _ring.Read(destination);
        if (read > 0)
        {
            _lastLeft = destination[(read - 1) * AudioConstants.Channels];
            _lastRight = destination[(read - 1) * AudioConstants.Channels + 1];
            _hasLastFrame = true;
        }

        if (read < framesWanted)
        {
            PadSilence(destination, read);
            Interlocked.Add(ref _underrunFrames, framesWanted - read);
        }
    }

    private static void PadSilence(Span<float> destination, int framesWritten)
    {
        var start = framesWritten * AudioConstants.Channels;
        if (start < destination.Length)
        {
            destination[start..].Clear();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            IsStarted = false;
        }

        _log?.Error("render sink stopped", new { sink = Name, device = DeviceName, error = e.Exception?.Message });
        Failed?.Invoke(this, e.Exception);
    }

    public void Stop()
    {
        WasapiPlayer? player;
        lock (_gate)
        {
            player = _player;
            _player = null;
            IsStarted = false;
        }

        if (player is null)
        {
            return;
        }

        try
        {
            player.PlaybackStopped -= OnPlaybackStopped;
            player.Stop();
        }
        catch (Exception ex)
        {
            _log?.Warning("render sink stop failed", new { sink = Name, error = ex.Message });
        }

        try
        {
            player.Dispose();
        }
        catch
        {
            // ignore COM teardown errors
        }

        _ring.Clear();
        _hasLastFrame = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private sealed class SinkWaveProvider : IWaveProvider
    {
        private readonly RenderSink _sink;

        public SinkWaveProvider(RenderSink sink)
        {
            _sink = sink;
        }

        public WaveFormat WaveFormat => AudioConstants.InternalWaveFormat;

        public int Read(Span<byte> buffer)
        {
            var floats = MemoryMarshal.Cast<byte, float>(buffer);
            _sink.ReadFrames(floats);
            return buffer.Length;
        }
    }
}
