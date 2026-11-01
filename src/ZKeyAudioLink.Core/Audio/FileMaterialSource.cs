using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ZKeyAudioLink.Core.Diagnostics;

namespace ZKeyAudioLink.Core.Audio;

/// <summary>
/// Decodes one media file from its saved start offset into a bounded 48 kHz
/// stereo float ring. A single decoder thread is the only reader of the file,
/// which is why both outputs can consume the same stream without competing for
/// a reader position.
/// </summary>
public sealed class FileMaterialSource : IMaterialSource, IDisposable
{
    private readonly FloatRingBuffer _ring = new(AudioConstants.MaterialRingCapacityFrames);
    private readonly AppLog? _log;
    private readonly string _path;
    private readonly TimeSpan _startOffset;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _spaceAvailable = new(false);
    private readonly object _gate = new();
    private Thread? _decoder;
    private volatile bool _endOfStream;
    private volatile bool _hasFailed;
    private string? _failureMessage;
    private long _framesProvided;
    private bool _disposed;
    private bool _started;

    public FileMaterialSource(string path, TimeSpan startOffset, AppLog? log = null)
    {
        _path = path;
        _startOffset = startOffset;
        _log = log;
    }

    public string Path => _path;
    public TimeSpan StartOffset => _startOffset;
    public long FramesProvided => Interlocked.Read(ref _framesProvided);
    public int BufferedFrames => _ring.CountFrames;
    public bool EndOfStream => _endOfStream;
    public bool HasFailed => _hasFailed;
    public string? FailureMessage => _failureMessage;

    public async Task PrefillAsync(CancellationToken cancellationToken)
    {
        var ready = Task.Run(() => StartDecoder(cancellationToken), cancellationToken);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_hasFailed)
            {
                throw new InvalidOperationException(_failureMessage ?? "解码失败。");
            }

            var buffered = _ring.CountFrames;
            if (buffered >= AudioConstants.PrefillFrames || _endOfStream)
            {
                break;
            }

            if (DateTime.UtcNow > deadline)
            {
                if (buffered > 0)
                {
                    _log?.Warning("material prefill timed out; starting with partial buffer", new { buffered });
                    break;
                }

                throw new TimeoutException("解码准备超时，音频没有产生数据。");
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        await ready.ConfigureAwait(false);
        if (_hasFailed)
        {
            throw new InvalidOperationException(_failureMessage ?? "解码失败。");
        }

        if (_endOfStream && _ring.CountFrames == 0)
        {
            throw new InvalidOperationException("从该开始时间起没有可播放的音频。");
        }
    }

    private void StartDecoder(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        var thread = new Thread(() => DecodeLoop(cancellationToken))
        {
            Name = "ZKeyAudioLink.Decoder",
            IsBackground = true,
            Priority = ThreadPriority.Normal
        };
        lock (_gate)
        {
            _decoder = thread;
        }

        thread.Start();
    }

    private void DecodeLoop(CancellationToken cancellationToken)
    {
        AudioFileReader? reader = null;
        try
        {
            reader = new AudioFileReader(_path);
            if (_startOffset > TimeSpan.Zero)
            {
                reader.CurrentTime = _startOffset;
            }

            var actual = reader.CurrentTime;
            if ((actual - _startOffset).Duration() > TimeSpan.FromMilliseconds(250))
            {
                _log?.Warning("seek landed away from the requested offset", new
                {
                    requested = _startOffset.TotalSeconds,
                    actual = actual.TotalSeconds
                });
            }

            ISampleProvider provider = reader;
            if (provider.WaveFormat.Channels == 1)
            {
                provider = new MonoToStereoSampleProvider(provider);
            }
            else if (provider.WaveFormat.Channels > 2)
            {
                provider = new FirstTwoChannelsSampleProvider(provider);
            }

            if (provider.WaveFormat.SampleRate != AudioConstants.SampleRate)
            {
                provider = new WdlResamplingSampleProvider(provider, AudioConstants.SampleRate);
            }

            var buffer = new float[AudioConstants.BlockFrames * AudioConstants.Channels * 4];
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                var read = provider.Read(buffer.AsSpan());
                if (read <= 0)
                {
                    _endOfStream = true;
                    _spaceAvailable.Set();
                    break;
                }

                var frames = read / AudioConstants.Channels;
                var offset = 0;
                while (offset < frames)
                {
                    if (cancellationToken.IsCancellationRequested || _disposed)
                    {
                        return;
                    }

                    var remaining = frames - offset;
                    var written = _ring.Write(buffer.AsSpan(offset * AudioConstants.Channels, remaining * AudioConstants.Channels));
                    if (written <= 0)
                    {
                        _spaceAvailable.Wait(20, cancellationToken);
                        continue;
                    }

                    offset += written;
                    Interlocked.Add(ref _framesProvided, written);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop while the decoder was waiting for ring space.
        }
        catch (Exception ex)
        {
            _hasFailed = true;
            _failureMessage = $"解码失败：{ex.Message}";
            _log?.Error("material decode failed", new { file = _path, error = ex.Message });
        }
        finally
        {
            try
            {
                reader?.Dispose();
            }
            catch
            {
                // ignore
            }

            _spaceAvailable.Set();
        }
    }

    public int Read(Span<float> destination)
    {
        var read = _ring.Read(destination);
        if (read > 0)
        {
            _spaceAvailable.Set();
        }

        return read;
    }

    public void Clear() => _ring.Clear();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignore
        }

        _spaceAvailable.Set();
        Thread? decoder;
        lock (_gate)
        {
            decoder = _decoder;
        }

        if (decoder is not null && decoder.IsAlive)
        {
            decoder.Join(TimeSpan.FromSeconds(2));
        }

        _cts.Dispose();
        _spaceAvailable.Dispose();
        _ring.Clear();
    }
}

/// <summary>Selects the first two channels of a multichannel stream.</summary>
public sealed class FirstTwoChannelsSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private readonly float[] _buffer;

    public FirstTwoChannelsSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        if (_sourceChannels < 2)
        {
            throw new ArgumentException("Source must have at least two channels.", nameof(source));
        }

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        _buffer = new float[AudioConstants.BlockFrames * _sourceChannels * 4];
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        var sourceSamples = buffer.Length / 2 * _sourceChannels;
        if (sourceSamples > _buffer.Length)
        {
            sourceSamples = _buffer.Length / _sourceChannels * _sourceChannels;
        }

        var read = _source.Read(_buffer.AsSpan(0, sourceSamples));
        var frames = read / _sourceChannels;
        for (var frame = 0; frame < frames; frame++)
        {
            buffer[frame * 2] = _buffer[frame * _sourceChannels];
            buffer[frame * 2 + 1] = _buffer[frame * _sourceChannels + 1];
        }

        return frames * 2;
    }
}
