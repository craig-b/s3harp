using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace S3Harp.IntegrationTests;

public sealed class ErrorResponseTests : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = new();

    public ErrorResponseTests()
    {
        // The AWS SDK is the compatibility oracle, so tests exercise real HTTP over Kestrel.
        factory.UseKestrel(port: 0);
    }

    [Fact]
    public async Task UnimplementedOperation_SurfacesThroughTheAwsSdkAsNotImplemented()
    {
        using var s3 = CreateClient();

        var request = new PutBucketRequest { BucketName = "demo" };
        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(
            () => s3.PutBucketAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("NotImplemented", exception.ErrorCode);
        Assert.Equal(HttpStatusCode.NotImplemented, exception.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(exception.RequestId));
    }

    public void Dispose() => factory.Dispose();

    private AmazonS3Client CreateClient()
    {
        using var httpClient = factory.CreateClient();
        var config = new AmazonS3Config
        {
            ServiceURL = httpClient.BaseAddress!.ToString(),
            ForcePathStyle = true,
            MaxErrorRetry = 0,
        };
        return new AmazonS3Client(new BasicAWSCredentials("test-access-key", "test-secret-key"), config);
    }
}
