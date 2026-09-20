namespace S3Harp.Server.Authentication;

/// <summary>The server's root keypair, supplied via configuration at startup; its id names the owner of every bucket.</summary>
internal sealed record RootCredentials(string AccessKeyId, string SecretAccessKey);

/// <summary>Resolves the secret key for an access key id presented by a request.</summary>
internal interface ICredentialStore
{
    string? FindSecretKey(string accessKeyId);
}

/// <summary>A credential store holding every keypair configuration lists, the root pair among them.</summary>
internal sealed class CredentialStore(IReadOnlyDictionary<string, string> secretsByAccessKeyId)
    : ICredentialStore
{
    public string? FindSecretKey(string accessKeyId) =>
        secretsByAccessKeyId.GetValueOrDefault(accessKeyId);
}
