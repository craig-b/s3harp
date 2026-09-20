using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Xunit;

namespace S3Harp.IntegrationTests;

public sealed class AuthenticationTests : IAsyncLifetime
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

    [Fact]
    public async Task RequestSignedWithASecondConfiguredKeypair_IsAccepted()
    {
        using var s3 = factory.CreateS3Client(
            accessKeyId: S3HarpFactory.SecondAccessKeyId,
            secretAccessKey: S3HarpFactory.SecondSecretAccessKey
        );

        await s3.PutBucketAsync(
            new PutBucketRequest { BucketName = "second-key-bucket" },
            TestContext.Current.CancellationToken
        );

        var buckets = await s3.ListBucketsAsync(TestContext.Current.CancellationToken);
        Assert.Contains(buckets.Buckets, bucket => bucket.BucketName == "second-key-bucket");
    }

    [Fact]
    public async Task RequestSignedWithTheWrongSecretForASecondKeypair_IsRejected()
    {
        using var s3 = factory.CreateS3Client(
            accessKeyId: S3HarpFactory.SecondAccessKeyId,
            secretAccessKey: "wrong-secret-access-key"
        );

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            s3.PutBucketAsync(
                new PutBucketRequest { BucketName = "demo" },
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("SignatureDoesNotMatch", exception.ErrorCode);
    }

    public ValueTask InitializeAsync() => factory.StartAsync();

    public ValueTask DisposeAsync() => factory.DisposeAsync();
}
