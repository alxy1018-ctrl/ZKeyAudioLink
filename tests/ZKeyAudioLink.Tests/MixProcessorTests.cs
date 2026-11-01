using ZKeyAudioLink.Core.Audio;

namespace ZKeyAudioLink.Tests;

public class MixProcessorTests
{
    private const int Frames = AudioConstants.BlockFrames;
    private const int Samples = Frames * AudioConstants.Channels;

    [Fact]
    public void MixesMicIntoSendAndMaterialIntoBothPaths()
    {
        var mic = Constant(0.1f);
        var material = Constant(0.2f);
        var send = new float[Samples];
        var monitor = new float[Samples];
        var processor = new MixProcessor();
        processor.Configure(2f, 0.5f, 0.25f);
        processor.Reset();

        processor.Process(mic, material, Frames, 1f, send, monitor);

        Assert.Equal(0.1 * 2 + 0.2 * 0.5, send[0], 3);
        Assert.Equal(0.2 * 0.25, monitor[0], 3);
        Assert.Equal(0.1 * 2 + 0.2 * 0.5, send[Frames * 2 - 1], 3);
        Assert.Equal(0.2 * 0.25, monitor[Frames * 2 - 1], 3);
        Assert.Equal(0.2, processor.Levels.MicPeak, 3);
        Assert.Equal(0.2, processor.Levels.MaterialPeak, 3);
    }

    [Fact]
    public void MonitorNeverContainsMicrophone()
    {
        var processor = new MixProcessor();
        processor.Configure(1f, 1f, 1f);
        processor.Reset();
        var send = new float[Samples];
        var monitor = new float[Samples];

        processor.Process(Constant(0.9f), Constant(0f), Frames, 1f, send, monitor);

        Assert.All(monitor, sample => Assert.Equal(0f, sample));
        Assert.True(Signal.Rms(send) > 0.5);
    }

    [Fact]
    public void LimiterKeepsTheSendPathBelowFullScale()
    {
        var processor = new MixProcessor();
        processor.Configure(1f, 1f, 0f);
        processor.Reset();
        var send = new float[Samples];
        var monitor = new float[Samples];
        var mic = Constant(0.9f);
        var material = Constant(0.9f);

        for (var block = 0; block < 10; block++)
        {
            processor.Process(mic, material, Frames, 1f, send, monitor);
        }

        var tail = send.AsSpan(Samples / 2);
        Assert.All(send, sample => Assert.InRange(sample, -1f, 1f));
        Assert.True(Signal.Peak(tail) <= 0.96, $"peak was {Signal.Peak(tail)}");
    }

    [Fact]
    public void MaterialEnvelopeMutesOnlyMaterial()
    {
        var processor = new MixProcessor();
        processor.Configure(1f, 1f, 1f);
        processor.Reset();
        var send = new float[Samples];
        var monitor = new float[Samples];

        processor.Process(Constant(0.2f), Constant(0.8f), Frames, 0f, send, monitor);

        Assert.True(Math.Abs(monitor[Samples - 1]) < 0.01, $"monitor tail={monitor[Samples - 1]}");
        Assert.True(send[0] > 0.5f);
        Assert.True(Math.Abs(send[Samples - 1] - 0.2f) < 0.01, $"send tail={send[Samples - 1]}");
    }

    private static float[] Constant(float value)
    {
        var block = new float[Samples];
        Array.Fill(block, value);
        return block;
    }
}
