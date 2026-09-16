using S3Harp.Core;
using Xunit;

namespace S3Harp.Server.Tests;

public sealed class CopySourceRangeTests
{
    [Fact]
    public void ReadsAClosedByteRange()
    {
        Assert.True(CopySourceRange.TryParse("bytes=0-21", out var range));
        Assert.Equal(new ByteRange(0, 21), range);
    }

    [Fact]
    public void AnAbsentHeaderMeansTheWholeSource()
    {
        Assert.True(CopySourceRange.TryParse("", out var range));
        Assert.Null(range);
    }

    [Theory]
    [InlineData("0-2")]
    [InlineData("bytes=0")]
    [InlineData("bytes=hello-world")]
    [InlineData("bytes=0-bar")]
    [InlineData("bytes=hello-")]
    [InlineData("bytes=0-2,3-5")]
    [InlineData("bytes=-5")]
    [InlineData("bytes=5-")]
    [InlineData("bytes=5-2")]
    public void RejectsEveryOtherShape(string header)
    {
        Assert.False(CopySourceRange.TryParse(header, out _));
    }
}
