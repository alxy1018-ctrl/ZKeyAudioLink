using ZKeyAudioLink.Core.Audio;
using ZKeyAudioLink.Core.Diagnostics;

namespace ZKeyAudioLink.Tests;

public class FileMaterialSourceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppLog _log;

    public FileMaterialSourceTests()
    {
        _log = new AppLog(_temp.File("logs"));
    }

    public void Dispose()
    {
        _log.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task OffsetStartsAtRequestedPositionAndRunsToEnd()
    {
        var path = WavFactory.WriteSilenceThenTone(_temp.File("two.wav"), 1.0, 1.0);
        using var source = new FileMaterialSource(path, TimeSpan.FromSeconds(1.0), _log);

        await source.PrefillAsync(CancellationToken.None);
        var (frames, samples) = Drain(source);

        Assert.InRange(frames, AudioConstants.SampleRate - 4000, AudioConstants.SampleRate + 4000);
        Assert.True(Signal.Rms(samples.AsSpan(0, Math.Min(samples.Length, AudioConstants.SampleRate / 4))) > 0.1,
            "the tone after the one-second offset was not decoded at the start");
    }

    [Fact]
    public async Task OffsetEarlierInSilenceKeepsSilence()
    {
        var path = WavFactory.WriteSilenceThenTone(_temp.File("two.wav"), 1.0, 1.0);
        using var source = new FileMaterialSource(path, TimeSpan.FromSeconds(0.5), _log);

        await source.PrefillAsync(CancellationToken.None);
        var (_, samples) = Drain(source);

        var firstQuarter = samples.AsSpan(0, Math.Min(samples.Length, AudioConstants.SampleRate / 2));
        Assert.True(Signal.Rms(firstQuarter) < 0.01);
        var later = samples.AsSpan(AudioConstants.SampleRate / 2);
        Assert.True(Signal.Rms(later) > 0.1);
    }

    [Fact]
    public async Task ShortFileEndsAtEndOfStream()
    {
        var path = WavFactory.Write(_temp.File("short.wav"), 0.1, _ => 0.2);
        using var source = new FileMaterialSource(path, TimeSpan.Zero, _log);

        await source.PrefillAsync(CancellationToken.None);
        var (frames, _) = Drain(source);

        Assert.InRange(frames, 4000, 6000);
        Assert.True(source.EndOfStream);
    }

    [Fact]
    public async Task ResamplesMono44kTo48kStereo()
    {
        var path = WavFactory.Write(_temp.File("mono44.wav"), 1.0, frame => 0.2 * Math.Sin(2 * Math.PI * 440 * frame / 44100.0),
            sampleRate: 44100, channels: 1);
        using var source = new FileMaterialSource(path, TimeSpan.Zero, _log);

        await source.PrefillAsync(CancellationToken.None);
        var (frames, _) = Drain(source);

        Assert.InRange(frames, 46000, 50000);
    }

    [Fact]
    public async Task CorruptFileFailsPrefill()
    {
        var path = _temp.File("broken.wav");
        await File.WriteAllTextAsync(path, "not wave");
        using var source = new FileMaterialSource(path, TimeSpan.Zero, _log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.PrefillAsync(CancellationToken.None));
    }

    private static (int Frames, float[] Samples) Drain(IMaterialSource source)
    {
        var destination = new float[AudioConstants.BlockFrames * 2];
        var collected = new List<float>();
        var frames = 0;
        var idle = 0;
        for (var i = 0; i < 500; i++)
        {
            var read = source.Read(destination);
            if (read > 0)
            {
                idle = 0;
                frames += read;
                for (var n = 0; n < read * 2; n++)
                {
                    collected.Add(destination[n]);
                }
            }
            else if (source.EndOfStream && source.BufferedFrames == 0)
            {
                break;
            }
            else
            {
                idle++;
                if (idle > 50)
                {
                    break;
                }

                Thread.Sleep(10);
            }
        }

        return (frames, collected.ToArray());
    }
}
