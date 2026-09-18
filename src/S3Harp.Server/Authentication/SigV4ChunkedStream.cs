using System.Buffers;
using System.Buffers.Text;
using System.Text;
using S3Harp.Core;

namespace S3Harp.Server.Authentication;

/// <summary>
/// Decodes an <c>aws-chunked</c> request body (<c>STREAMING-AWS4-HMAC-SHA256-PAYLOAD</c>),
/// verifying each chunk's signature against the SigV4 chain before its bytes become
/// readable. With a signed trailer, the checksum the client announced in
/// <c>x-amz-trailer</c> is verified against the decoded payload as well.
/// </summary>
public sealed class SigV4ChunkedStream(
    Stream inner,
    byte[] signingKey,
    CredentialScope scope,
    string timestamp,
    string seedSignature,
    bool signedTrailer = false,
    ChecksumAlgorithm? trailerChecksum = null
) : Stream
{
    private const int MaxHeaderLength = 1024;
    private const int MaxChunkSize = 16 * 1024 * 1024;
    private const int ReadAheadSize = 16 * 1024;

    private static readonly string EmptyHash = SigV4Signer.Sha256Hex([]);

    private static ReadOnlySpan<byte> SignaturePrefix => ";chunk-signature="u8;

    private static ReadOnlySpan<byte> LineEnd => "\r\n"u8;

    private readonly IncrementalChecksum? checksum = trailerChecksum is { } algorithm
        ? ChecksumAlgorithms.Create(algorithm)
        : null;

    private readonly string? checksumTrailer = trailerChecksum is { } named
        ? ChecksumHeaders.HeaderName(named)
        : null;

    /// <summary>Wire bytes read ahead of the decoder; the unconsumed part is [start, end).</summary>
    private readonly byte[] readAhead = ArrayPool<byte>.Shared.Rent(ReadAheadSize);
    private int readAheadStart;
    private int readAheadEnd;

    private string previousSignature = seedSignature;
    private byte[]? chunk;
    private int chunkLength;
    private int positionInChunk;
    private bool finished;

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
        if (
            separator <= 0
            || !Utf8Parser.TryParse(header.Span[..separator], out long size, out var digits, 'x')
            || digits != separator
            || size is < 0 or > MaxChunkSize
        )
        {
            throw new InvalidDataException("The chunk header is malformed.");
        }

        var presentedSignature = Encoding.ASCII.GetString(
            header.Span[(separator + SignaturePrefix.Length)..]
        );
        var data = await ReadChunkDataAsync((int)size, cancellationToken).ConfigureAwait(false);

        var stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256-PAYLOAD",
            timestamp,
            scope.ToString(),
            previousSignature,
            EmptyHash,
            SigV4Signer.Sha256Hex(data.Span)
        );
        var expectedSignature = SigV4Signer.Sign(signingKey, stringToSign);
        if (!SigV4Signer.SignaturesEqual(expectedSignature, presentedSignature))
        {
            throw new PayloadVerificationException(S3Errors.SignatureDoesNotMatch);
        }

        previousSignature = expectedSignature;
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
        await inner
            .ReadExactlyAsync(chunk.AsMemory(buffered, size - buffered), cancellationToken)
            .ConfigureAwait(false);
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

        if (presentedSignature is null)
        {
            throw new PayloadVerificationException(S3Errors.SignatureDoesNotMatch);
        }

        var stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256-TRAILER",
            timestamp,
            scope.ToString(),
            previousSignature,
            SigV4Signer.Sha256Hex(Encoding.UTF8.GetBytes(canonicalTrailer.ToString()))
        );
        if (
            !SigV4Signer.SignaturesEqual(
                SigV4Signer.Sign(signingKey, stringToSign),
                presentedSignature
            )
        )
        {
            throw new PayloadVerificationException(S3Errors.SignatureDoesNotMatch);
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
                throw new InvalidDataException("The chunk header exceeds the supported length.");
            }

            if (await FillReadAheadAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new InvalidDataException("The chunked body ended inside a chunk header.");
            }
        }
    }

    private async Task ConsumeChunkDelimiterAsync(CancellationToken cancellationToken)
    {
        while (readAheadEnd - readAheadStart < LineEnd.Length)
        {
            if (await FillReadAheadAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new InvalidDataException("The chunked body ended inside a chunk.");
            }
        }

        if (!readAhead.AsSpan(readAheadStart, LineEnd.Length).SequenceEqual(LineEnd))
        {
            throw new InvalidDataException("The chunk data is followed by a malformed delimiter.");
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
