using System.Text;
using Xunit;

namespace S3Harp.Core.Tests;

/// <summary>
/// Check values are the standard "123456789" vectors of each algorithm; the base64
/// forms are what S3 clients put in <c>x-amz-checksum-*</c>.
/// </summary>
public sealed class ChecksumTests
{
    [Theory]
    [InlineData(ChecksumAlgorithm.Crc32, "cbf43926")]
    [InlineData(ChecksumAlgorithm.Crc32C, "e3069283")]
    [InlineData(ChecksumAlgorithm.Crc64Nvme, "ae8b14860a799888")]
    [InlineData(ChecksumAlgorithm.Sha1, "f7c3bc1d808e04732adf679965ccc34ca7ae3441")]
    [InlineData(
        ChecksumAlgorithm.Sha256,
        "15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225"
    )]
    public void ProducesTheStandardCheckValue(ChecksumAlgorithm algorithm, string expectedHex)
    {
        using var checksum = ChecksumAlgorithms.Create(algorithm);

        checksum.Append("1234"u8);
        checksum.Append("56789"u8);

        Assert.Equal(expectedHex, Convert.ToHexStringLower(checksum.Finish()));
    }

    [Fact]
    public void MatchesTheBase64ValueAClientDeclares()
    {
        using var checksum = ChecksumAlgorithms.Create(ChecksumAlgorithm.Crc64Nvme);
        checksum.Append(Encoding.ASCII.GetBytes(new string('A', 1024)));

        Assert.True(checksum.Matches("Qeh8oXvGiSo="));
    }

    [Theory]
    [InlineData("AAAAAA==")]
    [InlineData("bad")]
    [InlineData("")]
    public void RejectsAValueThatIsNotThePayloadsChecksum(string declared)
    {
        using var checksum = ChecksumAlgorithms.Create(ChecksumAlgorithm.Crc32);
        checksum.Append("Hello, S3Harp!"u8);

        Assert.False(checksum.Matches(declared));
    }

    [Theory]
    [InlineData(ChecksumAlgorithm.Crc32, "CRC32")]
    [InlineData(ChecksumAlgorithm.Crc32C, "CRC32C")]
    [InlineData(ChecksumAlgorithm.Crc64Nvme, "CRC64NVME")]
    [InlineData(ChecksumAlgorithm.Sha1, "SHA1")]
    [InlineData(ChecksumAlgorithm.Sha256, "SHA256")]
    public void NamesEachAlgorithmAsS3Does(ChecksumAlgorithm algorithm, string name)
    {
        Assert.Equal(name, ChecksumAlgorithms.Name(algorithm));
        Assert.True(ChecksumAlgorithms.TryParseName(name.ToLowerInvariant(), out var parsed));
        Assert.Equal(algorithm, parsed);
    }

    [Fact]
    public void LeavesUnknownNamesUnparsed() =>
        Assert.False(ChecksumAlgorithms.TryParseName("MD5", out _));

    [Theory]
    [InlineData(
        ChecksumAlgorithm.Sha256,
        "275VF5loJr1YYawit0XSHREhkFXYkkPKGuoK0x9VKxI=,mrHwOfjTL5Zwfj74F05HOQGLdUb7E5szdCbxgUSq6NM=,Vw7oB/nKQ5xWb3hNgbyfkvDiivl+U+/Dft48nfJfDow=",
        "uWBwpe1dxI4Vw8Gf0X9ynOdw/SS6VBzfWm9giiv1sf4=-3"
    )]
    [InlineData(
        ChecksumAlgorithm.Sha1,
        "iIaTCGbm+vdVjNqIMF2S0T7ibMk=,LS/TJ32bAVKEwRu+sE3X7awh/lk=,6DDwovUaHwrKNXDMzOGbuvj9kxI=",
        "sizjvY4eud3MrcHdZM3cQ/ol39o=-3"
    )]
    [InlineData(ChecksumAlgorithm.Crc32, "3ldvBQ==,0oUPLw==", "5m/Xbg==-2")]
    public void ComposesPartChecksumsAsS3Does(
        ChecksumAlgorithm algorithm,
        string partChecksums,
        string expected
    ) => Assert.Equal(expected, ChecksumAlgorithms.Composite(algorithm, partChecksums.Split(',')));

    [Theory]
    [InlineData(ChecksumAlgorithm.Crc32, ChecksumType.Composite)]
    [InlineData(ChecksumAlgorithm.Crc32C, ChecksumType.Composite)]
    [InlineData(ChecksumAlgorithm.Sha1, ChecksumType.Composite)]
    [InlineData(ChecksumAlgorithm.Sha256, ChecksumType.Composite)]
    [InlineData(ChecksumAlgorithm.Crc64Nvme, ChecksumType.FullObject)]
    public void DefaultsMultipartUploadsToTheTypeS3Does(
        ChecksumAlgorithm algorithm,
        ChecksumType type
    ) => Assert.Equal(type, ChecksumAlgorithms.DefaultType(algorithm));

    [Theory]
    [InlineData(ChecksumAlgorithm.Crc32, ChecksumType.FullObject, true)]
    [InlineData(ChecksumAlgorithm.Crc32C, ChecksumType.FullObject, true)]
    [InlineData(ChecksumAlgorithm.Sha256, ChecksumType.FullObject, false)]
    [InlineData(ChecksumAlgorithm.Sha1, ChecksumType.FullObject, false)]
    [InlineData(ChecksumAlgorithm.Crc64Nvme, ChecksumType.Composite, false)]
    public void OnlyCrcsSpanBothTypes(
        ChecksumAlgorithm algorithm,
        ChecksumType type,
        bool supported
    ) => Assert.Equal(supported, ChecksumAlgorithms.Supports(algorithm, type));

    /// <summary>
    /// A payload long enough for the vectorised path with a tail that is not, split so
    /// the running value carries across appends.
    /// </summary>
    [Theory]
    [InlineData(ChecksumAlgorithm.Crc32, "48d1721d")]
    [InlineData(ChecksumAlgorithm.Crc32C, "987a5180")]
    [InlineData(ChecksumAlgorithm.Crc64Nvme, "47b7273689e8cea3")]
    public void ProducesTheCheckValueOfALongPayload(ChecksumAlgorithm algorithm, string expectedHex)
    {
        var payload = new byte[4099];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        using var checksum = ChecksumAlgorithms.Create(algorithm);

        checksum.Append(payload.AsSpan(0, 1000));
        checksum.Append(payload.AsSpan(1000));

        Assert.Equal(expectedHex, Convert.ToHexStringLower(checksum.Finish()));
    }
}
