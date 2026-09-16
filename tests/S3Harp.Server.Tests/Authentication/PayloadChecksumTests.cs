using System.Text;
using Microsoft.AspNetCore.Http;
using S3Harp.Server.Authentication;
using Xunit;

namespace S3Harp.Server.Tests.Authentication;

/// <summary>
/// Check values are the standard "123456789" vectors of each algorithm; the base64
/// forms are what S3 clients put in <c>x-amz-checksum-*</c>.
/// </summary>
public sealed class PayloadChecksumTests
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
    [InlineData("x-amz-checksum-crc32", ChecksumAlgorithm.Crc32)]
    [InlineData("X-Amz-Checksum-CRC32C", ChecksumAlgorithm.Crc32C)]
    [InlineData("x-amz-checksum-crc64nvme", ChecksumAlgorithm.Crc64Nvme)]
    [InlineData("x-amz-checksum-sha1", ChecksumAlgorithm.Sha1)]
    [InlineData("x-amz-checksum-sha256", ChecksumAlgorithm.Sha256)]
    public void NamesEachAlgorithmByItsHeader(string header, ChecksumAlgorithm expected)
    {
        Assert.True(ChecksumAlgorithms.TryParseHeaderName(header, out var algorithm));
        Assert.Equal(expected, algorithm);
        Assert.Equal(header.ToLowerInvariant(), ChecksumAlgorithms.HeaderName(expected));
    }

    [Fact]
    public void LeavesUnknownHeadersUnnamed()
    {
        Assert.False(ChecksumAlgorithms.TryParseHeaderName("x-amz-checksum-md5", out _));
    }

    [Fact]
    public void FindsTheChecksumARequestDeclaresInItsHeaders()
    {
        var headers = new HeaderDictionary { ["x-amz-checksum-sha1"] = "gLagvNJpcFuHJZa/U8arrgX+MoM=" };

        Assert.True(ChecksumAlgorithms.TryFindDeclared(headers, out var algorithm, out var declared));
        Assert.Equal(ChecksumAlgorithm.Sha1, algorithm);
        Assert.Equal("gLagvNJpcFuHJZa/U8arrgX+MoM=", declared);
        Assert.False(ChecksumAlgorithms.TryFindDeclared(new HeaderDictionary(), out _, out _));
    }
}
