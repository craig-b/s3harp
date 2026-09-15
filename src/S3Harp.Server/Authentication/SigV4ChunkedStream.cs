using System.Globalization;
using System.Text;

namespace S3Harp.Server.Authentication;

/// <summary>
/// Decodes an <c>aws-chunked</c> request body (<c>STREAMING-AWS4-HMAC-SHA256-PAYLOAD</c>),
/// verifying each chunk's signature against the SigV4 chain before its bytes become readable.
/// </summary>
public sealed class SigV4ChunkedStream(
    Stream inner,
    byte[] signingKey,
    CredentialScope scope,
    string timestamp,
    string seedSignature,
    bool signedTrailer = false) : Stream
{
    private const int MaxHeaderLength = 1024;
    private const long MaxChunkSize = 16 * 1024 * 1024;
    private const string SignaturePrefix = ";chunk-signature=";

    private static readonly string EmptyHash = SigV4Signer.Sha256Hex([]);

    private string previousSignature = seedSignature;
    private byte[] currentChunk = [];
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
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!finished && positionInChunk >= currentChunk.Length)
        {
            await LoadNextChunkAsync(cancellationToken).ConfigureAwait(false);
        }

        if (finished || buffer.Length == 0)
        {
            return 0;
        }

        var count = Math.Min(buffer.Length, currentChunk.Length - positionInChunk);
        currentChunk.AsMemory(positionInChunk, count).CopyTo(buffer);
        positionInChunk += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    private async Task LoadNextChunkAsync(CancellationToken cancellationToken)
    {
        var header = await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false);
        var separator = header.IndexOf(SignaturePrefix, StringComparison.Ordinal);
        if (separator <= 0
            || !long.TryParse(
                header.AsSpan(0, separator), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var size)
            || size is < 0 or > MaxChunkSize)
        {
            throw new InvalidDataException("The chunk header is malformed.");
        }

        var presentedSignature = header[(separator + SignaturePrefix.Length)..];
        var data = new byte[size];
        await inner.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);

        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256-PAYLOAD",
            timestamp,
            scope.ToString(),
            previousSignature,
            EmptyHash,
            SigV4Signer.Sha256Hex(data));
        var expectedSignature = SigV4Signer.Sign(signingKey, stringToSign);
        if (!SigV4Signer.SignaturesEqual(expectedSignature, presentedSignature))
        {
            throw new PayloadVerificationException(S3Errors.SignatureDoesNotMatch);
        }

        previousSignature = expectedSignature;
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
        currentChunk = data;
        positionInChunk = 0;
    }

    private async Task VerifyTrailerAsync(CancellationToken cancellationToken)
    {
        var canonicalTrailer = new StringBuilder();
        string? presentedSignature = null;
        while (await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false) is
            { Length: > 0 } line)
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new PayloadVerificationException(S3Errors.IncompleteBody);
            }

            var name = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (string.Equals(name, "x-amz-trailer-signature", StringComparison.Ordinal))
            {
                presentedSignature = value;
            }
            else
            {
                canonicalTrailer.Append(name).Append(':').Append(value).Append('\n');
            }
        }

        if (presentedSignature is null)
        {
            throw new PayloadVerificationException(S3Errors.SignatureDoesNotMatch);
        }

        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256-TRAILER",
            timestamp,
            scope.ToString(),
            previousSignature,
            SigV4Signer.Sha256Hex(Encoding.UTF8.GetBytes(canonicalTrailer.ToString())));
        if (!SigV4Signer.SignaturesEqual(
                SigV4Signer.Sign(signingKey, stringToSign), presentedSignature))
        {
            throw new PayloadVerificationException(S3Errors.SignatureDoesNotMatch);
        }
    }

    private async Task<string> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var single = new byte[1];
        while (line.Length <= MaxHeaderLength)
        {
            if (await inner.ReadAsync(single, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new InvalidDataException("The chunked body ended inside a chunk header.");
            }

            if (single[0] == '\n' && line.Length > 0 && line[^1] == '\r')
            {
                return line.ToString(0, line.Length - 1);
            }

            line.Append((char)single[0]);
        }

        throw new InvalidDataException("The chunk header exceeds the supported length.");
    }

    private async Task ConsumeChunkDelimiterAsync(CancellationToken cancellationToken)
    {
        var delimiter = new byte[2];
        await inner.ReadExactlyAsync(delimiter, cancellationToken).ConfigureAwait(false);
        if (delimiter[0] != '\r' || delimiter[1] != '\n')
        {
            throw new InvalidDataException("The chunk data is followed by a malformed delimiter.");
        }
    }
}
