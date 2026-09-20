using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using S3Harp.Core;

namespace S3Harp.Server.Authentication;

/// <summary>
/// Decodes an <c>aws-chunked</c> request body. A signed body
/// (<c>STREAMING-AWS4-HMAC-SHA256-PAYLOAD</c>) has each chunk's signature verified
/// against the SigV4 chain before its bytes become readable, and a signed trailer
/// verified the same way. An unsigned body (<c>STREAMING-UNSIGNED-PAYLOAD-TRAILER</c>,
/// what SDKs send over TLS) carries sizes alone. In either form the checksum the
/// client announced in <c>x-amz-trailer</c> is verified against the decoded payload.
/// A body that is framed wrongly or ends early fails verification like a bad
/// signature does, with the S3 error that says which.
/// </summary>
internal sealed class AwsChunkedStream : Stream
{
    private const int MaxHeaderLength = 1024;
    private const int MaxChunkSize = 16 * 1024 * 1024;
    private const int ReadAheadSize = 16 * 1024;

    private static readonly string EmptyHash = SigV4Signer.Sha256Hex([]);

    private static ReadOnlySpan<byte> SignaturePrefix => ";chunk-signature="u8;

    private static ReadOnlySpan<byte> LineEnd => "\r\n"u8;

    // The request body belongs to the server, which disposes it with the request.
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed")]
    private readonly Stream inner;
    private readonly ChunkSigning? signing;
    private readonly bool signedTrailer;
    private readonly IncrementalChecksum? checksum;
    private readonly string? checksumTrailer;

    /// <summary>Wire bytes read ahead of the decoder; the unconsumed part is [start, end).</summary>
    private readonly byte[] readAhead = ArrayPool<byte>.Shared.Rent(ReadAheadSize);
    private int readAheadStart;
    private int readAheadEnd;

    private string previousSignature;
    private byte[]? chunk;
    private int chunkLength;
    private int positionInChunk;
    private bool finished;

    private AwsChunkedStream(
        Stream inner,
        ChunkSigning? signing,
        bool signedTrailer,
        ChecksumAlgorithm? trailerChecksum
    )
    {
        this.inner = inner;
        this.signing = signing;
        this.signedTrailer = signedTrailer;
        previousSignature = signing?.SeedSignature ?? "";
        if (trailerChecksum is { } algorithm)
        {
            checksum = ChecksumAlgorithms.Create(algorithm);
            checksumTrailer = ChecksumHeaders.HeaderName(algorithm);
        }
    }

    /// <summary>A body whose chunks, and trailer when <paramref name="signedTrailer"/>, carry SigV4 signatures.</summary>
    public static AwsChunkedStream Signed(
        Stream inner,
        ChunkSigning signing,
        bool signedTrailer = false,
        ChecksumAlgorithm? trailerChecksum = null
    )
    {
        ArgumentNullException.ThrowIfNull(signing);
        return new(inner, signing, signedTrailer, trailerChecksum);
    }

    /// <summary>A body whose chunks carry sizes alone, ending in a trailer that is checked only for its checksum.</summary>
    public static AwsChunkedStream Unsigned(Stream inner, ChecksumAlgorithm? trailerChecksum) =>
        new(inner, signing: null, signedTrailer: true, trailerChecksum);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        while (!finished && positionInChunk >= chunkLength)
        {
            await LoadNextChunkAsync(cancellationToken).ConfigureAwait(false);
        }

        if (finished || buffer.Length == 0)
        {
            return 0;
        }

        var count = Math.Min(buffer.Length, chunkLength - positionInChunk);
        chunk.AsMemory(positionInChunk, count).CopyTo(buffer);
        positionInChunk += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    private async Task LoadNextChunkAsync(CancellationToken cancellationToken)
    {
        ReturnChunk();
        var header = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var separator = header.Span.IndexOf(SignaturePrefix);
        var sizeLength = separator < 0 ? header.Length : separator;
        if (
            (signing is null) != (separator < 0)
            || !Utf8Parser.TryParse(header.Span[..sizeLength], out long size, out var digits, 'x')
            || digits != sizeLength
            || size is < 0 or > MaxChunkSize
        )
        {
            throw new PayloadVerificationException(S3Errors.MalformedChunkedBody);
        }

        var data = await ReadChunkDataAsync((int)size, cancellationToken).ConfigureAwait(false);
        if (signing is not null)
        {
            VerifyChunkSignature(
                header.Span[(separator + SignaturePrefix.Length)..],
                data.Span,
                signing
            );
        }

        checksum?.Append(data.Span);
        if (size == 0)
        {
            if (signedTrailer)
            {
                await VerifyTrailerAsync(cancellationToken).ConfigureAwait(false);
            }

            finished = true;
            return;
        }

        await ConsumeChunkDelimiterAsync(cancellationToken).ConfigureAwait(false);
        positionInChunk = 0;
    }

    private void VerifyChunkSignature(
        ReadOnlySpan<byte> presented,
        ReadOnlySpan<byte> data,
        ChunkSigning chain
    )
    {
        var stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256-PAYLOAD",
            chain.Timestamp,
            chain.Scope.ToString(),
            previousSignature,
            EmptyHash,
            SigV4Signer.Sha256Hex(data)
        );
        var expectedSignature = SigV4Signer.Sign(chain.SigningKey, stringToSign);
        if (!SigV4Signer.SignaturesEqual(expectedSignature, Encoding.ASCII.GetString(presented)))
        {
            throw new PayloadVerificationException(S3Errors.SignatureDoesNotMatch);
        }

        previousSignature = expectedSignature;
    }

    /// <summary>Fills a pooled buffer with the chunk's bytes: what was read ahead first, the rest from the wire.</summary>
    private async ValueTask<ReadOnlyMemory<byte>> ReadChunkDataAsync(
        int size,
        CancellationToken cancellationToken
    )
    {
        chunk = ArrayPool<byte>.Shared.Rent(size);
        chunkLength = size;
        var buffered = Math.Min(size, readAheadEnd - readAheadStart);
        readAhead.AsSpan(readAheadStart, buffered).CopyTo(chunk);
        readAheadStart += buffered;
        var remaining = chunk.AsMemory(buffered, size - buffered);
        var read = await inner
            .ReadAtLeastAsync(
                remaining,
                remaining.Length,
                throwOnEndOfStream: false,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (read < remaining.Length)
        {
            throw new PayloadVerificationException(S3Errors.IncompleteBody);
        }

        return chunk.AsMemory(0, size);
    }

    private async Task VerifyTrailerAsync(CancellationToken cancellationToken)
    {
        var canonicalTrailer = new StringBuilder();
        var trailers = new List<(string Name, string Value)>();
        string? presentedSignature = null;
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line.Length == 0)
            {
                break;
            }

            var separator = line.Span.IndexOf((byte)':');
            if (separator <= 0)
            {
                throw new PayloadVerificationException(S3Errors.IncompleteBody);
            }

            var name = Encoding.ASCII.GetString(line.Span[..separator]);
            var value = Encoding.ASCII.GetString(line.Span[(separator + 1)..]).Trim();
            if (string.Equals(name, "x-amz-trailer-signature", StringComparison.Ordinal))
            {
                presentedSignature = value;
            }
            else
            {
                canonicalTrailer.Append(name).Append(':').Append(value).Append('\n');
                trailers.Add((name, value));
            }
        }

        if (signing is not null)
        {
            VerifyTrailerSignature(presentedSignature, canonicalTrailer.ToString(), signing);
        }

        if (checksum is null)
        {
            return;
        }

        var (declaredName, declaredValue) = trailers.FirstOrDefault(trailer =>
            string.Equals(trailer.Name, checksumTrailer, StringComparison.OrdinalIgnoreCase)
        );
        if (declaredName is null)
        {
            throw new PayloadVerificationException(S3Errors.IncompleteBody);
        }

        if (!checksum.Matches(declaredValue))
        {
            throw new PayloadVerificationException(S3Errors.BadDigest);
        }
    }

    private void VerifyTrailerSignature(
        string? presented,
        string canonicalTrailer,
        ChunkSigning chain
    )
    {
        if (presented is null)
        {
            throw new PayloadVerificationException(S3Errors.SignatureDoesNotMatch);
        }

        var stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256-TRAILER",
            chain.Timestamp,
            chain.Scope.ToString(),
            previousSignature,
            SigV4Signer.Sha256Hex(Encoding.UTF8.GetBytes(canonicalTrailer))
        );
        if (
            !SigV4Signer.SignaturesEqual(
                SigV4Signer.Sign(chain.SigningKey, stringToSign),
                presented
            )
        )
        {
            throw new PayloadVerificationException(S3Errors.SignatureDoesNotMatch);
        }
    }

    /// <summary>
    /// The next CRLF-terminated line of the wire, without its terminator. The memory
    /// points into the read-ahead buffer and is valid until the next read from the wire.
    /// </summary>
    private async ValueTask<ReadOnlyMemory<byte>> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var buffered = readAhead.AsMemory(readAheadStart, readAheadEnd - readAheadStart);
            var terminator = buffered.Span.IndexOf(LineEnd);
            if (terminator >= 0)
            {
                readAheadStart += terminator + LineEnd.Length;
                return buffered[..terminator];
            }

            if (buffered.Length > MaxHeaderLength)
            {
                throw new PayloadVerificationException(S3Errors.MalformedChunkedBody);
            }

            if (await FillReadAheadAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new PayloadVerificationException(S3Errors.IncompleteBody);
            }
        }
    }

    private async Task ConsumeChunkDelimiterAsync(CancellationToken cancellationToken)
    {
        while (readAheadEnd - readAheadStart < LineEnd.Length)
        {
            if (await FillReadAheadAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new PayloadVerificationException(S3Errors.IncompleteBody);
            }
        }

        if (!readAhead.AsSpan(readAheadStart, LineEnd.Length).SequenceEqual(LineEnd))
        {
            throw new PayloadVerificationException(S3Errors.MalformedChunkedBody);
        }

        readAheadStart += LineEnd.Length;
    }

    /// <summary>Reads more of the wire into the read-ahead buffer, compacting it first; 0 means the wire ended.</summary>
    private async ValueTask<int> FillReadAheadAsync(CancellationToken cancellationToken)
    {
        if (readAheadStart > 0)
        {
            readAhead.AsSpan(readAheadStart, readAheadEnd - readAheadStart).CopyTo(readAhead);
            readAheadEnd -= readAheadStart;
            readAheadStart = 0;
        }

        var read = await inner
            .ReadAsync(readAhead.AsMemory(readAheadEnd), cancellationToken)
            .ConfigureAwait(false);
        readAheadEnd += read;
        return read;
    }

    private void ReturnChunk()
    {
        if (chunk is { } rented)
        {
            ArrayPool<byte>.Shared.Return(rented);
            chunk = null;
            chunkLength = 0;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReturnChunk();
            ArrayPool<byte>.Shared.Return(readAhead);
            checksum?.Dispose();
        }

        base.Dispose(disposing);
    }
}
