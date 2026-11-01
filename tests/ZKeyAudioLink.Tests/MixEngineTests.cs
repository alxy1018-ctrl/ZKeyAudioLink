using ZKeyAudioLink.Core.Audio;
using ZKeyAudioLink.Core.Diagnostics;
using ZKeyAudioLink.Core.Media;

namespace ZKeyAudioLink.Tests;

public class MixEngineTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppLog _log;

    public MixEngineTests()
    {
        _log = new AppLog(_temp.File("logs"));
    }

    public void Dispose()
    {
        _log.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task MaterialReachesBothPathsWhileMicrophoneOnlyReachesSend()
    {
        var path = WavFactory.Write(_temp.File("material.wav"), 1.5, frame =>
        {
            const int toneStart = AudioConstants.SampleRate / 2;
            return frame < toneStart
                ? 0
                : 0.28 * Math.Sin(2 * Math.PI * 440 * (frame - toneStart) / (double)AudioConstants.SampleRate);
        });

        var send = new CollectingSink();
        var monitor = new CollectingSink();
        using var source = new FileMaterialSource(path, TimeSpan.Zero, _log);
        await source.PrefillAsync(CancellationToken.None);
        var completion = new TaskCompletionSource<MaterialCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new MixEngine(new ConstantMicSource(0.1f), send, monitor, _log);
        engine.MaterialCompletion += c => completion.TrySetResult(c);
        engine.Start();
        engine.StartMaterial(1, source, TimeSpan.Zero);

        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(MaterialStopReason.EndOfStream, result.Reason);

        var monitorSamples = monitor.Snapshot();
        var sendSamples = send.Snapshot();
        var frames = monitor.FrameCount;
        Assert.True(frames >= (int)(1.5 * AudioConstants.SampleRate) - 4800, $"only {frames} frames rendered");

        var firstTone = FindFirstAbove(monitorSamples, 0.01f);
        Assert.True(firstTone >= 0, "material never appeared on the monitor path");
        var firstToneSeconds = firstTone / 2.0 / AudioConstants.SampleRate;
        Assert.InRange(firstToneSeconds, 0.45, 0.7);

        var monitorTailStart = Math.Min(monitorSamples.Length - 1, firstTone + AudioConstants.SampleRate / 2);
        Assert.True(Signal.Rms(monitorSamples.AsSpan(monitorTailStart)) > 0.03);
        Assert.True(Signal.Rms(sendSamples) > 0.1);

        var sendStart = sendSamples.AsSpan(0, Math.Min(sendSamples.Length, AudioConstants.SampleRate / 2));
        Assert.True(Signal.Rms(sendStart) > 0.05, "mic should reach the send path before material starts");
    }

    [Fact]
    public async Task StoppingMaterialKeepsMicrophoneFlowing()
    {
        var send = new CollectingSink(maxFrames: AudioConstants.SampleRate * 20);
        var monitor = new CollectingSink(maxFrames: AudioConstants.SampleRate * 20);
        var source = new InfiniteMaterialSource(0.5f);
        var completion = new TaskCompletionSource<MaterialCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new MixEngine(new ConstantMicSource(0.1f), send, monitor, _log);
        engine.MaterialCompletion += c => completion.TrySetResult(c);
        engine.Start();
        engine.StartMaterial(1, source, TimeSpan.Zero);
        await Task.Delay(120);
        engine.StopMaterial(1);

        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MaterialStopReason.UserStopped, result.Reason);
        await Task.Delay(60);

        var sendSamples = send.Snapshot();
        Assert.True(sendSamples.Length > AudioConstants.SampleRate, "not enough audio captured");
        var tail = sendSamples.AsSpan(sendSamples.Length - 4000);
        Assert.InRange(Signal.Rms(tail), 0.08, 0.12);
        var monitorTail = monitor.Snapshot().AsSpan(^4000);
        Assert.True(Signal.Rms(monitorTail) < 0.02, "material should be silent after stop");
    }

    [Fact]
    public async Task SilentMaterialLeavesMonitorSilent()
    {
        var send = new CollectingSink();
        var monitor = new CollectingSink();
        var source = new InfiniteMaterialSource(0f);
        var completion = new TaskCompletionSource<MaterialCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new MixEngine(new ConstantMicSource(0.2f), send, monitor, _log);
        engine.MaterialCompletion += c => completion.TrySetResult(c);
        engine.Start();
        engine.StartMaterial(1, source, TimeSpan.Zero);
        await Task.Delay(120);
        engine.StopMaterial(1);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(Signal.Peak(monitor.Snapshot()) < 0.001);
        Assert.True(Signal.Rms(send.Snapshot()) > 0.1);
    }

    [Fact]
    public async Task NaturalEndOfFiniteSourceCompletes()
    {
        var send = new CollectingSink();
        var monitor = new CollectingSink();
        var source = new FiniteMaterialSource(0.4f, AudioConstants.SampleRate / 2);
        var completion = new TaskCompletionSource<MaterialCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new MixEngine(new ConstantMicSource(0f), send, monitor, _log);
        engine.MaterialCompletion += c => completion.TrySetResult(c);
        engine.Start();
        engine.StartMaterial(7, source, TimeSpan.FromSeconds(3));
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MaterialStopReason.EndOfStream, result.Reason);
        Assert.Equal(7, result.Generation);
        Assert.True(monitor.FrameCount >= AudioConstants.SampleRate / 2 - 1000);
    }

    private static int FindFirstAbove(float[] samples, float threshold)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            if (Math.Abs(samples[i]) > threshold)
            {
                return i;
            }
        }

        return -1;
    }
}

internal sealed class InfiniteMaterialSource : IMaterialSource
{
    private readonly float _value;

    public InfiniteMaterialSource(float value)
    {
        _value = value;
    }

    public int BufferedFrames => AudioConstants.SampleRate;
    public bool EndOfStream => false;
    public bool HasFailed => false;
    public string? FailureMessage => null;

    public int Read(Span<float> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = _value;
        }

        return destination.Length / AudioConstants.Channels;
    }
}

internal sealed class FiniteMaterialSource : IMaterialSource
{
    private readonly float _value;
    private int _framesLeft;

    public FiniteMaterialSource(float value, int frames)
    {
        _value = value;
        _framesLeft = frames;
    }

    public int BufferedFrames => _framesLeft;
    public bool EndOfStream => _framesLeft <= 0;
    public bool HasFailed => false;
    public string? FailureMessage => null;

    public int Read(Span<float> destination)
    {
        var frames = Math.Min(destination.Length / 2, _framesLeft);
        for (var i = 0; i < frames * 2; i++)
        {
            destination[i] = _value;
        }

        _framesLeft -= frames;
        return frames;
    }
}
