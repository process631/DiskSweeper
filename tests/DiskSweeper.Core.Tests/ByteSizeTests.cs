using System.Globalization;

namespace DiskSweeper.Core.Tests;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5.0 MB")]
    [InlineData(537_700_000_000L, "500.8 GB")]
    [InlineData(-2048, "-2.0 KB")]
    public void Formats_like_File_Explorer(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Deltas_always_show_a_sign()
    {
        Assert.Equal("+1.0 GB", ByteSize.FormatDelta(1L << 30, CultureInfo.InvariantCulture));
        Assert.Equal("-512 B", ByteSize.FormatDelta(-512, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("500", 500)]
    [InlineData("1 KB", 1024)]
    [InlineData("1.5gb", 1610612736)]
    [InlineData(" 2 M ", 2097152)]
    public void Parses_sizes_people_type(string text, long expected)
    {
        Assert.True(ByteSize.TryParse(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("5 XB")]
    public void Rejects_nonsense(string text)
    {
        Assert.False(ByteSize.TryParse(text, out _));
    }
}
