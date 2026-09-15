using S3Harp.Server.Authentication;
using Xunit;

namespace S3Harp.Server.Tests.Authentication;

/// <summary>
/// Verified against the worked S3 GET example in the AWS SigV4 documentation
/// ("Authenticating Requests: Using the Authorization Header").
/// </summary>
public sealed class SigV4SignerTests
{
    private const string SecretAccessKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static readonly CredentialScope Scope = new("20130524", "us-east-1", "s3");

    private const string CanonicalRequest =
        "GET\n" +
        "/test.txt\n" +
        "\n" +
        "host:examplebucket.s3.amazonaws.com\n" +
        "range:bytes=0-9\n" +
        $"x-amz-content-sha256:{EmptyPayloadHash}\n" +
        "x-amz-date:20130524T000000Z\n" +
        "\n" +
        "host;range;x-amz-content-sha256;x-amz-date\n" +
        EmptyPayloadHash;

    [Fact]
    public void SignCanonicalRequest_ReproducesTheDocumentedAwsSignature()
    {
        var signingKey = SigV4Signer.DeriveSigningKey(SecretAccessKey, Scope);

        var signature = SigV4Signer.SignCanonicalRequest(
            signingKey, Scope, "20130524T000000Z", CanonicalRequest);

        Assert.Equal("f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", signature);
    }

    [Fact]
    public void CredentialScope_FormatsAsTheSigV4ScopeString()
    {
        Assert.Equal("20130524/us-east-1/s3/aws4_request", Scope.ToString());
    }

    [Fact]
    public void Sha256Hex_HashesTheEmptyInputToTheWellKnownValue()
    {
        Assert.Equal(EmptyPayloadHash, SigV4Signer.Sha256Hex([]));
    }
}
