namespace S3Harp.Server;

/// <summary>An S3 protocol error: the wire-level code, HTTP status, and human-readable message.</summary>
public sealed record S3Error(string Code, int StatusCode, string Message);

/// <summary>The catalog of S3 errors S3Harp can return.</summary>
public static class S3Errors
{
    public static S3Error NotImplemented { get; } = new(
        "NotImplemented",
        StatusCodes.Status501NotImplemented,
        "S3Harp does not implement this operation yet.");
}
