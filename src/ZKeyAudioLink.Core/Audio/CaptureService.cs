using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ZKeyAudioLink.Core.Diagnostics;

namespace ZKeyAudioLink.Core.Audio;

/// <summary>
/// Continuous WASAPI microphone capture. The WASAPI callback only copies its
/// span into a bounded queue; format conversion and ring-buffer writes happen on
/// a dedicated pump thread. The requested capture format is the engine format,
/// so the normal path is a straight float copy.
/// </summary>
public sealed class CaptureService : IMicSource, IDisposable
{
    private readonly FloatRingBuffer _ring = new(AudioConstants.MicRingCapacityFrames);
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly AppLog? _log;
    private readonly object _gate = new();
    private WasapiRecorder? _recorder;
    private Thread? _pump;
    private CancellationTokenSource? _pumpCts;
    private long _queuedBytes;
    private long _droppedBytes;
    private long _droppedFrames;
    private bool _stopping;
    private bool _disposed;
    private InternalFormatConverter? _converter;
    private WaveFormat? _format;

    private const int MaxQueuedBytes = AudioConstants.SampleRate * AudioConstants.BytesPerFrame * 2;

    public CaptureService(AppLog? log = null)
    {
        _log = log;
    }

    public string? DeviceName { get; private set; }
    public string? DeviceId { get; private set; }
    public WaveFormat? Format => _format;
    public bool IsRunning { get; private set; }
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public int BufferedFrames => _ring.CountFrames;

    public event Action<Exception?>? Failed;

    public void Start(MMDevice device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        var recorder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(100)
            .WithFormat(AudioConstants.InternalWaveFormat)
            .Build();

        _format = recorder.WaveFormat;
        _converter = InternalFormatConverter.IsInternalFormat(_format) ? null : new InternalFormatConverter(_format);
        if (_converter is not null)
        {
            _log?.Warning("capture format differs from engine format; using fallback converter", new
            {
                capture = _format.ToString()
            });
        }

        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
        _stopping = false;
        _ring.Clear();
        _queue.Clear();
        Interlocked.Exchange(ref _queuedBytes, 0);

        recorder.DataAvailable += OnDataAvailable;
        recorder.RecordingStopped += OnRecordingStopped;
        try
        {
            recorder.StartRecording();
        }
        catch
        {
            recorder.DataAvailable -= OnDataAvailable;
            recorder.RecordingStopped -= OnRecordingStopped;
            recorder.Dispose();
            throw;
        }

        var cts = new CancellationTokenSource();
        var pump = new Thread(() => PumpLoop(cts.Token))
        {
            Name = "ZKeyAudioLink.MicPump",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        lock (_gate)
        {
            _recorder = recorder;
            _pumpCts = cts;
            _pump = pump;
            IsRunning = true;
        }

        pump.Start();
        _log?.Info("microphone capture started", new { device = DeviceName, deviceId = DeviceId, format = _format.ToString() });
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var copy = buffer.ToArray();
        var queued = Interlocked.Add(ref _queuedBytes, copy.Length);
        _queue.Enqueue(copy);

        // Bound the queue if the pump cannot keep up. Drop oldest chunks so the
        // capture side never grows without limit.
        while (queued > MaxQueuedBytes && _queue.TryDequeue(out var old))
        {
            queued = Interlocked.Add(ref _queuedBytes, -old.Length);
            Interlocked.Add(ref _droppedBytes, old.Length);
        }
    }

    private void PumpLoop(CancellationToken token)
    {
        var floatBuffer = new float[AudioConstants.MicRingCapacityFrames * AudioConstants.Channels];
        while (!token.IsCancellationRequested)
        {
            var gotOne = false;
            while (_queue.TryDequeue(out var bytes))
            {
                gotOne = true;
                Interlocked.Add(ref _queuedBytes, -bytes.Length);
                var frames = Convert(bytes, floatBuffer);
                if (frames > 0)
                {
                    var written = _ring.Write(floatBuffer.AsSpan(0, frames * AudioConstants.Channels));
                    if (written < frames)
                    {
                        Interlocked.Add(ref _droppedFrames, frames - written);
                    }
                }
            }

            if (!gotOne)
            {
                token.WaitHandle.WaitOne(5);
            }
        }
    }

    private int Convert(byte[] bytes, float[] destination)
    {
        if (_converter is null)
        {
            var floats = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
            var frames = floats.Length / AudioConstants.Channels;
            if (destination.Length < frames * AudioConstants.Channels)
            {
                return 0;
            }

            floats.CopyTo(destination);
            return frames;
        }

        return _converter.Convert(bytes, destination);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        IsRunning = false;
        _log?.Error("microphone capture stopped", new { device = DeviceName, error = e.Exception?.Message });
        Failed?.Invoke(e.Exception);
    }

    public int Read(Span<float> destination) => _ring.Read(destination);

    public void Stop()
    {
        WasapiRecorder? recorder;
        Thread? pump;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            _stopping = true;
            recorder = _recorder;
            pump = _pump;
            cts = _pumpCts;
            _recorder = null;
            _pump = null;
            _pumpCts = null;
            IsRunning = false;
        }

        if (recorder is not null)
        {
            recorder.DataAvailable -= OnDataAvailable;
            recorder.RecordingStopped -= OnRecordingStopped;
            try
            {
                recorder.StopRecording();
            }
            catch (Exception ex)
            {
                _log?.Warning("microphone stop failed", new { error = ex.Message });
            }

            try
            {
                recorder.Dispose();
            }
            catch
            {
                // ignore teardown errors
            }
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        if (pump is not null && pump.IsAlive)
        {
            pump.Join(TimeSpan.FromSeconds(2));
        }

        cts?.Dispose();
        _queue.Clear();
        Interlocked.Exchange(ref _queuedBytes, 0);
        _ring.Clear();
        _converter = null;
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
}

/// <summary>
/// Fallback converter for the unlikely case that WASAPI declines to capture at
/// 48 kHz float stereo. Handles PCM 16/24/32 and IEEE float, mono/stereo, and
/// linear-interpolation resampling. The normal path never uses this class.
/// </summary>
public sealed class InternalFormatConverter
{
    private readonly WaveFormat _source;
    private readonly double _ratio;
    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly int _bytesPerFrame;
    private readonly double[] _previous = new double[2];
    private double _position;
    private bool _hasPrevious;

    public InternalFormatConverter(WaveFormat source)
    {
        _source = source;
        _ratio = AudioConstants.SampleRate / (double)source.SampleRate;
        _channels = Math.Max(1, source.Channels);
        _bytesPerSample = source.BitsPerSample / 8;
        _bytesPerFrame = Math.Max(1, source.BlockAlign);

        if (source.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat) ||
            source.BitsPerSample is not (16 or 24 or 32) || _bytesPerSample <= 0)
        {
            throw new NotSupportedException($"Unsupported capture format {source}.");
        }
    }

    public static bool IsInternalFormat(WaveFormat format) =>
        format.SampleRate == AudioConstants.SampleRate &&
        format.Channels == AudioConstants.Channels &&
        format.Encoding == WaveFormatEncoding.IeeeFloat &&
        format.BitsPerSample == 32;

    public int Convert(ReadOnlySpan<byte> source, float[] destination)
    {
        var frames = source.Length / _bytesPerFrame;
        if (frames <= 0)
        {
            return 0;
        }

        return ConvertFrames(source, frames, destination);
    }

    public int ConvertFrames(ReadOnlySpan<byte> source, int frames, float[] destination)
    {
        var capacity = destination.Length / AudioConstants.Channels;
        var written = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * _bytesPerFrame;
            ReadStereoFrame(source.Slice(offset, _bytesPerFrame), out var left, out var right);

            if (!_hasPrevious)
            {
                _previous[0] = left;
                _previous[1] = right;
                _hasPrevious = true;
            }

            while (_position < 1.0)
            {
                if (written >= capacity)
                {
                    break;
                }

                var l = _previous[0] + (left - _previous[0]) * _position;
                var r = _previous[1] + (right - _previous[1]) * _position;
                destination[written * 2] = (float)l;
                destination[written * 2 + 1] = (float)r;
                written++;
                _position += _ratio;
            }

            _position -= 1.0;
            _previous[0] = left;
            _previous[1] = right;
        }

        return written;
    }

    private void ReadStereoFrame(ReadOnlySpan<byte> frame, out double left, out double right)
    {
        double first;
        double second;
        if (_source.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            first = BitConverter.ToSingle(frame[..4]);
            second = _channels >= 2 ? BitConverter.ToSingle(frame.Slice(_bytesPerSample, 4)) : first;
        }
        else
        {
            first = ReadPcm(frame[.._bytesPerSample]);
            second = _channels >= 2 ? ReadPcm(frame.Slice(_bytesPerSample, _bytesPerSample)) : first;
        }

        left = first;
        right = second;
    }

    private double ReadPcm(ReadOnlySpan<byte> sample)
    {
        return _bytesPerSample switch
        {
            2 => BitConverter.ToInt16(sample) / 32768.0,
            3 => (sample[0] | (sample[1] << 8) | ((sbyte)sample[2] << 16)) / 8388608.0,
            4 => BitConverter.ToInt32(sample) / 2147483648.0,
            _ => 0.0
        };
    }
}
