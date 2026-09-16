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
}
