using System.Text;
using S3Harp.Core;
using S3Harp.Server.Authentication;
using Xunit;

namespace S3Harp.Server.Tests.Authentication;

/// <summary>
/// Wire bodies and chunk signatures generated with an independent SigV4
/// implementation over the chain seed → "Hello, " → "S3Harp!" → final.
/// </summary>
public sealed class AwsChunkedStreamTests
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
            await decoded.WriteAsync(
                buffer.AsMemory(0, read),
                TestContext.Current.CancellationToken
            );
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

    [Fact]
    public async Task UnsignedWire_DecodesTheOriginalPayloadAndVerifiesItsTrailerChecksum()
    {
        using var stream = CreateUnsignedStream(UnsignedTrailerWireBody(PayloadCrc32));
        using var decoded = new MemoryStream();

        await stream.CopyToAsync(decoded, TestContext.Current.CancellationToken);

        Assert.Equal("Hello, S3Harp!", Encoding.UTF8.GetString(decoded.ToArray()));
    }

    [Fact]
    public async Task UnsignedWire_WithATrailerChecksumDifferingFromThePayload_IsRejectedAsBadDigest()
    {
        using var stream = CreateUnsignedStream(UnsignedTrailerWireBody("AAAAAA=="));
        using var decoded = new MemoryStream();

        var exception = await Assert.ThrowsAsync<PayloadVerificationException>(() =>
            stream.CopyToAsync(decoded, TestContext.Current.CancellationToken)
        );

        Assert.Equal(S3Errors.BadDigest, exception.Error);
    }

    [Fact]
    public async Task UnsignedWire_WithoutTheAnnouncedTrailer_IsRejectedAsIncomplete()
    {
        using var stream = CreateUnsignedStream("7\r\nHello, \r\n7\r\nS3Harp!\r\n0\r\n\r\n");
        using var decoded = new MemoryStream();

        var exception = await Assert.ThrowsAsync<PayloadVerificationException>(() =>
            stream.CopyToAsync(decoded, TestContext.Current.CancellationToken)
        );

        Assert.Equal(S3Errors.IncompleteBody, exception.Error);
    }

    [Fact]
    public async Task UnsignedWire_CarryingChunkSignatures_IsMalformed()
    {
        using var stream = CreateUnsignedStream(WireBody());
        using var decoded = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            stream.CopyToAsync(decoded, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task SignedWire_WithoutChunkSignatures_IsMalformed()
    {
        using var stream = CreateStream(UnsignedTrailerWireBody(PayloadCrc32));
        using var decoded = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            stream.CopyToAsync(decoded, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task ReadsTheWireInBlocks_NotOneByteAtATime()
    {
        using var inner = new CountingStream(new MemoryStream(Encoding.UTF8.GetBytes(WireBody())));
        using var stream = CreateStream(inner);
        using var decoded = new MemoryStream();

        await stream.CopyToAsync(decoded, TestContext.Current.CancellationToken);

        Assert.Equal("Hello, S3Harp!", Encoding.UTF8.GetString(decoded.ToArray()));
        Assert.InRange(inner.Reads, 1, 2);
    }

    [Fact]
    public async Task ChunksLargerThanTheReadAheadBuffer_DecodeIntact()
    {
        var payload = new byte[200_000];
        Random.Shared.NextBytes(payload);
        using var stream = CreateStream(new MemoryStream(SignedWireBody(payload, 70_000)));
        using var decoded = new MemoryStream();

        await stream.CopyToAsync(decoded, TestContext.Current.CancellationToken);

        Assert.Equal(payload, decoded.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    public async Task FragmentedInnerReads_ReassembleThePayload(int fragment)
    {
        var payload = "Hello, S3Harp!"u8.ToArray();
        using var inner = new FragmentingStream(
            new MemoryStream(SignedWireBody(payload, chunkSize: 5)),
            fragment
        );
        using var stream = CreateStream(inner);
        using var decoded = new MemoryStream();

        await stream.CopyToAsync(decoded, TestContext.Current.CancellationToken);

        Assert.Equal(payload, decoded.ToArray());
    }

    /// <summary>Frames the payload in chunks of the given size, signing each with the test key.</summary>
    private static byte[] SignedWireBody(byte[] payload, int chunkSize)
    {
        var signingKey = SigV4Signer.DeriveSigningKey(SecretAccessKey, Scope);
        var previous = SeedSignature;
        using var wire = new MemoryStream();
        for (var offset = 0; ; offset = Math.Min(offset + chunkSize, payload.Length))
        {
            var chunk = payload.AsSpan(offset, Math.Min(chunkSize, payload.Length - offset));
            var stringToSign = string.Join(
                '\n',
                "AWS4-HMAC-SHA256-PAYLOAD",
                Timestamp,
                Scope.ToString(),
                previous,
                SigV4Signer.Sha256Hex([]),
                SigV4Signer.Sha256Hex(chunk)
            );
            previous = SigV4Signer.Sign(signingKey, stringToSign);
            wire.Write(Encoding.ASCII.GetBytes($"{chunk.Length:x};chunk-signature={previous}\r\n"));
            wire.Write(chunk);
            wire.Write("\r\n"u8);
            if (chunk.Length == 0)
            {
                return wire.ToArray();
            }
        }
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public int Reads { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            Reads++;
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return inner.Read(buffer, offset, count);
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>Hands out at most a fixed number of bytes per read, as a network stream may.</summary>
    private sealed class FragmentingStream(Stream inner, int fragment) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => inner.ReadAsync(buffer[..Math.Min(fragment, buffer.Length)], cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(fragment, count));

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
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

    /// <summary>The framing SDKs send over TLS: sizes without signatures, then an unsigned trailer.</summary>
    private static string UnsignedTrailerWireBody(string crc32) =>
        $"7\r\nHello, \r\n7\r\nS3Harp!\r\n0\r\nx-amz-checksum-crc32:{crc32}\r\n\r\n";

    private static AwsChunkedStream CreateUnsignedStream(string wireBody) =>
        AwsChunkedStream.Unsigned(
            new MemoryStream(Encoding.UTF8.GetBytes(wireBody)),
            ChecksumAlgorithm.Crc32
        );

    private static AwsChunkedStream CreateStream(
        string wireBody,
        string seedSignature = SeedSignature,
        bool signedTrailer = false,
        ChecksumAlgorithm? trailerChecksum = null
    ) =>
        CreateStream(
            new MemoryStream(Encoding.UTF8.GetBytes(wireBody)),
            seedSignature,
            signedTrailer,
            trailerChecksum
        );

    private static AwsChunkedStream CreateStream(
        Stream wire,
        string seedSignature = SeedSignature,
        bool signedTrailer = false,
        ChecksumAlgorithm? trailerChecksum = null
    ) =>
        AwsChunkedStream.Signed(
            wire,
            new ChunkSigning(
                SigV4Signer.DeriveSigningKey(SecretAccessKey, Scope),
                Scope,
                Timestamp,
                seedSignature
            ),
            signedTrailer,
            trailerChecksum
        );
}
