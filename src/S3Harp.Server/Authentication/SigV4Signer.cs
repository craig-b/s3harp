using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace S3Harp.Server.Authentication;

/// <summary>The AWS Signature Version 4 signing algorithm.</summary>
public static class SigV4Signer
{
    public static byte[] DeriveSigningKey(string secretAccessKey, CredentialScope scope)
    {
        ArgumentNullException.ThrowIfNull(secretAccessKey);
        ArgumentNullException.ThrowIfNull(scope);

        var dateKey = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), scope.Date);
        var regionKey = HmacSha256(dateKey, scope.Region);
        var serviceKey = HmacSha256(regionKey, scope.Service);
        return HmacSha256(serviceKey, "aws4_request");
    }

    public static string SignCanonicalRequest(
        byte[] signingKey,
        CredentialScope scope,
        string timestamp,
        string canonicalRequest
    )
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(canonicalRequest);

        var stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256",
            timestamp,
            scope.ToString(),
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest))
        );
        return Sign(signingKey, stringToSign);
    }

    public static string Sign(byte[] signingKey, string stringToSign) =>
        Convert.ToHexStringLower(HmacSha256(signingKey, stringToSign));

    public static string Sha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Whether the presented hex signature matches the expected one, compared in constant time and any letter case.</summary>
    public static bool SignaturesEqual(string expected, string presented)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(presented);
        if (expected.Length != presented.Length || expected.Length > MaxSignatureLength)
        {
            return false;
        }

        Span<char> lowered = stackalloc char[expected.Length];
        presented.AsSpan().ToLowerInvariant(lowered);
        return CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(expected.AsSpan()),
            MemoryMarshal.AsBytes((ReadOnlySpan<char>)lowered)
        );
    }

    /// <summary>A SHA-256 HMAC is 64 hex characters; nothing longer is a signature.</summary>
    private const int MaxSignatureLength = 64;

    private static byte[] HmacSha256(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}
