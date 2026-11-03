using System.Diagnostics;
using ZKeyAudioLink.Core.Audio;
using ZKeyAudioLink.Core.Diagnostics;
using ZKeyAudioLink.Core.Media;
using ZKeyAudioLink.Core.Playback;

namespace ZKeyAudioLink.Tests;

public class PlaybackControllerTests : IDisposable
{
    private readonly AppLog _log;
    private readonly TempDirectory _temp = new();

    public PlaybackControllerTests()
    {
        _log = new AppLog(_temp.File("logs"));
    }

    public void Dispose()
    {
        _log.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task IdlePlayUsesCustomStartOffset()
    {
        var engine = new FakePlaybackEngine();
        var entry = NewEntry();
        using var controller = new PlaybackController(engine, () => entry, _log);

        await controller.PlayOrReplayAsync();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing);

        Assert.Equal(1, engine.PlayCount);
        Assert.Equal(TimeSpan.FromSeconds(5), engine.Offsets[0]);
        Assert.Equal(PlaybackPhase.Playing, controller.Phase);
    }

    [Fact]
    public async Task PlayingAgainReplaysFromZeroAndStopsPreviousGeneration()
    {
        var engine = new FakePlaybackEngine();
        var entry = NewEntry();
        using var controller = new PlaybackController(engine, () => entry, _log);

        await controller.PlayOrReplayAsync();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing);
        var firstGeneration = controller.Generation;

        await controller.PlayOrReplayAsync();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing && engine.PlayCount == 2);

        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.Zero }, engine.Offsets);
        Assert.Equal(firstGeneration, engine.StoppedGenerations[0]);
        Assert.Equal(2, controller.Generation);
        Assert.Equal(PlaybackPhase.Playing, controller.Phase);
    }

    [Fact]
    public async Task PlayWhilePreparingRestartsFromZero()
    {
        var engine = new FakePlaybackEngine();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PlayHandler = (_, _, _, ct) => gate.Task.WaitAsync(ct);
        using var controller = new PlaybackController(engine, () => NewEntry(), _log);

        await controller.PlayOrReplayAsync();
        Assert.Equal(PlaybackPhase.Preparing, controller.Phase);

        await controller.PlayOrReplayAsync();
        Assert.Equal(PlaybackPhase.Preparing, controller.Phase);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.Zero }, engine.Offsets);

        gate.SetResult();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing);
    }

    [Fact]
    public async Task StopIsIdempotentWhenPlaying()
    {
        var engine = new FakePlaybackEngine();
        using var controller = new PlaybackController(engine, () => NewEntry(), _log);

        await controller.PlayOrReplayAsync();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing);
        var generation = controller.Generation;

        await controller.StopAsync();
        Assert.Equal(PlaybackPhase.Idle, controller.Phase);
        Assert.Contains(generation, engine.StoppedGenerations);

        var stops = engine.StoppedGenerations.Count;
        await controller.StopAsync();
        Assert.Equal(stops, engine.StoppedGenerations.Count);
        Assert.Equal(PlaybackPhase.Idle, controller.Phase);
    }

    [Fact]
    public async Task StopWhilePreparingCancelsWithoutPlaying()
    {
        var engine = new FakePlaybackEngine();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PlayHandler = (_, _, _, ct) => gate.Task.WaitAsync(ct);
        using var controller = new PlaybackController(engine, () => NewEntry(), _log);

        await controller.PlayOrReplayAsync();
        Assert.Equal(PlaybackPhase.Preparing, controller.Phase);

        await controller.StopAsync();
        Assert.Equal(PlaybackPhase.Idle, controller.Phase);

        gate.SetResult();
        await Task.Delay(100);
        Assert.Equal(PlaybackPhase.Idle, controller.Phase);
    }

    [Fact]
    public async Task StopWhenIdleIsSafe()
    {
        var engine = new FakePlaybackEngine();
        using var controller = new PlaybackController(engine, () => null, _log);

        await controller.StopAsync();
        Assert.Equal(PlaybackPhase.Idle, controller.Phase);
        Assert.Empty(engine.StoppedGenerations);
        Assert.Equal(0, engine.PlayCount);
    }

    [Fact]
    public async Task NoSelectionProducesNotice()
    {
        var engine = new FakePlaybackEngine();
        using var controller = new PlaybackController(engine, () => null, _log);
        var notices = new List<string>();
        controller.Notice += notices.Add;

        await controller.PlayOrReplayAsync();

        Assert.Single(notices);
        Assert.Equal(PlaybackPhase.Idle, controller.Phase);
        Assert.Equal(0, engine.PlayCount);
    }

    [Fact]
    public async Task InvalidStartOffsetIsRejected()
    {
        var engine = new FakePlaybackEngine();
        var entry = NewEntry();
        entry.StartOffset = entry.Duration;
        using var controller = new PlaybackController(engine, () => entry, _log);
        var errors = new List<string>();
        controller.Error += errors.Add;

        await controller.PlayOrReplayAsync();

        Assert.Single(errors);
        Assert.Equal(0, engine.PlayCount);
        Assert.Equal(PlaybackPhase.Idle, controller.Phase);
    }

    [Fact]
    public async Task PlayAfterStopUsesCustomStartOffsetAgain()
    {
        var engine = new FakePlaybackEngine();
        using var controller = new PlaybackController(engine, () => NewEntry(), _log);

        await controller.PlayOrReplayAsync();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing);
        await controller.StopAsync();
        Assert.Equal(PlaybackPhase.Idle, controller.Phase);

        await controller.PlayOrReplayAsync();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing && engine.PlayCount == 2);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5) }, engine.Offsets);
    }

    [Fact]
    public async Task ReplayIgnoresOldGenerationCompletion()
    {
        var engine = new FakePlaybackEngine();
        using var controller = new PlaybackController(engine, () => NewEntry(), _log);

        await controller.PlayOrReplayAsync();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing);
        var oldGeneration = controller.Generation;

        await controller.PlayOrReplayAsync();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing && engine.PlayCount == 2);
        engine.Complete(oldGeneration, MaterialStopReason.UserStopped);
        await Task.Delay(100);

        Assert.Equal(PlaybackPhase.Playing, controller.Phase);
        Assert.Equal(oldGeneration + 1, controller.Generation);
    }

    [Fact]
    public async Task RapidReplaysDoNotStartCancelledGenerations()
    {
        var engine = new FakePlaybackEngine();
        var gate1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate3 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PlayHandler = (_, generation, _, ct) => generation switch
        {
            1 => gate1.Task.WaitAsync(ct),
            2 => gate2.Task.WaitAsync(ct),
            _ => gate3.Task.WaitAsync(ct)
        };
        using var controller = new PlaybackController(engine, () => NewEntry(), _log);

        await controller.PlayOrReplayAsync(); // gen 1 preparing
        await controller.PlayOrReplayAsync(); // gen 2 preparing, gen 1 stopped
        await controller.PlayOrReplayAsync(); // gen 3 preparing, gen 2 stopped
        Assert.Equal(PlaybackPhase.Preparing, controller.Phase);
        Assert.Equal(3, engine.Offsets.Count);

        gate1.TrySetResult();
        gate2.TrySetResult();
        gate3.SetResult();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing);
        Assert.Equal(3, controller.Generation);
    }

    [Fact]
    public async Task EndOfStreamCompletionReturnsToIdle()
    {
        var engine = new FakePlaybackEngine();
        using var controller = new PlaybackController(engine, () => NewEntry(), _log);

        await controller.PlayOrReplayAsync();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing);
        engine.Complete(controller.Generation, MaterialStopReason.EndOfStream);

        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Idle);
    }

    [Fact]
    public async Task PrepareFailureReportsErrorAndReturnsToIdle()
    {
        var engine = new FakePlaybackEngine
        {
            PlayHandler = (_, _, _, _) => throw new EngineNotReadyException("设备不可用")
        };
        using var controller = new PlaybackController(engine, () => NewEntry(), _log);
        var errors = new List<string>();
        controller.Error += errors.Add;

        await controller.PlayOrReplayAsync();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Idle);

        Assert.Contains("设备不可用", errors);
    }

    private static MediaEntry NewEntry() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "test",
        Duration = TimeSpan.FromSeconds(30),
        StartOffset = TimeSpan.FromSeconds(5),
        LibraryFileName = "test.wav"
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }
}

internal sealed class FakePlaybackEngine : IPlaybackEngine
{
    private readonly object _gate = new();

    public event Action<MaterialCompletion>? MaterialCompleted;

    public Func<MediaEntry, int, TimeSpan, CancellationToken, Task>? PlayHandler { get; set; }
    public int PlayCount { get; private set; }
    public List<TimeSpan> Offsets { get; } = new();
    public List<int> StoppedGenerations { get; } = new();

    public Task PlayMaterialAsync(MediaEntry entry, int generation, TimeSpan offset, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            PlayCount++;
            Offsets.Add(offset);
        }

        return PlayHandler is null ? Task.CompletedTask : PlayHandler(entry, generation, offset, cancellationToken);
    }

    public void StopMaterial(int generation)
    {
        lock (_gate)
        {
            StoppedGenerations.Add(generation);
        }
    }

    public void Complete(int generation, MaterialStopReason reason, string? message = null) =>
        MaterialCompleted?.Invoke(new MaterialCompletion(generation, reason, message));
}
