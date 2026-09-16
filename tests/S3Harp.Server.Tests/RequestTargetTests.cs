using Xunit;

namespace S3Harp.Server.Tests;

public sealed class RequestTargetTests
{
    private const string Domain = "localhost";

    [Theory]
    [InlineData("/", "", null)]
    [InlineData("/my-bucket", "my-bucket", null)]
    [InlineData("/my-bucket/", "my-bucket", null)]
    [InlineData("/my-bucket/photos/cat.jpg", "my-bucket", "photos/cat.jpg")]
    [InlineData("/my-bucket/folder/", "my-bucket", "folder/")]
    public void ABareHostNamesTheBucketInThePath(string path, string bucket, string? key)
    {
        Assert.Equal((bucket, key), RequestTarget.Resolve("localhost", path, Domain));
    }

    [Theory]
    [InlineData("my-bucket.localhost", "/", "my-bucket", null)]
    [InlineData("my-bucket.localhost", "/photos/cat.jpg", "my-bucket", "photos/cat.jpg")]
    [InlineData("my-bucket.localhost", "/folder/", "my-bucket", "folder/")]
    [InlineData("my.dotted.bucket.localhost", "/key", "my.dotted.bucket", "key")]
    [InlineData("My-Bucket.LOCALHOST", "/key", "My-Bucket", "key")]
    public void AHostUnderTheDomainNamesTheBucket(string host, string path, string bucket, string? key)
    {
        Assert.Equal((bucket, key), RequestTarget.Resolve(host, path, Domain));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("s3.example.test")]
    [InlineData("notlocalhost")]
    public void AHostOutsideTheDomainLeavesTheBucketInThePath(string host)
    {
        Assert.Equal(("my-bucket", "key"), RequestTarget.Resolve(host, "/my-bucket/key", Domain));
    }
}
