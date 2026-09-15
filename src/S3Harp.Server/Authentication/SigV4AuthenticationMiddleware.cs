using System.Globalization;

namespace S3Harp.Server.Authentication;

/// <summary>Verifies the SigV4 signature on every request before it reaches an operation.</summary>
public sealed class SigV4AuthenticationMiddleware(
    RequestDelegate next, ICredentialStore credentials, TimeProvider timeProvider)
{
    private const string ContentSha256Header = "x-amz-content-sha256";
    private const string DateHeader = "x-amz-date";
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(15);

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        string? authorization = request.Headers.Authorization;
        if (string.IsNullOrEmpty(authorization))
        {
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

        string? timestamp = request.Headers[DateHeader];
        if (!DateTimeOffset.TryParseExact(
                timestamp, TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var requestTime))
        {
            await Reject(context, S3Errors.AccessDenied).ConfigureAwait(false);
            return;
        }

        if ((timeProvider.GetUtcNow() - requestTime).Duration() > MaxClockSkew)
        {
            await Reject(context, S3Errors.RequestTimeTooSkewed).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(
                header.Scope.Date,
                requestTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
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
            signingKey, header.Scope, timestamp!, canonicalRequest);
        if (!SigV4Signer.SignaturesEqual(expected, header.Signature))
        {
            await Reject(context, S3Errors.SignatureDoesNotMatch).ConfigureAwait(false);
            return;
        }

        request.Body = payloadHash switch
        {
            "UNSIGNED-PAYLOAD" => request.Body,
            "STREAMING-AWS4-HMAC-SHA256-PAYLOAD" => new SigV4ChunkedStream(
                request.Body, signingKey, header.Scope, timestamp!, header.Signature),
            "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER" => new SigV4ChunkedStream(
                request.Body, signingKey, header.Scope, timestamp!, header.Signature,
                signedTrailer: true),
            _ => new Sha256VerifyingStream(request.Body, payloadHash),
        };

        await next(context).ConfigureAwait(false);
    }

    private static Task Reject(HttpContext context, S3Error error) =>
        new S3ErrorResult(error).ExecuteAsync(context);
}
