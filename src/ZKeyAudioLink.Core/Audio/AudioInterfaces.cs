namespace ZKeyAudioLink.Core.Audio;

/// <summary>Microphone side of the mixer. Reads interleaved 48 kHz stereo floats.</summary>
public interface IMicSource
{
    int BufferedFrames { get; }

    /// <summary>Reads up to destination.Length/2 frames. Returns frames read (0 when empty).</summary>
    int Read(Span<float> destination);
}

/// <summary>One output path of the mixer.</summary>
public interface IAudioSink
{
    string Name { get; }
    int BufferedFrames { get; }
    int CapacityFrames { get; }
    long DroppedFrames { get; }
    void Write(ReadOnlySpan<float> frames);
    void Clear();
}

/// <summary>Material side of the mixer. Reads interleaved 48 kHz stereo floats.</summary>
public interface IMaterialSource
{
    int BufferedFrames { get; }
    bool EndOfStream { get; }
    bool HasFailed { get; }
    string? FailureMessage { get; }

    /// <summary>Reads up to destination.Length/2 frames. Returns frames read.</summary>
    int Read(Span<float> destination);
}

public enum MaterialStopReason
{
    EndOfStream,
    UserStopped,
    Superseded,
    Failed,
    Shutdown
}

public sealed record MaterialCompletion(int Generation, MaterialStopReason Reason, string? Message);

public sealed class GainSnapshot
{
    public static readonly GainSnapshot Default = new(1f, 1f, 0.8f);

    public GainSnapshot(float microphone, float send, float monitor)
    {
        Microphone = microphone;
        Send = send;
        Monitor = monitor;
    }

    public float Microphone { get; }
    public float Send { get; }
    public float Monitor { get; }
}

public sealed class MixMetrics
{
    public int SendBufferedFrames { get; init; }
    public int MonitorBufferedFrames { get; init; }
    public int MicrophoneBufferedFrames { get; init; }
    public int MaterialBufferedFrames { get; init; }
    public long MicUnderrunFrames { get; init; }
    public long MaterialUnderrunFrames { get; init; }
    public long BlocksRendered { get; init; }
    public double LastBlockMilliseconds { get; init; }
    public double MaxBlockMilliseconds { get; init; }
    public int SendDroppedFrames { get; init; }
    public int MonitorDroppedFrames { get; init; }
    public bool MaterialActive { get; init; }
    public bool MaterialEndOfStream { get; init; }
    public int Generation { get; init; }
    public double PositionSeconds { get; init; }
    public MixLevels Levels { get; init; }
}
