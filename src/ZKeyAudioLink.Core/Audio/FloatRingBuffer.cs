namespace ZKeyAudioLink.Core.Audio;

/// <summary>
/// Bounded, interleaved float ring buffer. One writer and one reader; every
/// operation copies under a short lock (never called from the WASAPI capture
/// callback itself — that callback only copies into a queue).
/// </summary>
public sealed class FloatRingBuffer
{
    private readonly object _gate = new();
    private readonly float[] _data;
    private int _read;
    private int _write;
    private int _count;

    public FloatRingBuffer(int capacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        CapacityFrames = capacityFrames;
        _data = new float[capacityFrames * AudioConstants.Channels];
    }

    public int CapacityFrames { get; }

    public int CountFrames
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public long TotalWrittenFrames { get; private set; }
    public long TotalReadFrames { get; private set; }
    public long DroppedFrames { get; private set; }

    /// <summary>Writes as many frames as fit. Returns the number of frames written.</summary>
    public int Write(ReadOnlySpan<float> frames)
    {
        var frameCount = frames.Length / AudioConstants.Channels;
        if (frameCount <= 0)
        {
            return 0;
        }

        lock (_gate)
        {
            var space = CapacityFrames - _count;
            var toWrite = Math.Min(frameCount, space);
            var samples = toWrite * AudioConstants.Channels;
            if (samples <= 0)
            {
                return 0;
            }

            var first = Math.Min(samples, _data.Length - _write);
            frames[..first].CopyTo(_data.AsSpan(_write, first));
            if (samples > first)
            {
                frames.Slice(first, samples - first).CopyTo(_data.AsSpan(0, samples - first));
            }

            _write = (_write + samples) % _data.Length;
            _count += toWrite;
            TotalWrittenFrames += toWrite;
            return toWrite;
        }
    }

    /// <summary>Reads as many frames as available. Returns the number of frames read.</summary>
    public int Read(Span<float> destination)
    {
        var frameCount = destination.Length / AudioConstants.Channels;
        if (frameCount <= 0)
        {
            return 0;
        }

        lock (_gate)
        {
            var toRead = Math.Min(frameCount, _count);
            var samples = toRead * AudioConstants.Channels;
            if (samples <= 0)
            {
                return 0;
            }

            var first = Math.Min(samples, _data.Length - _read);
            _data.AsSpan(_read, first).CopyTo(destination[..first]);
            if (samples > first)
            {
                _data.AsSpan(0, samples - first).CopyTo(destination.Slice(first, samples - first));
            }

            _read = (_read + samples) % _data.Length;
            _count -= toRead;
            TotalReadFrames += toRead;
            return toRead;
        }
    }

    /// <summary>Discards the oldest frames (used to bound latency on overrun).</summary>
    public int DiscardOldest(int frames)
    {
        if (frames <= 0)
        {
            return 0;
        }

        lock (_gate)
        {
            var toDiscard = Math.Min(frames, _count);
            var samples = toDiscard * AudioConstants.Channels;
            _read = (_read + samples) % _data.Length;
            _count -= toDiscard;
            DroppedFrames += toDiscard;
            return toDiscard;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _read = 0;
            _write = 0;
            _count = 0;
        }
    }

    /// <summary>Writes a whole block or fails if it does not fit.</summary>
    public bool TryWriteAll(ReadOnlySpan<float> frames)
    {
        var frameCount = frames.Length / AudioConstants.Channels;
        if (frameCount <= 0)
        {
            return true;
        }

        lock (_gate)
        {
            if (CapacityFrames - _count < frameCount)
            {
                return false;
            }
        }

        Write(frames);
        return true;
    }
}
