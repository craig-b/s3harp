using System.Diagnostics.CodeAnalysis;

namespace S3Harp.Server.Authentication;

/// <summary>The parsed contents of a SigV4 <c>Authorization</c> header.</summary>
public sealed record SigV4AuthorizationHeader(
    string AccessKeyId,
    CredentialScope Scope,
    IReadOnlyList<string> SignedHeaders,
    string Signature)
{
    private const string Scheme = "AWS4-HMAC-SHA256 ";

    public static bool TryParse(
        string? value, [NotNullWhen(true)] out SigV4AuthorizationHeader? header)
    {
        header = null;
        if (value is null || !value.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        string? credential = null;
        string? signedHeaders = null;
        string? signature = null;
        foreach (var part in value.AsSpan(Scheme.Length).ToString().Split(','))
        {
            var pair = part.Trim();
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            var name = pair[..separator];
            var parameterValue = pair[(separator + 1)..];
            switch (name)
            {
                case "Credential":
                    credential = parameterValue;
                    break;
                case "SignedHeaders":
                    signedHeaders = parameterValue;
                    break;
                case "Signature":
                    signature = parameterValue;
                    break;
                default:
                    return false;
            }
        }

        if (credential is null || signedHeaders is null || signature is null)
        {
            return false;
        }

        var credentialParts = credential.Split('/');
        if (credentialParts is not [var accessKeyId, var date, var region, var service, "aws4_request"]
            || accessKeyId.Length == 0)
        {
            return false;
        }

        var headers = signedHeaders.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (headers.Length == 0)
        {
            return false;
        }

        header = new SigV4AuthorizationHeader(
            accessKeyId, new CredentialScope(date, region, service), headers, signature);
        return true;
    }
}
