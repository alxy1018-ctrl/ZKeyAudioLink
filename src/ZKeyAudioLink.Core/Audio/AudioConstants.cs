using NAudio.Wave;

namespace ZKeyAudioLink.Core.Audio;

/// <summary>
/// The internal working format of the whole engine. Every device boundary is
/// converted from/to this format; the mixer itself never changes format.
/// </summary>
public static class AudioConstants
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BytesPerSample = 4;
    public const int BytesPerFrame = Channels * BytesPerSample;
    public const int BlockFrames = 480;              // 10 ms
    public const int FadeFrames = 720;               // 15 ms fade in/out
    public const int SendTargetFrames = 2400;        // 50 ms in the output ring
    public const int MonitorTargetFrames = 2400;
    public const int SinkCapacityFrames = 12000;     // 250 ms hard bound
    public const int MicRingCapacityFrames = 48000;  // 1 s
    public const int MaterialRingCapacityFrames = 96000; // 2 s
    public const int PrefillFrames = 5760;           // 120 ms pre-roll before playing

    public static WaveFormat InternalWaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
}
