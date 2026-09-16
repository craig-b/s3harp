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
        "15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225")]
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
    public void LeavesUnknownNamesUnparsed()
    {
        Assert.False(ChecksumAlgorithms.TryParseName("MD5", out _));
    }
}
