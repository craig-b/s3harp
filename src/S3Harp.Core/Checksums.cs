using System.Security.Cryptography;

namespace S3Harp.Core;

/// <summary>The checksum algorithms S3 lets a client attach to a payload.</summary>
public enum ChecksumAlgorithm
{
    Crc32,
    Crc32C,
    Crc64Nvme,
    Sha1,
    Sha256,
}

/// <summary>Whether a checksum covers the object's bytes or is composed from its parts' checksums.</summary>
public enum ChecksumType
{
    FullObject,
    Composite,
}

/// <summary>
/// An object's stored checksum: the algorithm, the base64 value as S3 presents it
/// (a composite value carries the "-N" part-count suffix), and its type.
/// </summary>
public sealed record Checksum(ChecksumAlgorithm Algorithm, string Value, ChecksumType Type);

/// <summary>A checksum value with its algorithm: a part's, or one a client declares.</summary>
public sealed record ChecksumValue(ChecksumAlgorithm Algorithm, string Value);

/// <summary>
/// Names the checksum algorithms as S3 does and creates the incremental
/// computation of each.
/// </summary>
public static class ChecksumAlgorithms
{
    private static readonly (ChecksumAlgorithm Algorithm, string Name)[] Names =
    [
        (ChecksumAlgorithm.Crc32, "CRC32"),
        (ChecksumAlgorithm.Crc32C, "CRC32C"),
        (ChecksumAlgorithm.Crc64Nvme, "CRC64NVME"),
        (ChecksumAlgorithm.Sha1, "SHA1"),
        (ChecksumAlgorithm.Sha256, "SHA256"),
    ];

    public static string Name(ChecksumAlgorithm algorithm) =>
        Names.First(name => name.Algorithm == algorithm).Name;

    public static bool TryParseName(string name, out ChecksumAlgorithm algorithm)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var (candidate, candidateName) in Names)
        {
            if (string.Equals(name, candidateName, StringComparison.OrdinalIgnoreCase))
            {
                algorithm = candidate;
                return true;
            }
        }

        algorithm = default;
        return false;
    }

    /// <summary>
    /// S3's composite checksum of a multipart object: the checksum of the parts'
    /// checksum bytes in order, suffixed with the part count.
    /// </summary>
    public static string Composite(ChecksumAlgorithm algorithm, IEnumerable<string> partChecksums)
    {
        ArgumentNullException.ThrowIfNull(partChecksums);
        using var checksum = Create(algorithm);
        var count = 0;
        foreach (var part in partChecksums)
        {
            checksum.Append(Convert.FromBase64String(part));
            count++;
        }

        return $"{Convert.ToBase64String(checksum.Finish())}-{count}";
    }

    /// <summary>The type a multipart upload takes when the client names only the algorithm.</summary>
    public static ChecksumType DefaultType(ChecksumAlgorithm algorithm) =>
        algorithm == ChecksumAlgorithm.Crc64Nvme ? ChecksumType.FullObject : ChecksumType.Composite;

    /// <summary>
    /// Whether a multipart upload may combine the algorithm and type: CRC-32 and
    /// CRC-32C span both, the SHAs compose only, and CRC-64/NVME covers whole objects only.
    /// </summary>
    public static bool Supports(ChecksumAlgorithm algorithm, ChecksumType type) => algorithm switch
    {
        ChecksumAlgorithm.Crc32 or ChecksumAlgorithm.Crc32C => true,
        ChecksumAlgorithm.Crc64Nvme => type == ChecksumType.FullObject,
        _ => type == ChecksumType.Composite,
    };

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
