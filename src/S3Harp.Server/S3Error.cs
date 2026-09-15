namespace S3Harp.Server;

/// <summary>An S3 protocol error: the wire-level code, HTTP status, and human-readable message.</summary>
public sealed record S3Error(string Code, int StatusCode, string Message);

/// <summary>The catalog of S3 errors S3Harp can return.</summary>
public static class S3Errors
{
    public static S3Error AccessDenied { get; } = new(
        "AccessDenied",
        StatusCodes.Status403Forbidden,
        "Access Denied.");

    public static S3Error BucketAlreadyOwnedByYou { get; } = new(
        "BucketAlreadyOwnedByYou",
        StatusCodes.Status409Conflict,
        "Your previous request to create the named bucket succeeded and you already own it.");

    public static S3Error InvalidBucketName { get; } = new(
        "InvalidBucketName",
        StatusCodes.Status400BadRequest,
        "The specified bucket is not valid.");

    public static S3Error NoSuchBucket { get; } = new(
        "NoSuchBucket",
        StatusCodes.Status404NotFound,
        "The specified bucket does not exist.");

    public static S3Error AuthorizationHeaderMalformed { get; } = new(
        "AuthorizationHeaderMalformed",
        StatusCodes.Status400BadRequest,
        "The authorization header is malformed.");

    public static S3Error InvalidAccessKeyId { get; } = new(
        "InvalidAccessKeyId",
        StatusCodes.Status403Forbidden,
        "The AWS access key Id you provided does not exist in our records.");

    public static S3Error MissingContentSha256 { get; } = new(
        "InvalidRequest",
        StatusCodes.Status400BadRequest,
        "Missing required header for this request: x-amz-content-sha256.");

    public static S3Error NotImplemented { get; } = new(
        "NotImplemented",
        StatusCodes.Status501NotImplemented,
        "S3Harp does not implement this operation yet.");

    public static S3Error RequestTimeTooSkewed { get; } = new(
        "RequestTimeTooSkewed",
        StatusCodes.Status403Forbidden,
        "The difference between the request time and the server's time is too large.");

    public static S3Error SignatureDoesNotMatch { get; } = new(
        "SignatureDoesNotMatch",
        StatusCodes.Status403Forbidden,
        "The request signature we calculated does not match the signature you provided. Check your key and signing method.");
}
