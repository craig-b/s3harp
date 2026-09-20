namespace S3Harp.Server.Authentication;

/// <summary>
/// The SigV4 chain a signed aws-chunked body is verified against: each chunk's
/// signature covers the one before it, starting from the request's own.
/// </summary>
internal sealed record ChunkSigning(
    byte[] SigningKey,
    CredentialScope Scope,
    string Timestamp,
    string SeedSignature
);
