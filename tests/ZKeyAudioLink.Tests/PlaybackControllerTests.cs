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
    public async Task ToggleStartsPlaybackAndStopsFromIdle()
    {
        var engine = new FakePlaybackEngine();
        var entry = NewEntry();
        using var controller = new PlaybackController(engine, () => entry, _log);

        await controller.ToggleAsync();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing);

        await controller.ToggleAsync();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Idle);
        Assert.Contains(controller.Generation, engine.StoppedGenerations);
    }

    [Fact]
    public async Task NoSelectionProducesNotice()
    {
        var engine = new FakePlaybackEngine();
        using var controller = new PlaybackController(engine, () => null, _log);
        var notices = new List<string>();
        controller.Notice += notices.Add;

        await controller.ToggleAsync();

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

        await controller.ToggleAsync();

        Assert.Single(errors);
        Assert.Equal(0, engine.PlayCount);
        Assert.Equal(PlaybackPhase.Idle, controller.Phase);
    }

    [Fact]
    public async Task ToggleWhilePreparingCancelsWithoutPlaying()
    {
        var engine = new FakePlaybackEngine();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PlayHandler = async (_, _, ct) => await gate.Task.WaitAsync(ct);
        using var controller = new PlaybackController(engine, () => NewEntry(), _log);

        await controller.ToggleAsync();
        Assert.Equal(PlaybackPhase.Preparing, controller.Phase);

        await controller.ToggleAsync();
        Assert.Equal(PlaybackPhase.Idle, controller.Phase);

        gate.SetResult();
        await Task.Delay(100);
        Assert.Equal(PlaybackPhase.Idle, controller.Phase);
        Assert.Contains(controller.Generation, engine.StoppedGenerations);
    }

    [Fact]
    public async Task RapidTogglesDoNotStartACancelledGeneration()
    {
        var engine = new FakePlaybackEngine();
        var gate1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PlayHandler = (_, generation, ct) =>
            generation == 1 ? gate1.Task.WaitAsync(ct) : gate2.Task.WaitAsync(ct);
        using var controller = new PlaybackController(engine, () => NewEntry(), _log);

        await controller.ToggleAsync();  // gen 1 preparing
        await controller.ToggleAsync();  // cancel gen 1
        await controller.ToggleAsync();  // gen 2 preparing
        Assert.Equal(PlaybackPhase.Preparing, controller.Phase);

        gate2.SetResult();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing);
        Assert.Equal(2, controller.Generation);

        var errors = new List<string>();
        controller.Error += errors.Add;
        engine.Complete(1, MaterialStopReason.Failed, "stale");
        await Task.Delay(100);

        Assert.Equal(PlaybackPhase.Playing, controller.Phase);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task EndOfStreamCompletionReturnsToIdle()
    {
        var engine = new FakePlaybackEngine();
        using var controller = new PlaybackController(engine, () => NewEntry(), _log);

        await controller.ToggleAsync();
        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Playing);
        engine.Complete(controller.Generation, MaterialStopReason.EndOfStream);

        await WaitUntilAsync(() => controller.Phase == PlaybackPhase.Idle);
        Assert.Equal(PlaybackPhase.Idle, controller.Phase);
    }

    [Fact]
    public async Task PrepareFailureReportsErrorAndReturnsToIdle()
    {
        var engine = new FakePlaybackEngine
        {
            PlayHandler = (_, _, _) => throw new EngineNotReadyException("设备不可用")
        };
        using var controller = new PlaybackController(engine, () => NewEntry(), _log);
        var errors = new List<string>();
        controller.Error += errors.Add;

        await controller.ToggleAsync();
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

    public Func<MediaEntry, int, CancellationToken, Task>? PlayHandler { get; set; }
    public int PlayCount { get; private set; }
    public List<int> StoppedGenerations { get; } = new();

    public Task PlayMaterialAsync(MediaEntry entry, int generation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            PlayCount++;
        }

        return PlayHandler is null ? Task.CompletedTask : PlayHandler(entry, generation, cancellationToken);
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
