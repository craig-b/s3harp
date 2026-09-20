using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using S3Harp.Core;
using S3Harp.Server.Authentication;
using Xunit;

namespace S3Harp.IntegrationTests;

/// <summary>
/// The server behind its own certificate. The .NET SDK keeps signing its chunked
/// uploads over HTTPS; boto3 and clients like it switch to the unsigned chunked
/// form with a checksum trailer, which one test here sends by hand.
/// </summary>
public sealed class TlsTests : IAsyncLifetime
{
    private const string Bucket = "secure-bucket";

    private readonly S3HarpFactory factory = new();

    [Fact]
    public void ListensOverHttps() => Assert.Equal("https", factory.BaseAddress.Scheme);

    [Fact]
    public async Task UploadsAndDownloadsAnObject()
    {
        using var s3 = await CreateClientWithBucket();
        var payload = RandomNumberGenerator.GetBytes(256 * 1024);

        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = Bucket,
                Key = "secret.bin",
                InputStream = new MemoryStream(payload),
            },
            Token
        );

        using var stored = await s3.GetObjectAsync(Bucket, "secret.bin", Token);
        Assert.Equal(payload, await ReadAllAsync(stored));
    }

    [Fact]
    public async Task CompletesAMultipartUpload()
    {
        using var s3 = await CreateClientWithBucket();
        var firstPart = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        var secondPart = RandomNumberGenerator.GetBytes(64 * 1024);

        var initiate = await s3.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest { BucketName = Bucket, Key = "assembled.bin" },
            Token
        );
        var uploaded = new List<PartETag>();
        foreach (var (bytes, number) in new[] { (firstPart, 1), (secondPart, 2) })
        {
            var part = await s3.UploadPartAsync(
                new UploadPartRequest
                {
                    BucketName = Bucket,
                    Key = "assembled.bin",
                    UploadId = initiate.UploadId,
                    PartNumber = number,
                    InputStream = new MemoryStream(bytes),
                },
                Token
            );
            uploaded.Add(new PartETag(number, part.ETag));
        }

        await s3.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = "assembled.bin",
                UploadId = initiate.UploadId,
                PartETags = uploaded,
            },
            Token
        );

        using var stored = await s3.GetObjectAsync(Bucket, "assembled.bin", Token);
        Assert.Equal(firstPart.Concat(secondPart), await ReadAllAsync(stored));
    }

    [Fact]
    public async Task AcceptsTheUnsignedChunkedUploadClientsSendOverTls()
    {
        using var s3 = await CreateClientWithBucket();
        var payload = Encoding.UTF8.GetBytes("hello from an unsigned chunked body");
        using var crc32 = ChecksumAlgorithms.Create(S3Harp.Core.ChecksumAlgorithm.Crc32);
        crc32.Append(payload);
        var checksum = Convert.ToBase64String(crc32.Finish());
        var wire = Encoding.UTF8.GetBytes(
            $"{payload.Length:x}\r\n{Encoding.UTF8.GetString(payload)}\r\n0\r\n"
                + $"x-amz-checksum-crc32:{checksum}\r\n\r\n"
        );

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri(factory.BaseAddress, $"/{Bucket}/unsigned.txt")
        )
        {
            Content = new ByteArrayContent(wire),
        };
        request.Content.Headers.ContentEncoding.Add("aws-chunked");
        SignWithUnsignedStreamingPayload(request, payload.Length);
        using var http = factory.CreateHttpClient();
        var response = await http.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stored = await s3.GetObjectAsync(Bucket, "unsigned.txt", Token);
        Assert.Equal(payload, await ReadAllAsync(stored));
    }

    [Fact]
    public async Task ServesAPresignedUrl()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = Bucket,
                Key = "shared.txt",
                ContentBody = "hello over tls",
            },
            Token
        );
        var url = await s3.GetPreSignedURLAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = Bucket,
                Key = "shared.txt",
                Verb = HttpVerb.GET,
                Protocol = Protocol.HTTPS,
                Expires = DateTime.UtcNow.AddMinutes(5),
            }
        );

        using var http = factory.CreateHttpClient();
        var response = await http.GetAsync(new Uri(url), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello over tls", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task ServesVirtualHostedAddressing()
    {
        using var s3 = await CreateClientWithBucket();
        using var virtualHosted = factory.CreateS3Client(virtualHosted: true);

        await virtualHosted.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = Bucket,
                Key = "hosted.txt",
                ContentBody = "hello virtual host",
            },
            Token
        );

        using var stored = await s3.GetObjectAsync(Bucket, "hosted.txt", Token);
        using var reader = new StreamReader(stored.ResponseStream);
        Assert.Equal("hello virtual host", await reader.ReadToEndAsync(Token));
    }

    public ValueTask InitializeAsync() => factory.StartAsync(tls: true);

    public ValueTask DisposeAsync() => factory.DisposeAsync();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Signs the request the way boto3 does over TLS: the headers carry the SigV4
    /// signature while the payload hash names the unsigned streaming form.
    /// </summary>
    private void SignWithUnsignedStreamingPayload(HttpRequestMessage request, int decodedLength)
    {
        const string payloadHash = "STREAMING-UNSIGNED-PAYLOAD-TRAILER";
        var timestamp = DateTimeOffset.UtcNow.ToString(
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture
        );
        var scope = new CredentialScope(timestamp[..8], "us-east-1", "s3");
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["content-encoding"] = "aws-chunked",
            ["host"] = factory.BaseAddress.Authority,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = timestamp,
            ["x-amz-decoded-content-length"] = decodedLength.ToString(CultureInfo.InvariantCulture),
            ["x-amz-trailer"] = "x-amz-checksum-crc32",
        };
        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalRequest =
            $"PUT\n{request.RequestUri?.AbsolutePath}\n\n"
            + string.Concat(headers.Select(header => $"{header.Key}:{header.Value}\n"))
            + $"\n{signedHeaders}\n{payloadHash}";
        var signature = SigV4Signer.SignCanonicalRequest(
            SigV4Signer.DeriveSigningKey(S3HarpFactory.SecretAccessKey, scope),
            scope,
            timestamp,
            canonicalRequest
        );

        foreach (var (name, value) in headers.Where(header => header.Key is not "host"))
        {
            if (name is not "content-encoding")
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"AWS4-HMAC-SHA256 Credential={S3HarpFactory.AccessKeyId}/{scope}, "
                + $"SignedHeaders={signedHeaders}, Signature={signature}"
        );
    }

    private static async Task<byte[]> ReadAllAsync(GetObjectResponse stored)
    {
        using var buffer = new MemoryStream();
        await stored.ResponseStream.CopyToAsync(buffer, Token);
        return buffer.ToArray();
    }

    private async Task<AmazonS3Client> CreateClientWithBucket()
    {
        var s3 = factory.CreateS3Client();
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, Token);
        return s3;
    }
}
