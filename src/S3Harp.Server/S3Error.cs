namespace S3Harp.Server;

/// <summary>An S3 protocol error: the wire-level code, HTTP status, and human-readable message.</summary>
internal sealed record S3Error(string Code, int StatusCode, string Message);

/// <summary>The catalog of S3 errors S3Harp can return.</summary>
internal static class S3Errors
{
    public static S3Error AccessDenied { get; } =
        new("AccessDenied", StatusCodes.Status403Forbidden, "Access Denied.");

    public static S3Error AuthorizationQueryParametersError { get; } =
        new(
            "AuthorizationQueryParametersError",
            StatusCodes.Status400BadRequest,
            "The query-string authentication parameters are invalid."
        );

    public static S3Error PresignedRequestExpired { get; } =
        new("AccessDenied", StatusCodes.Status403Forbidden, "Request has expired.");

    public static S3Error BadDigest { get; } =
        new(
            "BadDigest",
            StatusCodes.Status400BadRequest,
            "The Content-MD5 or checksum value that you specified did not match "
                + "what the server received."
        );

    public static S3Error ChecksumTypeUnsupported { get; } =
        new(
            "InvalidRequest",
            StatusCodes.Status400BadRequest,
            "The checksum type is not supported for the checksum algorithm."
        );

    public static S3Error BucketAlreadyOwnedByYou { get; } =
        new(
            "BucketAlreadyOwnedByYou",
            StatusCodes.Status409Conflict,
            "Your previous request to create the named bucket succeeded and you already own it."
        );

    public static S3Error IncompleteBody { get; } =
        new(
            "IncompleteBody",
            StatusCodes.Status400BadRequest,
            "The chunked request body ended before it was complete."
        );

    public static S3Error MalformedChunkedBody { get; } =
        new(
            "InvalidRequest",
            StatusCodes.Status400BadRequest,
            "The chunked request body is not framed as its payload declaration says."
        );

    public static S3Error InvalidArgument { get; } =
        new("InvalidArgument", StatusCodes.Status400BadRequest, "Invalid Argument.");

    public static S3Error InvalidBucketName { get; } =
        new(
            "InvalidBucketName",
            StatusCodes.Status400BadRequest,
            "The specified bucket is not valid."
        );

    public static S3Error BucketNotEmpty { get; } =
        new(
            "BucketNotEmpty",
            StatusCodes.Status409Conflict,
            "The bucket you tried to delete is not empty."
        );

    public static S3Error NoSuchBucket { get; } =
        new("NoSuchBucket", StatusCodes.Status404NotFound, "The specified bucket does not exist.");

    public static S3Error NoSuchKey { get; } =
        new("NoSuchKey", StatusCodes.Status404NotFound, "The specified key does not exist.");

    public static S3Error XAmzContentSHA256Mismatch { get; } =
        new(
            "XAmzContentSHA256Mismatch",
            StatusCodes.Status400BadRequest,
            "The provided 'x-amz-content-sha256' header does not match what was computed."
        );

    public static S3Error AuthorizationHeaderMalformed { get; } =
        new(
            "AuthorizationHeaderMalformed",
            StatusCodes.Status400BadRequest,
            "The authorization header is malformed."
        );

    public static S3Error InvalidAccessKeyId { get; } =
        new(
            "InvalidAccessKeyId",
            StatusCodes.Status403Forbidden,
            "The AWS access key Id you provided does not exist in our records."
        );

    public static S3Error EntityTooSmall { get; } =
        new(
            "EntityTooSmall",
            StatusCodes.Status400BadRequest,
            "Your proposed upload is smaller than the minimum allowed object size"
        );

    public static S3Error InvalidPart { get; } =
        new(
            "InvalidPart",
            StatusCodes.Status400BadRequest,
            "One or more of the specified parts could not be found or did not match its entity tag."
        );

    public static S3Error InvalidPartOrder { get; } =
        new(
            "InvalidPartOrder",
            StatusCodes.Status400BadRequest,
            "The list of parts was not in ascending order. Parts must be ordered by part number."
        );

    public static S3Error InvalidRange { get; } =
        new(
            "InvalidRange",
            StatusCodes.Status416RangeNotSatisfiable,
            "The requested range is not satisfiable."
        );

    public static S3Error MalformedXML { get; } =
        new(
            "MalformedXML",
            StatusCodes.Status400BadRequest,
            "The XML you provided was not well-formed."
        );

    public static S3Error PreconditionFailed { get; } =
        new(
            "PreconditionFailed",
            StatusCodes.Status412PreconditionFailed,
            "At least one of the pre-conditions you specified did not hold"
        );

    public static S3Error NoSuchUpload { get; } =
        new(
            "NoSuchUpload",
            StatusCodes.Status404NotFound,
            "The specified multipart upload does not exist."
        );

    public static S3Error CopyToSelf { get; } =
        new(
            "InvalidRequest",
            StatusCodes.Status400BadRequest,
            "This copy request is illegal because it is trying to copy an object to itself "
                + "without changing the object's metadata, storage class, website redirect location "
                + "or encryption attributes."
        );

    public static S3Error RangeWithPartNumber { get; } =
        new(
            "InvalidRequest",
            StatusCodes.Status400BadRequest,
            "Cannot specify both Range header and partNumber query parameter"
        );

    public static S3Error MissingContentSha256 { get; } =
        new(
            "InvalidRequest",
            StatusCodes.Status400BadRequest,
            "Missing required header for this request: x-amz-content-sha256."
        );

    public static S3Error NotImplemented { get; } =
        new(
            "NotImplemented",
            StatusCodes.Status501NotImplemented,
            "S3Harp does not implement this operation yet."
        );

    public static S3Error RequestTimeTooSkewed { get; } =
        new(
            "RequestTimeTooSkewed",
            StatusCodes.Status403Forbidden,
            "The difference between the request time and the server's time is too large."
        );

    public static S3Error SignatureDoesNotMatch { get; } =
        new(
            "SignatureDoesNotMatch",
            StatusCodes.Status403Forbidden,
            "The request signature we calculated does not match the signature you provided. Check your key and signing method."
        );
}
