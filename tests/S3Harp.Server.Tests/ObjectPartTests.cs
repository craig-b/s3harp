using S3Harp.Core;
using Xunit;

namespace S3Harp.Server.Tests;

public sealed class ObjectPartTests
{
    [Theory]
    [InlineData(1, 0, 4)]
    [InlineData(2, 5, 7)]
    [InlineData(3, 8, 8)]
    public void SelectsTheByteRangeOfAPartOfAMultipartObject(int partNumber, long from, long to)
    {
        var selected = ObjectPart.Select(partNumber, Record(partSizes: [5, 3, 1]));

        Assert.Equal(new RangeEvaluation(RangeOutcome.Partial, from, to), selected);
    }

    [Fact]
    public void ReportsNoSuchPartBeyondTheLastPart()
    {
        Assert.Null(ObjectPart.Select(4, Record(partSizes: [5, 3, 1])));
    }

    [Fact]
    public void PartOneOfAnObjectStoredInOnePiece_IsTheWholeObject()
    {
        var selected = ObjectPart.Select(1, Record(partSizes: []));

        Assert.Equal(new RangeEvaluation(RangeOutcome.WholeObject, 0, 0), selected);
    }

    [Fact]
    public void AnObjectStoredInOnePiece_HasNoFurtherParts()
    {
        Assert.Null(ObjectPart.Select(2, Record(partSizes: [])));
    }

    private static ObjectRecord Record(long[] partSizes) => new(
        "key", "blob", Size: partSizes.Sum(), ETag: "etag", partSizes, Checksum: null, ContentType: null,
        ContentHeaders.None, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch);
}
