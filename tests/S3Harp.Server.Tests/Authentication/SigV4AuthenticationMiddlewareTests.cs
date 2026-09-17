using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using S3Harp.Core;
using S3Harp.Server.Authentication;
using Xunit;

namespace S3Harp.Server.Tests.Authentication;

public sealed class SigV4AuthenticationMiddlewareTests
{
    private const string AccessKeyId = "S3HARPEXAMPLEKEY";
    private const string SecretAccessKey = "s3harp-example-secret";
    private const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Timestamp = Now.ToString(
        "yyyyMMdd'T'HHmmss'Z'",
        CultureInfo.InvariantCulture
    );

    [Fact]
    public async Task RequestWithoutAuthorization_IsRejectedAsAccessDenied()
    {
        var (context, nextCalled) = await RunMiddleware(CreateContext());

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("AccessDenied", ReadErrorCode(context));
    }

    [Fact]
    public async Task RequestWithMalformedAuthorization_IsRejectedAsMalformed()
    {
        var context = CreateContext();
        context.Request.Headers.Authorization = "AWS4-HMAC-SHA256 nonsense";

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("AuthorizationHeaderMalformed", ReadErrorCode(context));
    }

    [Fact]
    public async Task RequestWithUnknownAccessKey_IsRejectedAsInvalidAccessKeyId()
    {
        var context = CreateSignedContext(accessKeyId: "UNKNOWNACCESSKEY", SecretAccessKey);

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("InvalidAccessKeyId", ReadErrorCode(context));
    }

    [Theory]
    [InlineData("R")]
    [InlineData("ddd, dd MMM yyyy HH:mm:ss '-0000'")]
    public async Task RequestDatedByTheDateHeaderAlone_IsAccepted(string dateFormat)
    {
        // SigV4 takes the request time from Date when x-amz-date is absent, as
        // botocore does whenever a caller supplies a Date header; botocore itself
        // writes the RFC 2822 numeric zone rather than GMT.
        var context = CreateSignedContext(
            AccessKeyId,
            SecretAccessKey,
            dateHeaderFormat: dateFormat
        );

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task RequestSignedFarFromServerTime_IsRejectedAsTimeTooSkewed()
    {
        var context = CreateSignedContext(AccessKeyId, SecretAccessKey, signedAt: Now.AddHours(-2));

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("RequestTimeTooSkewed", ReadErrorCode(context));
    }

    [Fact]
    public async Task RequestSignedWithWrongSecret_IsRejectedAsSignatureDoesNotMatch()
    {
        var context = CreateSignedContext(AccessKeyId, "a-different-secret");

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("SignatureDoesNotMatch", ReadErrorCode(context));
    }

    [Fact]
    public async Task CorrectlySignedRequest_ReachesTheNextMiddleware()
    {
        var context = CreateSignedContext(AccessKeyId, SecretAccessKey);

        (_, var nextCalled) = await RunMiddleware(context);

        Assert.True(nextCalled());
    }

    [Fact]
    public async Task StreamingPayload_ReadsBackAsTheDecodedVerifiedContent()
    {
        var context = CreateSignedContext(
            AccessKeyId,
            SecretAccessKey,
            payloadHash: "STREAMING-AWS4-HMAC-SHA256-PAYLOAD"
        );
        context.Request.Body = new MemoryStream(
            BuildChunkedWire(SecretAccessKey, HeaderSignature(context), ["Hello, ", "S3Harp!"])
        );

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.True(nextCalled());
        using var decoded = new MemoryStream();
        await context.Request.Body.CopyToAsync(decoded, TestContext.Current.CancellationToken);
        Assert.Equal("Hello, S3Harp!", Encoding.UTF8.GetString(decoded.ToArray()));
    }

    [Fact]
    public async Task MatchingContentSha_DeliversTheBodyUnchanged()
    {
        var body = Encoding.UTF8.GetBytes("Hello, S3Harp!");
        var context = CreateSignedContext(
            AccessKeyId,
            SecretAccessKey,
            payloadHash: SigV4Signer.Sha256Hex(body)
        );
        context.Request.Body = new MemoryStream(body);

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.True(nextCalled());
        using var delivered = new MemoryStream();
        await context.Request.Body.CopyToAsync(delivered, TestContext.Current.CancellationToken);
        Assert.Equal(body, delivered.ToArray());
    }

    [Fact]
    public async Task MismatchedContentSha_FailsWhenTheBodyIsConsumed()
    {
        var context = CreateSignedContext(
            AccessKeyId,
            SecretAccessKey,
            payloadHash: SigV4Signer.Sha256Hex(Encoding.UTF8.GetBytes("declared content"))
        );
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("actual content"));

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.True(nextCalled());
        using var sink = new MemoryStream();
        await Assert.ThrowsAsync<PayloadVerificationException>(() =>
            context.Request.Body.CopyToAsync(sink, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task ChecksumValueMatchingTheBody_DeliversTheBody()
    {
        var body = Encoding.UTF8.GetBytes("Hello, S3Harp!");
        var context = CreateSignedContext(AccessKeyId, SecretAccessKey);
        context.Request.Headers["x-amz-checksum-sha256"] =
            "Aj0Lx1vWnbGF+irlCT3Pa4HNGctHtn3/Q49ApNekoy8=";
        context.Request.Body = new MemoryStream(body);

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.True(nextCalled());
        using var delivered = new MemoryStream();
        await context.Request.Body.CopyToAsync(delivered, TestContext.Current.CancellationToken);
        Assert.Equal(body, delivered.ToArray());
    }

    [Fact]
    public async Task ChecksumValueDifferingFromTheBody_FailsAsBadDigestWhenConsumed()
    {
        var context = CreateSignedContext(AccessKeyId, SecretAccessKey);
        context.Request.Headers["x-amz-checksum-crc32"] = "AAAAAA==";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("Hello, S3Harp!"));

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.True(nextCalled());
        using var sink = new MemoryStream();
        var exception = await Assert.ThrowsAsync<PayloadVerificationException>(() =>
            context.Request.Body.CopyToAsync(sink, TestContext.Current.CancellationToken)
        );
        Assert.Equal(S3Errors.BadDigest, exception.Error);
    }

    [Fact]
    public async Task OnCompleteMultipartUpload_TheChecksumHeaderIsNotHeldAgainstTheBody()
    {
        var context = CreateSignedContext(
            AccessKeyId,
            SecretAccessKey,
            shape: context =>
            {
                context.Request.Method = "POST";
                context.Request.QueryString = new QueryString("?uploadId=abc");
                context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget =
                    "/demo?uploadId=abc";
            }
        );
        context.Request.Headers["x-amz-checksum-sha256"] =
            "sDGBh5Sl/cL+/VEtpYWyKkP3wHD+lmz/q9Wq8TQpY8c=-2";
        context.Request.Body = new MemoryStream(
            Encoding.UTF8.GetBytes("<CompleteMultipartUpload/>")
        );

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.True(nextCalled());
        using var sink = new MemoryStream();
        await context.Request.Body.CopyToAsync(sink, TestContext.Current.CancellationToken);
        Assert.Equal("<CompleteMultipartUpload/>", Encoding.UTF8.GetString(sink.ToArray()));
    }

    [Theory]
    [InlineData("NadAdg==", true)]
    [InlineData("AAAAAA==", false)]
    public async Task StreamingTrailerChecksum_IsVerifiedAgainstTheDecodedPayload(
        string declaredCrc32,
        bool matches
    )
    {
        var context = CreateSignedContext(
            AccessKeyId,
            SecretAccessKey,
            payloadHash: "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER"
        );
        context.Request.Headers["x-amz-trailer"] = "x-amz-checksum-crc32";
        context.Request.Body = new MemoryStream(
            BuildChunkedWire(
                SecretAccessKey,
                HeaderSignature(context),
                ["Hello, ", "S3Harp!"],
                trailer: ("x-amz-checksum-crc32", declaredCrc32)
            )
        );

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.True(nextCalled());
        using var decoded = new MemoryStream();
        var copy = context.Request.Body.CopyToAsync(decoded, TestContext.Current.CancellationToken);
        if (matches)
        {
            await copy;
            Assert.Equal("Hello, S3Harp!", Encoding.UTF8.GetString(decoded.ToArray()));
        }
        else
        {
            var exception = await Assert.ThrowsAsync<PayloadVerificationException>(() => copy);
            Assert.Equal(S3Errors.BadDigest, exception.Error);
        }
    }

    [Fact]
    public async Task ValidPresignedRequest_ReachesTheNextMiddleware()
    {
        var context = CreatePresignedContext(AccessKeyId, SecretAccessKey);

        (_, var nextCalled) = await RunMiddleware(context);

        Assert.True(nextCalled());
    }

    [Fact]
    public async Task ExpiredPresignedRequest_IsRejectedAsAccessDenied()
    {
        var context = CreatePresignedContext(
            AccessKeyId,
            SecretAccessKey,
            signedAt: Now.AddMinutes(-10),
            expires: "60"
        );

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("AccessDenied", ReadErrorCode(context));
    }

    [Fact]
    public async Task PresignedRequestWithAWrongSecret_IsRejectedAsSignatureDoesNotMatch()
    {
        var context = CreatePresignedContext(AccessKeyId, "a-different-secret");

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.False(nextCalled());
        Assert.Equal("SignatureDoesNotMatch", ReadErrorCode(context));
    }

    [Fact]
    public async Task PresignedRequestWithAnUnknownAccessKey_IsRejected()
    {
        var context = CreatePresignedContext("UNKNOWNACCESSKEY", SecretAccessKey);

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.False(nextCalled());
        Assert.Equal("InvalidAccessKeyId", ReadErrorCode(context));
    }

    [Theory]
    [InlineData("+300")]
    [InlineData(" 300")]
    public async Task PresignedRequestWithALooselyFormattedExpires_IsRejected(string expires)
    {
        var context = CreatePresignedContext(AccessKeyId, SecretAccessKey, expires: expires);

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("AuthorizationQueryParametersError", ReadErrorCode(context));
    }

    [Fact]
    public async Task PresignedRequestWithAnInvalidExpires_IsRejected()
    {
        var context = CreatePresignedContext(AccessKeyId, SecretAccessKey, expires: "0");

        (context, var nextCalled) = await RunMiddleware(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("AuthorizationQueryParametersError", ReadErrorCode(context));
    }

    private static DefaultHttpContext CreatePresignedContext(
        string accessKeyId,
        string secretAccessKey,
        DateTimeOffset? signedAt = null,
        string expires = "300"
    )
    {
        var context = CreateContext();
        var timestamp = (signedAt ?? Now).ToString(
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture
        );
        var scope = new CredentialScope(timestamp[..8], "us-east-1", "s3");

        var canonicalQuery =
            "X-Amz-Algorithm=AWS4-HMAC-SHA256"
            + $"&X-Amz-Credential={Uri.EscapeDataString($"{accessKeyId}/{scope}")}"
            + $"&X-Amz-Date={timestamp}"
            + $"&X-Amz-Expires={expires}"
            + "&X-Amz-SignedHeaders=host";
        var canonicalRequest =
            $"GET\n/demo\n{canonicalQuery}\nhost:localhost\n\nhost\nUNSIGNED-PAYLOAD";
        var signature = SigV4Signer.SignCanonicalRequest(
            SigV4Signer.DeriveSigningKey(secretAccessKey, scope),
            scope,
            timestamp,
            canonicalRequest
        );

        var fullQuery = $"{canonicalQuery}&X-Amz-Signature={signature}";
        context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget = $"/demo?{fullQuery}";
        context.Request.QueryString = new QueryString($"?{fullQuery}");
        return context;
    }

    private static string HeaderSignature(DefaultHttpContext context)
    {
        var parsed = SigV4AuthorizationHeader.TryParse(
            context.Request.Headers.Authorization,
            out var header
        );
        Assert.True(parsed);
        return header!.Signature;
    }

    private static byte[] BuildChunkedWire(
        string secretAccessKey,
        string seedSignature,
        string[] chunks,
        (string Name, string Value)? trailer = null
    )
    {
        var scope = new CredentialScope(Timestamp[..8], "us-east-1", "s3");
        var signingKey = SigV4Signer.DeriveSigningKey(secretAccessKey, scope);
        var emptyHash = SigV4Signer.Sha256Hex([]);
        var wire = new StringBuilder();
        var previous = seedSignature;
        foreach (var chunk in chunks.Append(string.Empty))
        {
            var data = Encoding.UTF8.GetBytes(chunk);
            var stringToSign = string.Join(
                '\n',
                "AWS4-HMAC-SHA256-PAYLOAD",
                Timestamp,
                scope.ToString(),
                previous,
                emptyHash,
                SigV4Signer.Sha256Hex(data)
            );
            previous = SigV4Signer.Sign(signingKey, stringToSign);
            wire.Append(
                CultureInfo.InvariantCulture,
                $"{data.Length:x};chunk-signature={previous}\r\n"
            );
            if (data.Length > 0 || trailer is null)
            {
                wire.Append(chunk).Append("\r\n");
            }
        }

        if (trailer is { } line)
        {
            var canonicalTrailer = $"{line.Name}:{line.Value}\n";
            var trailerSignature = SigV4Signer.Sign(
                signingKey,
                string.Join(
                    '\n',
                    "AWS4-HMAC-SHA256-TRAILER",
                    Timestamp,
                    scope.ToString(),
                    previous,
                    SigV4Signer.Sha256Hex(Encoding.UTF8.GetBytes(canonicalTrailer))
                )
            );
            wire.Append(CultureInfo.InvariantCulture, $"{line.Name}:{line.Value}\r\n")
                .Append(
                    CultureInfo.InvariantCulture,
                    $"x-amz-trailer-signature:{trailerSignature}\r\n"
                )
                .Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(wire.ToString());
    }

    private static async Task<(DefaultHttpContext Context, Func<bool> NextCalled)> RunMiddleware(
        DefaultHttpContext context
    )
    {
        var called = false;
        var middleware = new SigV4AuthenticationMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            new RootCredentialStore(new RootCredentials(AccessKeyId, SecretAccessKey)),
            new FixedTimeProvider(Now)
        );

        await middleware.InvokeAsync(context);
        return (context, () => called);
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget = "/demo";
        context.Request.Headers.Host = "localhost";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext CreateSignedContext(
        string accessKeyId,
        string secretAccessKey,
        DateTimeOffset? signedAt = null,
        string payloadHash = UnsignedPayload,
        string? dateHeaderFormat = null,
        Action<DefaultHttpContext>? shape = null
    )
    {
        var context = CreateContext();
        shape?.Invoke(context);
        var timestamp = (signedAt ?? Now).ToString(
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture
        );
        var scope = new CredentialScope(timestamp[..8], "us-east-1", "s3");
        var dateHeader = dateHeaderFormat is null ? "x-amz-date" : "date";
        context.Request.Headers[dateHeader] = dateHeaderFormat is null
            ? timestamp
            : (signedAt ?? Now).ToString(dateHeaderFormat, CultureInfo.InvariantCulture);
        context.Request.Headers["x-amz-content-sha256"] = payloadHash;

        string[] signedHeaders = [dateHeader, "host", "x-amz-content-sha256"];
        var canonical = CanonicalRequest.Build(context.Request, signedHeaders, payloadHash);
        var signature = SigV4Signer.SignCanonicalRequest(
            SigV4Signer.DeriveSigningKey(secretAccessKey, scope),
            scope,
            timestamp,
            canonical
        );

        context.Request.Headers.Authorization =
            $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{scope}, "
            + $"SignedHeaders={string.Join(';', signedHeaders)}, Signature={signature}";
        return context;
    }

    private static string? ReadErrorCode(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return XDocument.Load(context.Response.Body).Root?.Element("Code")?.Value;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
