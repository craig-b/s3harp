using System.Net;
using Amazon.S3;
using Xunit;

namespace S3Harp.IntegrationTests;

public sealed class ErrorResponseTests : IDisposable
{
    private readonly S3HarpFactory factory = new();

    [Fact]
    public async Task UnimplementedOperation_SurfacesThroughTheAwsSdkAsNotImplemented()
    {
        using var s3 = factory.CreateS3Client();

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            s3.GetBucketVersioningAsync("demo", TestContext.Current.CancellationToken)
        );

        Assert.Equal("NotImplemented", exception.ErrorCode);
        Assert.Equal(HttpStatusCode.NotImplemented, exception.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(exception.RequestId));
    }

    public void Dispose() => factory.Dispose();
}
