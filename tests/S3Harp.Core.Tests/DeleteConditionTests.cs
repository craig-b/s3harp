using Xunit;

namespace S3Harp.Core.Tests;

public sealed class DeleteConditionTests
{
    private static readonly ObjectRecord Existing = new(
        "key",
        "blob",
        Size: 3,
        ETag: "etag-hex",
        Parts: [],
        Checksum: null,
        ContentType: null,
        ContentHeaders: ContentHeaders.None,
        Metadata: new Dictionary<string, string>(),
        LastModified: new DateTimeOffset(2026, 9, 16, 12, 0, 0, 450, TimeSpan.Zero)
    );

    [Fact]
    public void AnEmptyCondition_MatchesAnyObject()
    {
        Assert.True(new DeleteCondition().Matches(Existing));
    }

    [Theory]
    [InlineData("etag-hex", true)]
    [InlineData("other", false)]
    public void MatchesTheETagExactly(string etag, bool expected)
    {
        Assert.Equal(expected, new DeleteCondition(ETag: etag).Matches(Existing));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void MatchesTheSize(long size, bool expected)
    {
        Assert.Equal(expected, new DeleteCondition(Size: size).Matches(Existing));
    }

    [Fact]
    public void MatchesTheLastModifiedTimeToTheSecond()
    {
        var sameSecond = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var nextSecond = sameSecond.AddSeconds(1);

        Assert.True(new DeleteCondition(LastModified: sameSecond).Matches(Existing));
        Assert.False(new DeleteCondition(LastModified: nextSecond).Matches(Existing));
    }

    [Fact]
    public void EveryGivenTermMustMatch()
    {
        Assert.False(new DeleteCondition(ETag: "etag-hex", Size: 4).Matches(Existing));
    }
}
