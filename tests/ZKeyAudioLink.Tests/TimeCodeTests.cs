using ZKeyAudioLink.Core.Media;

namespace ZKeyAudioLink.Tests;

public class TimeCodeTests
{
    [Theory]
    [InlineData("00:10.000", 10.0)]
    [InlineData("1:02.500", 62.5)]
    [InlineData("00:00.001", 0.001)]
    [InlineData("90", 90.0)]
    [InlineData("1:00:00.250", 3600.25)]
    [InlineData("0:05", 5.0)]
    [InlineData("00:60.000", 60.0)]
    public void ParsesValidValues(string text, double expectedSeconds)
    {
        Assert.True(TimeCode.TryParse(text, out var value));
        Assert.Equal(expectedSeconds, value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-00:01.000")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("1:2:3:4")]
    [InlineData("00:12.3.4")]
    public void RejectsInvalidValues(string text)
    {
        Assert.False(TimeCode.TryParse(text, out _));
    }

    [Fact]
    public void FormatsMinutesAndHours()
    {
        Assert.Equal("00:10.000", TimeCode.Format(TimeSpan.FromSeconds(10)));
        Assert.Equal("01:02.500", TimeCode.Format(TimeSpan.FromSeconds(62.5)));
        Assert.Equal("1:00:00.250", TimeCode.Format(TimeSpan.FromSeconds(3600.25)));
    }
}
