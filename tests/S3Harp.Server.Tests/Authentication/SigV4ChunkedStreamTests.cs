using System.Text;
using S3Harp.Core;
using S3Harp.Server.Authentication;
using Xunit;

namespace S3Harp.Server.Tests.Authentication;

/// <summary>
/// Wire bodies and chunk signatures generated with an independent SigV4
/// implementation over the chain seed → "Hello, " → "S3Harp!" → final.
/// </summary>
public sealed class SigV4ChunkedStreamTests
{
    private const string SecretAccessKey = "s3harp-example-secret";
    private const string Timestamp = "20260916T120000Z";
    private const string SeedSignature =
        "1111111111111111111111111111111111111111111111111111111111111111";
    private const string FirstChunkSignature =
        "c7eabceb7a60fe2afca2590e22805584123e9e7095aad9a5125d2345057d774a";
    private const string SecondChunkSignature =
        "56853a83769be7bc8bc5ae041ccd735bea44aa8398b7a2bb6d73db32c55a1f57";
    private const string FinalChunkSignature =
        "17cf131f2609b1f12f691b2f8b7165df7ccfe43cf979d9aad444eef4b3cd39ec";

    private static readonly CredentialScope Scope = new("20260916", "us-east-1", "s3");

    [Fact]
    public async Task DecodesVerifiedChunksIntoTheOriginalPayload()
    {
        using var stream = CreateStream(WireBody());
        using var decoded = new MemoryStream();

        await stream.CopyToAsync(decoded, TestContext.Current.CancellationToken);

        Assert.Equal("Hello, S3Harp!", Encoding.UTF8.GetString(decoded.ToArray()));
    }

    [Fact]
    public async Task SingleByteReads_ReassembleThePayload()
    {
        using var stream = CreateStream(WireBody());
        using var decoded = new MemoryStream();
        var buffer = new byte[1];

        int read;
        while ((read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken)) > 0)
        {
            decoded.Write(buffer, 0, read);
        }

        Assert.Equal("Hello, S3Harp!", Encoding.UTF8.GetString(decoded.ToArray()));
    }

    [Fact]
    public async Task TamperedChunkData_IsRejectedBeforeItIsReadable()
    {
        var body = WireBody().Replace("Hello, ", "Hacked, "[..7], StringComparison.Ordinal);
        using var stream = CreateStream(body);
        using var decoded = new MemoryStream();

        await Assert.ThrowsAsync<PayloadVerificationException>(() =>
            stream.CopyToAsync(decoded, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task WrongChunkSignature_IsRejected()
    {
        var body = WireBody()
            .Replace(FirstChunkSignature, SecondChunkSignature, StringComparison.Ordinal);
        using var stream = CreateStream(body);
        using var decoded = new MemoryStream();

        await Assert.ThrowsAsync<PayloadVerificationException>(() =>
            stream.CopyToAsync(decoded, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task WrongSeedSignature_RejectsTheFirstChunk()
    {
        using var stream = CreateStream(
            WireBody(),
            seedSignature: "2222222222222222222222222222222222222222222222222222222222222222"
        );
        using var decoded = new MemoryStream();

        await Assert.ThrowsAsync<PayloadVerificationException>(() =>
            stream.CopyToAsync(decoded, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task SignedTrailerWire_DecodesTheOriginalPayload()
    {
        using var stream = CreateStream(TrailerWireBody(), signedTrailer: true);
        using var decoded = new MemoryStream();

        await stream.CopyToAsync(decoded, TestContext.Current.CancellationToken);

        Assert.Equal("Hello, S3Harp!", Encoding.UTF8.GetString(decoded.ToArray()));
    }

    [Fact]
    public async Task WrongTrailerSignature_IsRejected()
    {
        var body = TrailerWireBody()
            .Replace(TrailerSignature, FirstChunkSignature, StringComparison.Ordinal);
        using var stream = CreateStream(body, signedTrailer: true);
        using var decoded = new MemoryStream();

        await Assert.ThrowsAsync<PayloadVerificationException>(() =>
            stream.CopyToAsync(decoded, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task TrailerChecksumMatchingThePayload_IsAccepted()
    {
        using var stream = CreateStream(
            TrailerWireBody(PayloadCrc32, PayloadCrc32TrailerSignature),
            signedTrailer: true,
            trailerChecksum: ChecksumAlgorithm.Crc32
        );
        using var decoded = new MemoryStream();

        await stream.CopyToAsync(decoded, TestContext.Current.CancellationToken);

        Assert.Equal("Hello, S3Harp!", Encoding.UTF8.GetString(decoded.ToArray()));
    }

    [Fact]
    public async Task TrailerChecksumDifferingFromThePayload_IsRejectedAsBadDigest()
    {
        using var stream = CreateStream(
            TrailerWireBody(),
            signedTrailer: true,
            trailerChecksum: ChecksumAlgorithm.Crc32
        );
        using var decoded = new MemoryStream();

        var exception = await Assert.ThrowsAsync<PayloadVerificationException>(() =>
            stream.CopyToAsync(decoded, TestContext.Current.CancellationToken)
        );

        Assert.Equal(S3Errors.BadDigest, exception.Error);
    }

    [Fact]
    public async Task ExpectedTrailerChecksumThatNeverArrives_IsRejectedAsIncomplete()
    {
        using var stream = CreateStream(
            TrailerWireBody(),
            signedTrailer: true,
            trailerChecksum: ChecksumAlgorithm.Sha256
        );
        using var decoded = new MemoryStream();

        var exception = await Assert.ThrowsAsync<PayloadVerificationException>(() =>
            stream.CopyToAsync(decoded, TestContext.Current.CancellationToken)
        );

        Assert.Equal(S3Errors.IncompleteBody, exception.Error);
    }

    private const string TrailerSignature =
        "adf5dc3a91f8f7fc95b307653fb2ca0282c8cd842d652835653eddd8971a48d3";

    /// <summary>The CRC32 of "Hello, S3Harp!" as a client declares it, with its trailer signature.</summary>
    private const string PayloadCrc32 = "NadAdg==";
    private const string PayloadCrc32TrailerSignature =
        "69a5889247bb0c13682f01ac131f026131f8f381793699c12cb1398c337009d7";

    private static string WireBody() =>
        $"7;chunk-signature={FirstChunkSignature}\r\nHello, \r\n"
        + $"7;chunk-signature={SecondChunkSignature}\r\nS3Harp!\r\n"
        + $"0;chunk-signature={FinalChunkSignature}\r\n\r\n";

    private static string TrailerWireBody(
        string crc32 = "AAAAAA==",
        string trailerSignature = TrailerSignature
    ) =>
        $"7;chunk-signature={FirstChunkSignature}\r\nHello, \r\n"
        + $"7;chunk-signature={SecondChunkSignature}\r\nS3Harp!\r\n"
        + $"0;chunk-signature={FinalChunkSignature}\r\n"
        + $"x-amz-checksum-crc32:{crc32}\r\n"
        + $"x-amz-trailer-signature:{trailerSignature}\r\n"
        + "\r\n";

    private static SigV4ChunkedStream CreateStream(
        string wireBody,
        string seedSignature = SeedSignature,
        bool signedTrailer = false,
        ChecksumAlgorithm? trailerChecksum = null
    ) =>
        new(
            new MemoryStream(Encoding.UTF8.GetBytes(wireBody)),
            SigV4Signer.DeriveSigningKey(SecretAccessKey, Scope),
            Scope,
            Timestamp,
            seedSignature,
            signedTrailer,
            trailerChecksum
        );
}
