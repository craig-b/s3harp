using System.Buffers;
using System.Security.Cryptography;

namespace S3Harp.Core;

/// <summary>The outcome of writing a blob: its identity, size, and content MD5.</summary>
public sealed record BlobWriteResult(string BlobId, long Size, string ContentMd5Hex);

/// <summary>
/// Stores object data as plain files under the data directory. Blob ids are opaque
/// generated identifiers; every write lands durably via temp file, fsync, and rename.
/// </summary>
public sealed class BlobStore
{
    private const int BufferSize = 64 * 1024;

    private readonly string blobsDirectory;
    private readonly string uploadsDirectory;

    public BlobStore(string rootDirectory)
    {
        blobsDirectory = Path.Combine(rootDirectory, "blobs");
        uploadsDirectory = Path.Combine(rootDirectory, "uploads");
        Directory.CreateDirectory(blobsDirectory);
        Directory.CreateDirectory(uploadsDirectory);
    }

    public async Task<BlobWriteResult> WriteAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var blobId = Guid.NewGuid().ToString("N");
        var uploadPath = Path.Combine(uploadsDirectory, blobId);

        // S3 defines the ETag of a simple upload as the content MD5; the hash is a
        // protocol artifact here, carrying no security claim.
#pragma warning disable CA5351
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
#pragma warning restore CA5351

        try
        {
            return await WriteCoreAsync(content, blobId, uploadPath, md5, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            File.Delete(uploadPath);
            throw;
        }
    }

    private async Task<BlobWriteResult> WriteCoreAsync(
        Stream content,
        string blobId,
        string uploadPath,
        IncrementalHash md5,
        CancellationToken cancellationToken)
    {
        long size = 0;
        var file = new FileStream(
            uploadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            BufferSize, useAsync: true);
        await using (file.ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false)) > 0)
                {
                    md5.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    size += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            file.Flush(flushToDisk: true);
        }

        var finalPath = PathFor(blobId);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        File.Move(uploadPath, finalPath);
        DurableFile.FlushDirectory(Path.GetDirectoryName(finalPath)!);

        return new BlobWriteResult(blobId, size, Convert.ToHexStringLower(md5.GetHashAndReset()));
    }

    public Stream OpenRead(string blobId) => new FileStream(
        PathFor(blobId), FileMode.Open, FileAccess.Read, FileShare.Read,
        BufferSize, useAsync: true);

    public void Delete(string blobId) => File.Delete(PathFor(blobId));

    private string PathFor(string blobId) => Path.Combine(blobsDirectory, blobId[..2], blobId);
}
