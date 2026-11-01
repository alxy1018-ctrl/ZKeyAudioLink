using ZKeyAudioLink.Core.Configuration;
using ZKeyAudioLink.Core.Media;

namespace ZKeyAudioLink.Tests;

public class MediaLibraryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SettingsStore _store;
    private readonly MediaLibrary _library;

    public MediaLibraryTests()
    {
        _store = new SettingsStore(_temp.File("settings.json"), _temp.File("settings.bak"));
        _library = new MediaLibrary(_store, mediaDirectory: _temp.File("media"));
    }

    public void Dispose()
    {
        _library.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task ImportsCopyIntoLibraryAndPersists()
    {
        var source = WavFactory.WriteSilenceThenTone(_temp.File("source.wav"), 0.5, 1.5);

        var result = await _library.ImportAsync(source);

        Assert.Equal(ImportStatus.Imported, result.Status);
        Assert.NotNull(result.Entry);
        Assert.Single(_library.Entries);
        Assert.True(File.Exists(result.Entry!.GetLibraryPath(_temp.File("media"))));
        Assert.InRange(result.Entry.Duration.TotalSeconds, 1.9, 2.1);

        var reloaded = new SettingsStore(_temp.File("settings.json"), _temp.File("settings.bak")).Load();
        Assert.Single(reloaded.Media);
        Assert.Equal(result.Entry.Id, reloaded.Media[0].Id);
    }

    [Fact]
    public async Task DuplicateImportIsReportedAndNotAddedTwice()
    {
        var source = WavFactory.WriteSilenceThenTone(_temp.File("source.wav"), 0.2, 0.3);
        await _library.ImportAsync(source);

        var duplicate = await _library.ImportAsync(source);

        Assert.Equal(ImportStatus.Duplicate, duplicate.Status);
        Assert.Single(_library.Entries);
    }

    [Fact]
    public async Task RejectsUnsupportedEmptyAndCorruptFiles()
    {
        File.WriteAllText(_temp.File("notes.txt"), "hello");
        File.WriteAllBytes(_temp.File("empty.wav"), Array.Empty<byte>());
        File.WriteAllText(_temp.File("corrupt.wav"), "not a wave");

        Assert.Equal(ImportStatus.Failed, (await _library.ImportAsync(_temp.File("notes.txt"))).Status);
        Assert.Equal(ImportStatus.Failed, (await _library.ImportAsync(_temp.File("empty.wav"))).Status);
        Assert.Equal(ImportStatus.Failed, (await _library.ImportAsync(_temp.File("corrupt.wav"))).Status);
        Assert.Empty(_library.Entries);
    }

    [Fact]
    public async Task HandlesChineseAndSpacePaths()
    {
        var source = WavFactory.WriteSilenceThenTone(_temp.File("中文 空格 测试.wav"), 0.2, 0.4);
        var result = await _library.ImportAsync(source);

        Assert.Equal(ImportStatus.Imported, result.Status);
        Assert.Equal("中文 空格 测试", result.Entry!.DisplayName);
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheLibraryCopy()
    {
        var source = WavFactory.WriteSilenceThenTone(_temp.File("original.wav"), 0.2, 0.4);
        var result = await _library.ImportAsync(source);
        var copy = result.Entry!.GetLibraryPath(_temp.File("media"));

        Assert.True(_library.Remove(result.Entry.Id, out var error), error);
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(source));
        Assert.Empty(_library.Entries);
    }

    [Fact]
    public async Task StartOffsetValidationAndPersistence()
    {
        var source = WavFactory.WriteSilenceThenTone(_temp.File("source.wav"), 0.2, 1.0);
        var entry = (await _library.ImportAsync(source)).Entry!;

        Assert.False(_library.TrySetStartOffset(entry.Id, TimeSpan.FromSeconds(-1), out _));
        Assert.False(_library.TrySetStartOffset(entry.Id, entry.Duration, out _));
        Assert.False(_library.TrySetStartOffset(entry.Id, entry.Duration + TimeSpan.FromSeconds(1), out _));
        Assert.True(_library.TrySetStartOffset(entry.Id, TimeSpan.FromSeconds(0.5), out _));
        Assert.Equal(TimeSpan.FromSeconds(0.5), entry.StartOffset);

        var reloaded = new SettingsStore(_temp.File("settings.json"), _temp.File("settings.bak")).Load();
        Assert.Equal(TimeSpan.FromSeconds(0.5), reloaded.Media[0].StartOffset);
    }

    [Fact]
    public async Task MissingFilesAreDetected()
    {
        var source = WavFactory.WriteSilenceThenTone(_temp.File("source.wav"), 0.1, 0.2);
        var entry = (await _library.ImportAsync(source)).Entry!;
        File.Delete(entry.GetLibraryPath(_temp.File("media")));

        Assert.Single(_library.FindMissingFiles());
    }
}
