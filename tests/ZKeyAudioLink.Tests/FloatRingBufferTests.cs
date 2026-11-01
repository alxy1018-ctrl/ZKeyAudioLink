using ZKeyAudioLink.Core.Audio;

namespace ZKeyAudioLink.Tests;

public class FloatRingBufferTests
{
    [Fact]
    public void WriteReadWrapsAround()
    {
        var ring = new FloatRingBuffer(100);
        var first = new float[80 * 2];
        Array.Fill(first, 0.5f);
        Assert.Equal(80, ring.Write(first));

        var read = new float[40 * 2];
        Assert.Equal(40, ring.Read(read));
        Assert.All(read, sample => Assert.Equal(0.5f, sample));

        var second = new float[50 * 2];
        Array.Fill(second, 1.5f);
        Assert.Equal(50, ring.Write(second));
        Assert.Equal(90, ring.CountFrames);

        var everything = new float[100 * 2];
        Assert.Equal(90, ring.Read(everything));
        Assert.Equal(0, ring.CountFrames);
    }

    [Fact]
    public void ReportsPartialWritesAndDropsOnDiscard()
    {
        var ring = new FloatRingBuffer(10);
        var block = new float[8 * 2];
        Array.Fill(block, 0.25f);
        Assert.Equal(8, ring.Write(block));
        Assert.Equal(2, ring.Write(block));
        Assert.Equal(2, ring.DiscardOldest(2));
        Assert.Equal(8, ring.CountFrames);
        Assert.Equal(2, ring.DroppedFrames);
    }

    [Fact]
    public void TryWriteAllIsAtomic()
    {
        var ring = new FloatRingBuffer(10);
        var block = new float[8 * 2];
        Assert.True(ring.TryWriteAll(block));
        Assert.False(ring.TryWriteAll(new float[5 * 2]));
        Assert.Equal(8, ring.CountFrames);
    }

    [Fact]
    public void EmptyReadReturnsZero()
    {
        var ring = new FloatRingBuffer(10);
        Assert.Equal(0, ring.Read(new float[4 * 2]));
    }
}
