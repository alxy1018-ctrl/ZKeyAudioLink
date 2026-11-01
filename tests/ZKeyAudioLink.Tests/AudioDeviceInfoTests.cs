using NAudio.CoreAudioApi;
using ZKeyAudioLink.Core.Audio;

namespace ZKeyAudioLink.Tests;

public class AudioDeviceInfoTests
{
    [Fact]
    public void StandardCableInputIsVirtualCable()
    {
        var info = new AudioDeviceInfo("{id}", "CABLE Input (VB-Audio Virtual Cable)", DataFlow.Render, DeviceState.Active);

        Assert.True(info.IsVirtualCable);
        Assert.False(info.IsCableSixteenChannel);
    }

    [Fact]
    public void SixteenChannelCableEndpointIsNotTheStandardVirtualCable()
    {
        var info = new AudioDeviceInfo("{id}", "CABLE In 16ch (VB-Audio Virtual Cable)", DataFlow.Render, DeviceState.Active);

        Assert.False(info.IsVirtualCable);
        Assert.True(info.IsCableSixteenChannel);
    }

    [Fact]
    public void CableOutputIsCaptureSide()
    {
        var info = new AudioDeviceInfo("{id}", "CABLE Output (VB-Audio Virtual Cable)", DataFlow.Capture, DeviceState.Active);

        Assert.True(info.IsCableOutput);
        Assert.False(info.IsVirtualCable);
    }
}
