using System.Globalization;
using S3Harp.Core;

namespace S3Harp.Server.Authentication;

/// <summary>Verifies the SigV4 signature on every request before it reaches an operation.</summary>
internal sealed class SigV4AuthenticationMiddleware(
    RequestDelegate next,
    ICredentialStore credentials,
    TimeProvider timeProvider
)
{
    private const string ContentSha256Header = "x-amz-content-sha256";
    private const string DateHeader = "x-amz-date";
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>HTTP's RFC 1123 date, plus the RFC 2822 numeric-zone form some clients write.</summary>
    private static readonly string[] HttpDateFormats = ["R", "ddd, dd MMM yyyy HH:mm:ss zzz"];
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(15);

    /// <exception cref="IOException">The response could not be written.</exception>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        string? authorization = request.Headers.Authorization;
        if (string.IsNullOrEmpty(authorization))
        {
            if (request.Query.ContainsKey("X-Amz-Algorithm"))
            {
                await AuthenticatePresignedAsync(context).ConfigureAwait(false);
                return;
            }

            await Reject(context, S3Errors.AccessDenied).ConfigureAwait(false);
            return;
        }

        if (!SigV4AuthorizationHeader.TryParse(authorization, out var header))
        {
            await Reject(context, S3Errors.AuthorizationHeaderMalformed).ConfigureAwait(false);
            return;
        }

        string? payloadHash = request.Headers[ContentSha256Header];
        if (string.IsNullOrEmpty(payloadHash))
        {
            await Reject(context, S3Errors.MissingContentSha256).ConfigureAwait(false);
            return;
        }

        if (!TryReadRequestTime(request.Headers, out var requestTime, out var timestamp))
        {
            await Reject(context, S3Errors.AccessDenied).ConfigureAwait(false);
            return;
        }

        if ((timeProvider.GetUtcNow() - requestTime).Duration() > MaxClockSkew)
        {
            await Reject(context, S3Errors.RequestTimeTooSkewed).ConfigureAwait(false);
            return;
        }

        if (
            !string.Equals(
                header.Scope.Date,
                requestTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                StringComparison.Ordinal
            )
        )
        {
            await Reject(context, S3Errors.SignatureDoesNotMatch).ConfigureAwait(false);
            return;
        }

        var secretAccessKey = credentials.FindSecretKey(header.AccessKeyId);
        if (secretAccessKey is null)
        {
            await Reject(context, S3Errors.InvalidAccessKeyId).ConfigureAwait(false);
            return;
        }

        var canonicalRequest = CanonicalRequest.Build(request, header.SignedHeaders, payloadHash);
        var signingKey = SigV4Signer.DeriveSigningKey(secretAccessKey, header.Scope);
        var expected = SigV4Signer.SignCanonicalRequest(
            signingKey,
            header.Scope,
            timestamp,
            canonicalRequest
        );
        if (!SigV4Signer.SignaturesEqual(expected, header.Signature))
        {
            await Reject(context, S3Errors.SignatureDoesNotMatch).ConfigureAwait(false);
            return;
        }

        var chunkSigning = new ChunkSigning(signingKey, header.Scope, timestamp, header.Signature);
        request.Body = payloadHash switch
        {
            "UNSIGNED-PAYLOAD" => request.Body,
            "STREAMING-AWS4-HMAC-SHA256-PAYLOAD" => AwsChunkedStream.Signed(
                request.Body,
                chunkSigning
            ),
            "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER" => AwsChunkedStream.Signed(
                request.Body,
                chunkSigning,
                signedTrailer: true,
                AnnouncedTrailerChecksum(request.Headers)
            ),
            "STREAMING-UNSIGNED-PAYLOAD-TRAILER" => AwsChunkedStream.Unsigned(
                request.Body,
                AnnouncedTrailerChecksum(request.Headers)
            ),
            _ => new Sha256VerifyingStream(request.Body, payloadHash),
        };

        // On CompleteMultipartUpload the checksum header names the object being
        // assembled, not the XML body, so the body is not held to it.
        if (
            !IsCompleteMultipartUpload(request)
            && ChecksumHeaders.TryFindDeclared(request.Headers, out var algorithm, out var declared)
        )
        {
            request.Body = new ChecksumVerifyingStream(
                request.Body,
                ChecksumAlgorithms.Create(algorithm),
                declared
            );
        }

        await next(context).ConfigureAwait(false);
    }

    private static bool IsCompleteMultipartUpload(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) && request.Query.ContainsKey("uploadId");

    /// <summary>The checksum algorithm <c>x-amz-trailer</c> announces, when it names one.</summary>
    private static ChecksumAlgorithm? AnnouncedTrailerChecksum(IHeaderDictionary headers)
    {
        string? trailer = headers["x-amz-trailer"];
        return
            trailer is not null
            && ChecksumHeaders.TryParseHeaderName(trailer.Trim(), out var algorithm)
            ? algorithm
            : null;
    }

    /// <summary>
    /// The request time SigV4 signs over: <c>x-amz-date</c> when present, else the
    /// standard <c>Date</c> header. The timestamp is the ISO-basic form the
    /// string to sign carries in either case.
    /// </summary>
    private static bool TryReadRequestTime(
        IHeaderDictionary headers,
        out DateTimeOffset requestTime,
        out string timestamp
    )
    {
        string? amzDate = headers[DateHeader];
        if (amzDate is not null)
        {
            timestamp = amzDate;
            return DateTimeOffset.TryParseExact(
                amzDate,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out requestTime
            );
        }

        if (
            DateTimeOffset.TryParseExact(
                headers.Date,
                HttpDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out requestTime
            )
        )
        {
            timestamp = requestTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            return true;
        }

        timestamp = "";
        return false;
    }

    /// <exception cref="IOException">The response could not be written.</exception>
    private async Task AuthenticatePresignedAsync(HttpContext context)
    {
        const long maxExpirySeconds = 604_800;
        var request = context.Request;
        var query = request.Query;

        var credentialParts = query["X-Amz-Credential"].ToString().Split('/');
        var signedHeaderList = query["X-Amz-SignedHeaders"]
            .ToString()
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (
            query["X-Amz-Algorithm"] != "AWS4-HMAC-SHA256"
            || credentialParts
                is not [var accessKeyId, var date, var region, var service, "aws4_request"]
            || accessKeyId.Length == 0
            || signedHeaderList.Length == 0
            || !DecimalDigits.TryParseInt64(query["X-Amz-Expires"], out var expiresSeconds)
            || expiresSeconds is < 1 or > maxExpirySeconds
        )
        {
            await Reject(context, S3Errors.AuthorizationQueryParametersError).ConfigureAwait(false);
            return;
        }

        var timestamp = query["X-Amz-Date"].ToString();
        if (
            !DateTimeOffset.TryParseExact(
                timestamp,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var signedAt
            )
        )
        {
            await Reject(context, S3Errors.AuthorizationQueryParametersError).ConfigureAwait(false);
            return;
        }

        if (timeProvider.GetUtcNow() > signedAt.AddSeconds(expiresSeconds))
        {
            await Reject(context, S3Errors.PresignedRequestExpired).ConfigureAwait(false);
            return;
        }

        var secretAccessKey = credentials.FindSecretKey(accessKeyId);
        if (secretAccessKey is null)
        {
            await Reject(context, S3Errors.InvalidAccessKeyId).ConfigureAwait(false);
            return;
        }

        var scope = new CredentialScope(date, region, service);
        var canonicalRequest = CanonicalRequest.Build(
            request,
            signedHeaderList,
            "UNSIGNED-PAYLOAD",
            omitSignatureParameter: true
        );
        var expected = SigV4Signer.SignCanonicalRequest(
            SigV4Signer.DeriveSigningKey(secretAccessKey, scope),
            scope,
            timestamp,
            canonicalRequest
        );
        if (!SigV4Signer.SignaturesEqual(expected, query["X-Amz-Signature"].ToString()))
        {
            await Reject(context, S3Errors.SignatureDoesNotMatch).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <exception cref="IOException">The response could not be written.</exception>
    private static Task Reject(HttpContext context, S3Error error) =>
        new S3ErrorResult(error).ExecuteAsync(context);
}
