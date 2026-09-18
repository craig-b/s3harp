using System.Security.Cryptography;
using S3Harp.Core;

namespace S3Harp.Server.Authentication;

/// <summary>Raised while reading a request body whose content fails verification.</summary>
internal sealed class PayloadVerificationException(S3Error error) : IOException(error.Message)
{
    public S3Error Error { get; } = error;
}

/// <summary>
/// Passes a request body through while observing its bytes, and verifies the
/// content on the final read, once every byte has been seen.
/// </summary>
internal abstract class PayloadVerifyingStream(Stream inner) : Stream
{
    private bool verified;

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
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            Observe(buffer.Span[..read]);
            return read;
        }

        if (!verified)
        {
            verified = true;
            VerifyContent();
        }

        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected abstract void Observe(ReadOnlySpan<byte> data);

    /// <summary>Throws a <see cref="PayloadVerificationException"/> when the content fails.</summary>
    protected abstract void VerifyContent();
}

/// <summary>
/// Fails a request body whose SHA-256 differs from the value the client signed in
/// <c>x-amz-content-sha256</c>.
/// </summary>
internal sealed class Sha256VerifyingStream(Stream inner, string declaredSha256Hex)
    : PayloadVerifyingStream(inner)
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    protected override void Observe(ReadOnlySpan<byte> data) => hash.AppendData(data);

    protected override void VerifyContent()
    {
        var computed = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!SigV4Signer.SignaturesEqual(computed, declaredSha256Hex))
        {
            throw new PayloadVerificationException(S3Errors.XAmzContentSHA256Mismatch);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hash.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Fails a request body whose checksum differs from the value the client declared
/// in its <c>x-amz-checksum-*</c> header.
/// </summary>
internal sealed class ChecksumVerifyingStream(
    Stream inner,
    IncrementalChecksum checksum,
    string declaredBase64
) : PayloadVerifyingStream(inner)
{
    protected override void Observe(ReadOnlySpan<byte> data) => checksum.Append(data);

    protected override void VerifyContent()
    {
        if (!checksum.Matches(declaredBase64))
        {
            throw new PayloadVerificationException(S3Errors.BadDigest);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            checksum.Dispose();
        }

        base.Dispose(disposing);
    }
}
