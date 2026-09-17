using Xunit;

namespace S3Harp.Server.Tests;

public sealed class RangeHeaderTests
{
    [Theory]
    [InlineData("bytes=0-4", 11, 0, 4)]
    [InlineData("bytes=6-10", 11, 6, 10)]
    [InlineData("bytes=6-999", 11, 6, 10)]
    [InlineData("bytes=6-", 11, 6, 10)]
    [InlineData("bytes=-5", 11, 6, 10)]
    [InlineData("bytes=-999", 11, 0, 10)]
    [InlineData("bytes=0-0", 11, 0, 0)]
    public void Evaluate_ResolvesSatisfiableRanges(
        string header,
        long size,
        long expectedFrom,
        long expectedTo
    )
    {
        var evaluation = RangeHeader.Evaluate(header, size);

        Assert.Equal(RangeOutcome.Partial, evaluation.Outcome);
        Assert.Equal(expectedFrom, evaluation.From);
        Assert.Equal(expectedTo, evaluation.To);
    }

    [Theory]
    [InlineData(null, 11)]
    [InlineData("", 11)]
    [InlineData("bytes=0-4,6-8", 11)]
    [InlineData("bytes=abc-def", 11)]
    [InlineData("bytes=5-2", 11)]
    [InlineData("items=0-4", 11)]
    [InlineData("bytes=", 11)]
    public void Evaluate_ServesTheWholeObjectForAbsentOrIgnorableHeaders(string? header, long size)
    {
        Assert.Equal(RangeOutcome.WholeObject, RangeHeader.Evaluate(header, size).Outcome);
    }

    [Theory]
    [InlineData("bytes=11-", 11)]
    [InlineData("bytes=11-20", 11)]
    [InlineData("bytes=-0", 11)]
    [InlineData("bytes=0-", 0)]
    [InlineData("bytes=-5", 0)]
    public void Evaluate_ReportsUnsatisfiableRanges(string header, long size)
    {
        Assert.Equal(RangeOutcome.Unsatisfiable, RangeHeader.Evaluate(header, size).Outcome);
    }
}
