using System.Text;
using ZKeyAudioLink.Core.Audio;

namespace ZKeyAudioLink.Tests;

public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZKeyAudioLinkTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // test cleanup best-effort
        }
    }
}

public static class WavFactory
{
    /// <summary>Writes a PCM16 WAV. <paramref name="sampleAt"/> receives the frame index.</summary>
    public static string Write(
        string path,
        double seconds,
        Func<int, double> sampleAt,
        int sampleRate = AudioConstants.SampleRate,
        int channels = 2)
    {
        var frames = (int)Math.Round(seconds * sampleRate);
        var dataBytes = frames * channels * 2;
        using var stream = System.IO.File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((ushort)(channels * 2));
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (var frame = 0; frame < frames; frame++)
        {
            var value = Math.Clamp(sampleAt(frame), -1.0, 1.0);
            var pcm = (short)Math.Round(value * short.MaxValue);
            for (var channel = 0; channel < channels; channel++)
            {
                writer.Write(pcm);
            }
        }

        return path;
    }

    public static string WriteSilenceThenTone(string path, double silenceSeconds, double toneSeconds, double amplitude = 0.28)
    {
        const double frequency = 440.0;
        var baseFrames = (int)Math.Round(silenceSeconds * AudioConstants.SampleRate);
        var toneFrames = (int)Math.Round(toneSeconds * AudioConstants.SampleRate);
        return Write(path, silenceSeconds + toneSeconds, frame =>
        {
            if (frame < baseFrames)
            {
                return 0;
            }

            var index = frame - baseFrames;
            return amplitude * Math.Sin(2 * Math.PI * frequency * index / AudioConstants.SampleRate);
        });
    }
}

public sealed class CollectingSink : IAudioSink
{
    private readonly object _gate = new();
    private readonly List<float> _samples = new();

    public CollectingSink(int maxFrames = AudioConstants.SampleRate * 8)
    {
        MaxFrames = maxFrames;
    }

    public string Name => "collecting";
    public int MaxFrames { get; }
    public int BufferedFrames => 0;
    public int CapacityFrames => int.MaxValue;
    public long DroppedFrames { get; private set; }
    public bool Overflowed { get; private set; }

    public int FrameCount
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count / AudioConstants.Channels;
            }
        }
    }

    public void Write(ReadOnlySpan<float> frames)
    {
        lock (_gate)
        {
            var incomingFrames = frames.Length / AudioConstants.Channels;
            if (_samples.Count / AudioConstants.Channels + incomingFrames > MaxFrames)
            {
                Overflowed = true;
                DroppedFrames += incomingFrames;
                return;
            }

            for (var i = 0; i < frames.Length; i++)
            {
                _samples.Add(frames[i]);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _samples.Clear();
        }
    }

    public float[] Snapshot()
    {
        lock (_gate)
        {
            return _samples.ToArray();
        }
    }

    public float[] Slice(int startFrame, int frameCount)
    {
        lock (_gate)
        {
            var start = startFrame * AudioConstants.Channels;
            var count = Math.Min(frameCount * AudioConstants.Channels, _samples.Count - start);
            return count <= 0 ? Array.Empty<float>() : _samples.GetRange(start, count).ToArray();
        }
    }
}

public sealed class ConstantMicSource : IMicSource
{
    private readonly float _value;

    public ConstantMicSource(float value)
    {
        _value = value;
    }

    public int BufferedFrames => 1;

    public int Read(Span<float> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = _value;
        }

        return destination.Length / AudioConstants.Channels;
    }
}

public static class Signal
{
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    public static double Peak(ReadOnlySpan<float> samples)
    {
        double peak = 0;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return peak;
    }
}
