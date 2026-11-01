using ZKeyAudioLink.Core.Hotkeys;

namespace ZKeyAudioLink.Tests;

public class HotkeyStateTests
{
    private const long KeyboardA = 1;
    private const long KeyboardB = 2;

    [Fact]
    public void DefaultKeyIsZ()
    {
        Assert.Equal("Z", HotkeyKeys.Default.Name);
        Assert.Equal("Z", HotkeyKeys.GetOrDefault(null).Name);
        Assert.Equal("Z", HotkeyKeys.GetOrDefault("not-a-key").Name);
    }

    [Fact]
    public void HoldingSelectedKeyTriggersOnlyOnce()
    {
        var state = new HotkeyState();
        var key = HotkeyKeys.GetOrDefault("X");
        state.SetKey(key);

        Assert.True(state.Handle(KeyboardA, key.VirtualKey, key.ScanCode, 0, HotkeyState.WmKeyDown));
        Assert.False(state.Handle(KeyboardA, key.VirtualKey, key.ScanCode, 0, HotkeyState.WmKeyDown));
        Assert.False(state.Handle(KeyboardA, key.VirtualKey, key.ScanCode, 0, HotkeyState.WmKeyDown));

        Assert.False(state.Handle(KeyboardA, key.VirtualKey, key.ScanCode, 0, HotkeyState.WmKeyUp));
        Assert.True(state.Handle(KeyboardA, key.VirtualKey, key.ScanCode, 0, HotkeyState.WmKeyDown));
    }

    [Fact]
    public void SecondKeyboardDoesNotTriggerWhileSelectedKeyIsHeld()
    {
        var state = new HotkeyState();
        var key = HotkeyKeys.GetOrDefault("X");
        state.SetKey(key);

        Assert.True(state.Handle(KeyboardA, key.VirtualKey, key.ScanCode, 0, HotkeyState.WmKeyDown));
        Assert.False(state.Handle(KeyboardB, key.VirtualKey, key.ScanCode, 0, HotkeyState.WmKeyDown));
        Assert.False(state.Handle(KeyboardA, key.VirtualKey, key.ScanCode, 0, HotkeyState.WmKeyUp));
        Assert.False(state.Handle(KeyboardB, key.VirtualKey, key.ScanCode, 0, HotkeyState.WmKeyUp));

        Assert.True(state.Handle(KeyboardB, key.VirtualKey, key.ScanCode, 0, HotkeyState.WmKeyDown));
    }

    [Fact]
    public void OtherKeysAreIgnored()
    {
        var state = new HotkeyState();
        state.SetKey(HotkeyKeys.GetOrDefault("X"));

        var y = HotkeyKeys.GetOrDefault("Y");
        Assert.False(state.Handle(KeyboardA, y.VirtualKey, y.ScanCode, 0, HotkeyState.WmKeyDown));
    }

    [Fact]
    public void ImeProcessKeyUsesSelectedScanCode()
    {
        var state = new HotkeyState();
        var key = HotkeyKeys.GetOrDefault("X");
        state.SetKey(key);

        Assert.True(state.Handle(KeyboardA, 0xE5, key.ScanCode, 0, HotkeyState.WmKeyDown));
    }

    [Fact]
    public void ExtendedScanCodeDoesNotMatchLetterScanFallback()
    {
        var state = new HotkeyState();
        var key = HotkeyKeys.GetOrDefault("Z");
        state.SetKey(key);

        Assert.False(state.Handle(KeyboardA, 0xE5, key.ScanCode, HotkeyState.RiKeyE0, HotkeyState.WmKeyDown));
    }

    [Fact]
    public void FunctionKeyUsesVirtualKey()
    {
        var state = new HotkeyState();
        var f8 = HotkeyKeys.GetOrDefault("F8");
        state.SetKey(f8);

        Assert.True(state.Handle(KeyboardA, f8.VirtualKey, f8.ScanCode, 0, HotkeyState.WmKeyDown));
        Assert.False(state.Handle(KeyboardA, HotkeyKeys.Default.VirtualKey, HotkeyKeys.Default.ScanCode, 0, HotkeyState.WmKeyDown));
    }

    [Fact]
    public void ChangingKeyResetsHeldState()
    {
        var state = new HotkeyState();
        var z = HotkeyKeys.GetOrDefault("Z");
        var x = HotkeyKeys.GetOrDefault("X");
        state.SetKey(z);
        Assert.True(state.Handle(KeyboardA, z.VirtualKey, z.ScanCode, 0, HotkeyState.WmKeyDown));

        state.SetKey(x);
        Assert.True(state.Handle(KeyboardA, x.VirtualKey, x.ScanCode, 0, HotkeyState.WmKeyDown));
    }

    [Fact]
    public void OldKeyAutoRepeatDoesNotTriggerNewKey()
    {
        var state = new HotkeyState();
        var z = HotkeyKeys.GetOrDefault("Z");
        var x = HotkeyKeys.GetOrDefault("X");
        state.SetKey(z);
        Assert.True(state.Handle(KeyboardA, z.VirtualKey, z.ScanCode, 0, HotkeyState.WmKeyDown));

        state.SetKey(x);
        Assert.False(state.Handle(KeyboardA, z.VirtualKey, z.ScanCode, 0, HotkeyState.WmKeyDown));
        Assert.True(state.Handle(KeyboardA, x.VirtualKey, x.ScanCode, 0, HotkeyState.WmKeyDown));
    }

    [Fact]
    public void SettingTheSameKeyDoesNotResetHeldState()
    {
        var state = new HotkeyState();
        var z = HotkeyKeys.GetOrDefault("Z");
        state.SetKey(z);
        Assert.True(state.Handle(KeyboardA, z.VirtualKey, z.ScanCode, 0, HotkeyState.WmKeyDown));

        state.SetKey(z);
        Assert.False(state.Handle(KeyboardA, z.VirtualKey, z.ScanCode, 0, HotkeyState.WmKeyDown));
    }

    [Fact]
    public void NavigationKeyDoesNotUseScanCodeFallback()
    {
        var state = new HotkeyState();
        var home = HotkeyKeys.GetOrDefault("Home");
        state.SetKey(home);

        Assert.False(home.UseScanCodeFallback);
        Assert.False(state.Handle(KeyboardA, 0x67, home.ScanCode, 0, HotkeyState.WmKeyDown)); // numpad 7 VKey, shared scan
        Assert.True(state.Handle(KeyboardA, home.VirtualKey, home.ScanCode, 0, HotkeyState.WmKeyDown));
    }

    [Fact]
    public void ResetReleasesHeldKey()
    {
        var state = new HotkeyState();
        var z = HotkeyKeys.GetOrDefault("Z");
        state.SetKey(z);
        Assert.True(state.Handle(KeyboardA, z.VirtualKey, z.ScanCode, 0, HotkeyState.WmKeyDown));
        state.Reset();
        Assert.True(state.Handle(KeyboardA, z.VirtualKey, z.ScanCode, 0, HotkeyState.WmKeyDown));
    }
}

public class HotkeyKeysTests
{
    [Fact]
    public void CatalogContainsCommonKeysAndStableNames()
    {
        Assert.Contains(HotkeyKeys.All, k => k.Name == "Z" && k.DisplayName == "Z");
        Assert.Contains(HotkeyKeys.All, k => k.Name == "F8");
        Assert.Contains(HotkeyKeys.All, k => k.Name == "Space" && k.DisplayName == "空格");
        Assert.False(HotkeyKeys.GetOrDefault("Home").UseScanCodeFallback);
        Assert.True(HotkeyKeys.All.Count >= 60);
    }

    [Fact]
    public void LookupIsCaseInsensitive()
    {
        Assert.Equal("F8", HotkeyKeys.GetOrDefault("f8").Name);
        Assert.True(HotkeyKeys.TryGet("x", out var x));
        Assert.Equal("X", x.Name);
        Assert.False(HotkeyKeys.TryGet("bogus", out var fallback));
        Assert.Equal("Z", fallback.Name);
    }
}
