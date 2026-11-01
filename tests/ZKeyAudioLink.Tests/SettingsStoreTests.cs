using ZKeyAudioLink.Core.Configuration;
using ZKeyAudioLink.Core.Media;

namespace ZKeyAudioLink.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void RoundTripsSettings()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        var store = new SettingsStore(path, path + ".bak");
        store.Settings.MicrophoneGain = 1.25f;
        store.Settings.HotkeyEnabled = false;
        store.Settings.HotkeyKey = "F8";
        store.Settings.Media.Add(new MediaEntry
        {
            DisplayName = "测试",
            LibraryFileName = "abc.wav",
            Duration = TimeSpan.FromSeconds(30),
            StartOffset = TimeSpan.FromSeconds(5)
        });
        Assert.True(store.Save());

        var loaded = new SettingsStore(path, path + ".bak").Load();
        Assert.Equal(1.25f, loaded.MicrophoneGain);
        Assert.False(loaded.HotkeyEnabled);
        Assert.Equal("F8", loaded.HotkeyKey);
        Assert.Single(loaded.Media);
        Assert.Equal(TimeSpan.FromSeconds(5), loaded.Media[0].StartOffset);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void RestoresBackupWhenPrimaryIsCorrupt()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        var store = new SettingsStore(path, path + ".bak");
        store.Settings.MonitorGain = 0.42f;
        Assert.True(store.Save());
        store.Settings.MonitorGain = 0.66f;
        Assert.True(store.Save()); // creates the .bak with the 0.42 value

        File.WriteAllText(path, "{ not json");

        var loadedStore = new SettingsStore(path, path + ".bak");
        var loaded = loadedStore.Load();
        Assert.Equal(0.42f, loaded.MonitorGain);
        Assert.NotNull(loadedStore.LoadWarning);
        Assert.NotEmpty(Directory.GetFiles(temp.Path, "settings.json.corrupt-*"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void UsesDefaultsWhenEverythingIsCorrupt()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, "garbage");
        var store = new SettingsStore(path, path + ".bak");
        var loaded = store.Load();
        Assert.Empty(loaded.Media);
        Assert.NotNull(store.LoadWarning);
    }

    [Fact]
    public void MissingHotkeyDefaultsToZForOldConfig()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, "{\"version\":1,\"media\":[]}");
        var loaded = new SettingsStore(path, path + ".bak").Load();
        Assert.Equal("Z", loaded.HotkeyKey);
    }

    [Fact]
    public void NormalizesInvalidEntries()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        var store = new SettingsStore(path, path + ".bak");
        store.Settings.Media.Add(new MediaEntry
        {
            Id = Guid.Empty,
            Duration = TimeSpan.FromSeconds(10),
            StartOffset = TimeSpan.FromSeconds(20),
            LibraryFileName = null!
        });
        store.Settings.SelectedMediaId = Guid.NewGuid();
        store.Settings.MicrophoneGain = 99f;
        store.Settings.HotkeyKey = "NotAKey";
        Assert.True(store.Save());

        var loaded = new SettingsStore(path, path + ".bak").Load();
        Assert.Single(loaded.Media);
        Assert.NotEqual(Guid.Empty, loaded.Media[0].Id);
        Assert.Equal(TimeSpan.Zero, loaded.Media[0].StartOffset);
        Assert.Equal(string.Empty, loaded.Media[0].LibraryFileName);
        Assert.Null(loaded.SelectedMediaId);
        Assert.Equal(4f, loaded.MicrophoneGain);
        Assert.Equal("Z", loaded.HotkeyKey);
    }
}
