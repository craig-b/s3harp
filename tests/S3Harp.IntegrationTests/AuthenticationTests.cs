using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Xunit;

namespace S3Harp.IntegrationTests;

public sealed class AuthenticationTests : IDisposable
{
    private readonly S3HarpFactory factory = new();

    [Fact]
    public async Task RequestSignedWithWrongSecret_IsRejectedAsSignatureDoesNotMatch()
    {
        using var s3 = factory.CreateS3Client(secretAccessKey: "wrong-secret-access-key");

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            s3.PutBucketAsync(
                new PutBucketRequest { BucketName = "demo" },
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("SignatureDoesNotMatch", exception.ErrorCode);
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task RequestSignedWithUnknownAccessKey_IsRejectedAsInvalidAccessKeyId()
    {
        using var s3 = factory.CreateS3Client(accessKeyId: "UNKNOWNACCESSKEYID");

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            s3.PutBucketAsync(
                new PutBucketRequest { BucketName = "demo" },
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("InvalidAccessKeyId", exception.ErrorCode);
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    public void Dispose() => factory.Dispose();
}
