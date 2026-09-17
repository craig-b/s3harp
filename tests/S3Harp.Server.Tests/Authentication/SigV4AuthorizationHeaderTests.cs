using S3Harp.Server.Authentication;
using Xunit;

namespace S3Harp.Server.Tests.Authentication;

public sealed class SigV4AuthorizationHeaderTests
{
    private const string ValidHeader =
        "AWS4-HMAC-SHA256 "
        + "Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, "
        + "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, "
        + "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41";

    [Fact]
    public void TryParse_ReadsAllComponentsOfAValidHeader()
    {
        var parsed = SigV4AuthorizationHeader.TryParse(ValidHeader, out var header);

        Assert.True(parsed);
        Assert.NotNull(header);
        Assert.Equal("AKIAIOSFODNN7EXAMPLE", header.AccessKeyId);
        Assert.Equal(new CredentialScope("20130524", "us-east-1", "s3"), header.Scope);
        Assert.Equal(["host", "range", "x-amz-content-sha256", "x-amz-date"], header.SignedHeaders);
        Assert.Equal(
            "f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            header.Signature
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer some-token")]
    [InlineData("AWS4-HMAC-SHA256")]
    [InlineData("AWS4-HMAC-SHA256 Credential=only/20130524/us-east-1/s3/aws4_request")]
    [InlineData("AWS4-HMAC-SHA256 SignedHeaders=host, Signature=abc")]
    [InlineData(
        "AWS4-HMAC-SHA256 Credential=akid/20130524/us-east-1/s3, SignedHeaders=host, Signature=abc"
    )]
    [InlineData(
        "AWS4-HMAC-SHA256 Credential=akid/20130524/us-east-1/s3/other, SignedHeaders=host, Signature=abc"
    )]
    [InlineData(
        "AWS4-HMAC-SHA256 Credential=akid/20130524/us-east-1/s3/aws4_request, Unknown=x, SignedHeaders=host, Signature=abc"
    )]
    public void TryParse_RejectsHeadersMissingRequiredShape(string? value)
    {
        Assert.False(SigV4AuthorizationHeader.TryParse(value, out _));
    }
}
