using System.Security.Cryptography;

namespace S3Harp.Server.Authentication;

/// <summary>The checksum algorithms S3 lets a client attach to a payload.</summary>
public enum ChecksumAlgorithm
{
    Crc32,
    Crc32C,
    Crc64Nvme,
    Sha1,
    Sha256,
}

/// <summary>
/// Names the checksum algorithms by their <c>x-amz-checksum-*</c> headers and
/// creates the incremental computation for each.
/// </summary>
public static class ChecksumAlgorithms
{
    private const string HeaderPrefix = "x-amz-checksum-";

    private static readonly (ChecksumAlgorithm Algorithm, string Suffix)[] Names =
    [
        (ChecksumAlgorithm.Crc32, "crc32"),
        (ChecksumAlgorithm.Crc32C, "crc32c"),
        (ChecksumAlgorithm.Crc64Nvme, "crc64nvme"),
        (ChecksumAlgorithm.Sha1, "sha1"),
        (ChecksumAlgorithm.Sha256, "sha256"),
    ];

    public static string HeaderName(ChecksumAlgorithm algorithm) =>
        HeaderPrefix + Names.First(name => name.Algorithm == algorithm).Suffix;

    public static bool TryParseHeaderName(string headerName, out ChecksumAlgorithm algorithm)
    {
        ArgumentNullException.ThrowIfNull(headerName);
        foreach (var (candidate, suffix) in Names)
        {
            if (headerName.Length == HeaderPrefix.Length + suffix.Length
                && headerName.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase)
                && headerName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                algorithm = candidate;
                return true;
            }
        }

        algorithm = default;
        return false;
    }

    /// <summary>
    /// The checksum a request declares for its body in an <c>x-amz-checksum-*</c> header.
    /// </summary>
    public static bool TryFindDeclared(
        IHeaderDictionary headers, out ChecksumAlgorithm algorithm, out string declared)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var (candidate, _) in Names)
        {
            if (headers.TryGetValue(HeaderName(candidate), out var value) && value.Count > 0)
            {
                algorithm = candidate;
                declared = value.ToString().Trim();
                return true;
            }
        }

        algorithm = default;
        declared = "";
        return false;
    }

    public static IncrementalChecksum Create(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Crc32 => new CrcChecksum(CrcChecksum.Crc32Table, width: 32),
        ChecksumAlgorithm.Crc32C => new CrcChecksum(CrcChecksum.Crc32CTable, width: 32),
        ChecksumAlgorithm.Crc64Nvme => new CrcChecksum(CrcChecksum.Crc64NvmeTable, width: 64),
        ChecksumAlgorithm.Sha1 => new HashChecksum(HashAlgorithmName.SHA1),
        ChecksumAlgorithm.Sha256 => new HashChecksum(HashAlgorithmName.SHA256),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };
}

/// <summary>A payload checksum accumulated as the payload's bytes pass through.</summary>
public abstract class IncrementalChecksum : IDisposable
{
    private byte[]? result;

    public abstract void Append(ReadOnlySpan<byte> data);

    /// <summary>The checksum of everything appended, in S3's big-endian byte order.</summary>
    public byte[] Finish() => result ??= Compute();

    /// <summary>True when the base64 value a client declared is this payload's checksum.</summary>
    public bool Matches(string declaredBase64)
    {
        ArgumentNullException.ThrowIfNull(declaredBase64);
        Span<byte> declared = stackalloc byte[64];
        return Convert.TryFromBase64String(declaredBase64, declared, out var length)
            && CryptographicOperations.FixedTimeEquals(declared[..length], Finish());
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected abstract byte[] Compute();

    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>A cryptographic hash of the payload.</summary>
internal sealed class HashChecksum(HashAlgorithmName algorithm) : IncrementalChecksum
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(algorithm);

    public override void Append(ReadOnlySpan<byte> data) => hash.AppendData(data);

    protected override byte[] Compute() => hash.GetHashAndReset();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hash.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// A reflected CRC with all-ones initial value and final XOR, which is the shape of
/// CRC-32, CRC-32C and CRC-64/NVME alike; only the polynomial differs.
/// </summary>
internal sealed class CrcChecksum(ulong[] table, int width) : IncrementalChecksum
{
    internal static readonly ulong[] Crc32Table = BuildTable(0xEDB88320);
    internal static readonly ulong[] Crc32CTable = BuildTable(0x82F63B78);
    internal static readonly ulong[] Crc64NvmeTable = BuildTable(0x9A6C9329AC4BC9B5);

    private readonly ulong mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
    private ulong crc = width == 64 ? ulong.MaxValue : (1UL << width) - 1;

    public override void Append(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = table[(byte)(crc ^ b)] ^ (crc >> 8);
        }
    }

    protected override byte[] Compute()
    {
        var value = (crc ^ mask) & mask;
        var bytes = new byte[width / 8];
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            bytes[i] = (byte)value;
            value >>= 8;
        }

        return bytes;
    }

    private static ulong[] BuildTable(ulong reflectedPolynomial)
    {
        var table = new ulong[256];
        for (var i = 0; i < table.Length; i++)
        {
            var entry = (ulong)i;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? (entry >> 1) ^ reflectedPolynomial : entry >> 1;
            }

            table[i] = entry;
        }

        return table;
    }
}
