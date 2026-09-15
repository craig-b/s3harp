using System.Security.Cryptography;

namespace S3Harp.Server.Authentication;

/// <summary>Raised while reading a request body whose content fails verification.</summary>
public sealed class PayloadVerificationException(S3Error error) : IOException(error.Message)
{
    public S3Error Error { get; } = error;
}

/// <summary>
/// Passes a request body through while hashing it, and fails the final read when the
/// content's SHA-256 differs from the value the client signed in
/// <c>x-amz-content-sha256</c>.
/// </summary>
public sealed class Sha256VerifyingStream(Stream inner, string declaredSha256Hex) : Stream
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            hash.AppendData(buffer.Span[..read]);
            return read;
        }

        if (!verified)
        {
            verified = true;
            var computed = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!SigV4Signer.SignaturesEqual(computed, declaredSha256Hex))
            {
                throw new PayloadVerificationException(S3Errors.XAmzContentSHA256Mismatch);
            }
        }

        return 0;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
