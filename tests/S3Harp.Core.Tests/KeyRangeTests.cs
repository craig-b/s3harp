using Xunit;

namespace S3Harp.Core.Tests;

public sealed class KeyRangeTests
{
    [Theory]
    [InlineData("abc", "abd")]
    [InlineData("docs/", "docs0")]
    [InlineData("a￿", "b")]
    public void PrefixSuccessor_ReturnsTheSmallestStringAboveEveryPrefixedKey(
        string prefix,
        string expected
    ) => Assert.Equal(expected, KeyRange.PrefixSuccessor(prefix));

    [Theory]
    [InlineData("")]
    [InlineData("￿")]
    [InlineData("￿￿")]
    public void PrefixSuccessor_ReportsUnboundedPrefixes(string prefix) =>
        Assert.Null(KeyRange.PrefixSuccessor(prefix));
}
