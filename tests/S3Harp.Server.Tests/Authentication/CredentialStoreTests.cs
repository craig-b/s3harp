using S3Harp.Server.Authentication;
using Xunit;

namespace S3Harp.Server.Tests.Authentication;

public sealed class CredentialStoreTests
{
    private static readonly CredentialStore Store = new(
        new Dictionary<string, string>
        {
            ["S3HARPROOTKEY"] = "root-secret",
            ["S3HARPSECONDKEY"] = "second-secret",
        }
    );

    [Theory]
    [InlineData("S3HARPROOTKEY", "root-secret")]
    [InlineData("S3HARPSECONDKEY", "second-secret")]
    public void FindsTheSecretOfEveryConfiguredKey(string accessKeyId, string secret) =>
        Assert.Equal(secret, Store.FindSecretKey(accessKeyId));

    [Theory]
    [InlineData("S3HARPUNKNOWNKEY")]
    [InlineData("s3harprootkey")]
    public void FindsNothingForAnUnknownOrDifferentlyCasedKey(string accessKeyId) =>
        Assert.Null(Store.FindSecretKey(accessKeyId));
}
