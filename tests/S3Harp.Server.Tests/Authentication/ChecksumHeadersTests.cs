using Microsoft.AspNetCore.Http;
using S3Harp.Core;
using S3Harp.Server.Authentication;
using Xunit;

namespace S3Harp.Server.Tests.Authentication;

public sealed class ChecksumHeadersTests
{
    [Theory]
    [InlineData("x-amz-checksum-crc32", ChecksumAlgorithm.Crc32)]
    [InlineData("X-Amz-Checksum-CRC32C", ChecksumAlgorithm.Crc32C)]
    [InlineData("x-amz-checksum-crc64nvme", ChecksumAlgorithm.Crc64Nvme)]
    [InlineData("x-amz-checksum-sha1", ChecksumAlgorithm.Sha1)]
    [InlineData("x-amz-checksum-sha256", ChecksumAlgorithm.Sha256)]
    public void NamesEachAlgorithmByItsHeader(string header, ChecksumAlgorithm expected)
    {
        Assert.True(ChecksumHeaders.TryParseHeaderName(header, out var algorithm));
        Assert.Equal(expected, algorithm);
        Assert.Equal(header.ToLowerInvariant(), ChecksumHeaders.HeaderName(expected));
    }

    [Fact]
    public void LeavesUnknownHeadersUnnamed()
    {
        Assert.False(ChecksumHeaders.TryParseHeaderName("x-amz-checksum-md5", out _));
    }

    [Fact]
    public void FindsTheChecksumARequestDeclaresInItsHeaders()
    {
        var headers = new HeaderDictionary { ["x-amz-checksum-sha1"] = "gLagvNJpcFuHJZa/U8arrgX+MoM=" };

        Assert.True(ChecksumHeaders.TryFindDeclared(headers, out var algorithm, out var declared));
        Assert.Equal(ChecksumAlgorithm.Sha1, algorithm);
        Assert.Equal("gLagvNJpcFuHJZa/U8arrgX+MoM=", declared);
        Assert.False(ChecksumHeaders.TryFindDeclared(new HeaderDictionary(), out _, out _));
    }

    [Fact]
    public void TheDeclaredChecksumNamesTheUploadAlgorithm()
    {
        var headers = new HeaderDictionary
        {
            ["x-amz-checksum-sha1"] = "gLagvNJpcFuHJZa/U8arrgX+MoM=",
            ["x-amz-sdk-checksum-algorithm"] = "CRC32",
        };

        Assert.Equal(ChecksumAlgorithm.Sha1, ChecksumHeaders.UploadAlgorithm(headers));
    }

    [Fact]
    public void TheAnnouncedTrailerNamesTheUploadAlgorithm()
    {
        var headers = new HeaderDictionary { ["x-amz-trailer"] = "x-amz-checksum-crc32c" };

        Assert.Equal(ChecksumAlgorithm.Crc32C, ChecksumHeaders.UploadAlgorithm(headers));
    }

    [Theory]
    [InlineData("x-amz-sdk-checksum-algorithm")]
    [InlineData("x-amz-checksum-algorithm")]
    public void TheRequestedAlgorithmHeaderNamesTheUploadAlgorithm(string header)
    {
        var headers = new HeaderDictionary { [header] = "sha256" };

        Assert.Equal(ChecksumAlgorithm.Sha256, ChecksumHeaders.UploadAlgorithm(headers));
    }

    [Fact]
    public void WithoutAnyChecksumRequest_TheUploadAlgorithmIsCrc64Nvme()
    {
        Assert.Equal(ChecksumAlgorithm.Crc64Nvme, ChecksumHeaders.UploadAlgorithm(new HeaderDictionary()));
    }

    [Fact]
    public void WritesAChecksumAsItsValueAndTypeHeaders()
    {
        var headers = new HeaderDictionary();

        ChecksumHeaders.Write(
            headers, new Checksum(ChecksumAlgorithm.Crc64Nvme, "Qeh8oXvGiSo=", ChecksumType.FullObject));

        Assert.Equal("Qeh8oXvGiSo=", headers["x-amz-checksum-crc64nvme"]);
        Assert.Equal("FULL_OBJECT", headers["x-amz-checksum-type"]);
    }

    [Fact]
    public void NamesTheXmlElementOfEachAlgorithm()
    {
        Assert.Equal("ChecksumCRC32C", ChecksumHeaders.ElementName(ChecksumAlgorithm.Crc32C));
        Assert.Equal("COMPOSITE", ChecksumHeaders.TypeName(ChecksumType.Composite));
    }
}
