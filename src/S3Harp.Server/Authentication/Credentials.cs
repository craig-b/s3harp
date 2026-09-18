namespace S3Harp.Server.Authentication;

/// <summary>The server's root keypair, supplied via configuration at startup.</summary>
internal sealed record RootCredentials(string AccessKeyId, string SecretAccessKey);

/// <summary>Resolves the secret key for an access key id presented by a request.</summary>
internal interface ICredentialStore
{
    string? FindSecretKey(string accessKeyId);
}

/// <summary>A credential store holding the single root keypair.</summary>
internal sealed class RootCredentialStore(RootCredentials credentials) : ICredentialStore
{
    public string? FindSecretKey(string accessKeyId) =>
        string.Equals(accessKeyId, credentials.AccessKeyId, StringComparison.Ordinal)
            ? credentials.SecretAccessKey
            : null;
}
