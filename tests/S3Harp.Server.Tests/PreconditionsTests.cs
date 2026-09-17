using Xunit;

namespace S3Harp.Server.Tests;

public sealed class PreconditionsTests
{
    private const string ETag = "5eb63bbbe01eeed093cb22bb8f5acdc3";
    private static readonly DateTimeOffset LastModified = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoHeaders_Proceed()
    {
        Assert.Equal(PreconditionOutcome.Proceed, Evaluate(new ConditionalHeaders()));
    }

    [Theory]
    [InlineData("\"5eb63bbbe01eeed093cb22bb8f5acdc3\"")]
    [InlineData("5eb63bbbe01eeed093cb22bb8f5acdc3")]
    [InlineData("\"other\", \"5eb63bbbe01eeed093cb22bb8f5acdc3\"")]
    [InlineData("*")]
    public void IfMatch_MatchingTheETag_Proceeds(string header)
    {
        Assert.Equal(
            PreconditionOutcome.Proceed,
            Evaluate(new ConditionalHeaders(IfMatch: header))
        );
    }

    [Fact]
    public void IfMatch_MissingTheETag_FailsThePrecondition()
    {
        Assert.Equal(
            PreconditionOutcome.PreconditionFailed,
            Evaluate(new ConditionalHeaders(IfMatch: "\"ABCORZ\""))
        );
    }

    [Theory]
    [InlineData("\"5eb63bbbe01eeed093cb22bb8f5acdc3\"")]
    [InlineData("\"other\", \"5eb63bbbe01eeed093cb22bb8f5acdc3\"")]
    [InlineData("*")]
    public void IfNoneMatch_MatchingTheETag_IsNotModified(string header)
    {
        Assert.Equal(
            PreconditionOutcome.NotModified,
            Evaluate(new ConditionalHeaders(IfNoneMatch: header))
        );
    }

    [Fact]
    public void IfNoneMatch_MissingTheETag_Proceeds()
    {
        Assert.Equal(
            PreconditionOutcome.Proceed,
            Evaluate(new ConditionalHeaders(IfNoneMatch: "\"ABCORZ\""))
        );
    }

    [Theory]
    [InlineData("Wed, 16 Sep 2026 12:00:00 GMT", PreconditionOutcome.NotModified)]
    [InlineData("Wed, 16 Sep 2026 12:00:01 GMT", PreconditionOutcome.NotModified)]
    [InlineData("Wed, 16 Sep 2026 11:59:59 GMT", PreconditionOutcome.Proceed)]
    [InlineData("not a date", PreconditionOutcome.Proceed)]
    public void IfModifiedSince_ComparesAtSecondPrecision(
        string header,
        PreconditionOutcome expected
    )
    {
        Assert.Equal(expected, Evaluate(new ConditionalHeaders(IfModifiedSince: header)));
    }

    [Theory]
    [InlineData("Wed, 16 Sep 2026 12:00:00 GMT", PreconditionOutcome.Proceed)]
    [InlineData("Wed, 16 Sep 2026 12:00:01 GMT", PreconditionOutcome.Proceed)]
    [InlineData("Sat, 29 Oct 1994 19:43:31 GMT", PreconditionOutcome.PreconditionFailed)]
    [InlineData("not a date", PreconditionOutcome.Proceed)]
    public void IfUnmodifiedSince_ComparesAtSecondPrecision(
        string header,
        PreconditionOutcome expected
    )
    {
        Assert.Equal(expected, Evaluate(new ConditionalHeaders(IfUnmodifiedSince: header)));
    }

    [Fact]
    public void IfMatch_OutranksIfUnmodifiedSince()
    {
        var headers = new ConditionalHeaders(
            IfMatch: $"\"{ETag}\"",
            IfUnmodifiedSince: "Sat, 29 Oct 1994 19:43:31 GMT"
        );

        Assert.Equal(PreconditionOutcome.Proceed, Evaluate(headers));
    }

    [Fact]
    public void IfNoneMatch_OutranksIfModifiedSince()
    {
        var headers = new ConditionalHeaders(
            IfNoneMatch: $"\"{ETag}\"",
            IfModifiedSince: "Sat, 29 Oct 1994 19:43:31 GMT"
        );

        Assert.Equal(PreconditionOutcome.NotModified, Evaluate(headers));
    }

    [Fact]
    public void FailedPrecondition_OutranksNotModified()
    {
        var headers = new ConditionalHeaders(IfMatch: "\"ABCORZ\"", IfNoneMatch: $"\"{ETag}\"");

        Assert.Equal(PreconditionOutcome.PreconditionFailed, Evaluate(headers));
    }

    private static PreconditionOutcome Evaluate(ConditionalHeaders headers) =>
        Preconditions.Evaluate(headers, ETag, LastModified);
}
