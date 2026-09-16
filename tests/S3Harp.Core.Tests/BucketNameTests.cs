using Xunit;

namespace S3Harp.Core.Tests;

public sealed class BucketNameTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("my-bucket")]
    [InlineData("my.bucket.2026")]
    [InlineData("0numeric0")]
    [InlineData("exactly-sixty-three-characters-long-name-abcdefghijklmnopqrstuv")]
    [InlineData("192.168.5.123.local")]
    [InlineData("a.b.c.d")]
    public void IsValid_AcceptsConformingNames(string name)
    {
        Assert.True(BucketName.IsValid(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("sixty-four-characters-long-bucket-name-abcdefghijklmnopqrstuvwxy")]
    [InlineData("UpperCase")]
    [InlineData("under_score")]
    [InlineData("-starts-with-hyphen")]
    [InlineData("ends-with-hyphen-")]
    [InlineData(".starts-with-dot")]
    [InlineData("ends-with-dot.")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("foo..bar")]
    [InlineData("192.168.5.123")]
    public void IsValid_RejectsNonConformingNames(string name)
    {
        Assert.False(BucketName.IsValid(name));
    }
}
