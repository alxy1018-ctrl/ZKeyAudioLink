using ZKeyAudioLink.Core.Hotkeys;

namespace ZKeyAudioLink.Tests;

public class ZKeyStateTests
{
    private const long KeyboardA = 1;
    private const long KeyboardB = 2;

    [Fact]
    public void HoldingZTriggersOnlyOnce()
    {
        var state = new ZKeyState();

        Assert.True(state.Handle(KeyboardA, ZKeyState.VkZ, 0x2C, 0, ZKeyState.WmKeyDown));
        Assert.False(state.Handle(KeyboardA, ZKeyState.VkZ, 0x2C, 0, ZKeyState.WmKeyDown));
        Assert.False(state.Handle(KeyboardA, ZKeyState.VkZ, 0x2C, 0, ZKeyState.WmKeyDown));

        Assert.False(state.Handle(KeyboardA, ZKeyState.VkZ, 0x2C, 0, ZKeyState.WmKeyUp));
        Assert.True(state.Handle(KeyboardA, ZKeyState.VkZ, 0x2C, 0, ZKeyState.WmKeyDown));
    }

    [Fact]
    public void SecondKeyboardDoesNotTriggerWhileZIsHeld()
    {
        var state = new ZKeyState();

        Assert.True(state.Handle(KeyboardA, ZKeyState.VkZ, 0x2C, 0, ZKeyState.WmKeyDown));
        Assert.False(state.Handle(KeyboardB, ZKeyState.VkZ, 0x2C, 0, ZKeyState.WmKeyDown));
        Assert.False(state.Handle(KeyboardA, ZKeyState.VkZ, 0x2C, 0, ZKeyState.WmKeyUp));
        Assert.False(state.Handle(KeyboardB, ZKeyState.VkZ, 0x2C, 0, ZKeyState.WmKeyUp));

        Assert.True(state.Handle(KeyboardB, ZKeyState.VkZ, 0x2C, 0, ZKeyState.WmKeyDown));
    }

    [Fact]
    public void NonZKeysAreIgnored()
    {
        var state = new ZKeyState();
        Assert.False(state.Handle(KeyboardA, 0x59, 0x15, 0, ZKeyState.WmKeyDown)); // Y
        Assert.False(state.Handle(KeyboardA, ZKeyState.VkZ, 0x2C, ZKeyState.RiKeyE0, ZKeyState.WmKeyDown));
    }

    [Fact]
    public void ImeProcessKeyWithZScanCodeStillTriggers()
    {
        var state = new ZKeyState();
        Assert.True(state.Handle(KeyboardA, 0xE5, ZKeyState.ZScanCode, 0, ZKeyState.WmKeyDown));
    }

    [Fact]
    public void ResetReleasesHeldKey()
    {
        var state = new ZKeyState();
        Assert.True(state.Handle(KeyboardA, ZKeyState.VkZ, 0x2C, 0, ZKeyState.WmKeyDown));
        state.Reset();
        Assert.True(state.Handle(KeyboardA, ZKeyState.VkZ, 0x2C, 0, ZKeyState.WmKeyDown));
    }
}
