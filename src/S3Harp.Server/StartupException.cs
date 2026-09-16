namespace S3Harp.Server;

/// <summary>
/// Raised when S3Harp cannot start as configured. The message names every
/// setting at fault and is meant to be shown to the operator as it is.
/// </summary>
public sealed class StartupException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
