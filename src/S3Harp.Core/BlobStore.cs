using System.Buffers;
using System.Security.Cryptography;

namespace S3Harp.Core;

/// <summary>
/// The outcome of writing a blob: its identity, size, content MD5, and the base64
/// checksum of the algorithm requested, when one was.
/// </summary>
public sealed record BlobWriteResult(string BlobId, long Size, string ContentMd5Hex, string? Checksum);

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

    /// <summary>Writes the content as a new blob, computing the requested checksum as it streams.</summary>
    public async Task<BlobWriteResult> WriteAsync(
        Stream content, ChecksumAlgorithm? checksum, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var blobId = Guid.NewGuid().ToString("N");
        var uploadPath = Path.Combine(uploadsDirectory, blobId);

        // S3 defines the ETag of a simple upload as the content MD5; the hash is a
        // protocol artifact here, carrying no security claim.
#pragma warning disable CA5351
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
#pragma warning restore CA5351
        using var integrity = checksum is { } algorithm ? ChecksumAlgorithms.Create(algorithm) : null;

        try
        {
            return await WriteCoreAsync(content, blobId, uploadPath, md5, integrity, cancellationToken)
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
        IncrementalChecksum? integrity,
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
                    integrity?.Append(buffer.AsSpan(0, read));
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

        Publish(blobId, uploadPath);
        return new BlobWriteResult(
            blobId, size, Convert.ToHexStringLower(md5.GetHashAndReset()),
            integrity is null ? null : Convert.ToBase64String(integrity.Finish()));
    }

    /// <summary>The base64 checksum of a stored blob's content.</summary>
    public async Task<string> ComputeChecksumAsync(
        string blobId, ChecksumAlgorithm algorithm, CancellationToken cancellationToken)
    {
        using var integrity = ChecksumAlgorithms.Create(algorithm);
        var file = OpenRead(blobId);
        await using (file.ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                int read;
                while ((read = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    integrity.Append(buffer.AsSpan(0, read));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return Convert.ToBase64String(integrity.Finish());
    }

    private void Publish(string blobId, string uploadPath)
    {
        var finalPath = PathFor(blobId);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        File.Move(uploadPath, finalPath);
        DurableFile.FlushDirectory(Path.GetDirectoryName(finalPath)!);
    }

    /// <summary>The outcome of assembling blobs into one: the new blob and its size.</summary>
    public sealed record BlobConcatResult(string BlobId, long Size);

    /// <summary>
    /// Assembles the blobs, in order, into a new blob. On reflink-capable
    /// filesystems the parts' blocks are shared rather than rewritten.
    /// </summary>
    public Task<BlobConcatResult> ConcatenateAsync(
        IReadOnlyList<string> blobIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blobIds);
        return Task.Run(() => Concatenate(blobIds, cancellationToken), cancellationToken);
    }

    private BlobConcatResult Concatenate(
        IReadOnlyList<string> blobIds, CancellationToken cancellationToken)
    {
        var blobId = Guid.NewGuid().ToString("N");
        var uploadPath = Path.Combine(uploadsDirectory, blobId);
        try
        {
            long size = 0;
            using (var destination = File.OpenHandle(
                uploadPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                foreach (var sourceId in blobIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var source = File.OpenHandle(PathFor(sourceId), options: FileOptions.None);
                    var length = RandomAccess.GetLength(source);
                    FileRange.Copy(source, 0, destination, size, length);
                    size += length;
                }

                RandomAccess.FlushToDisk(destination);
            }

            Publish(blobId, uploadPath);
            return new BlobConcatResult(blobId, size);
        }
        catch
        {
            File.Delete(uploadPath);
            throw;
        }
    }

    /// <summary>
    /// Copies a blob under a new id. The runtime's file copy uses block cloning
    /// where the filesystem offers it.
    /// </summary>
    public async Task<string> CopyAsync(string blobId, CancellationToken cancellationToken)
    {
        var newBlobId = Guid.NewGuid().ToString("N");
        var uploadPath = Path.Combine(uploadsDirectory, newBlobId);
        try
        {
            await Task.Run(
                () =>
                {
                    File.Copy(PathFor(blobId), uploadPath);
                    using var handle = File.OpenHandle(uploadPath, access: FileAccess.ReadWrite);
                    RandomAccess.FlushToDisk(handle);
                },
                cancellationToken).ConfigureAwait(false);
            Publish(newBlobId, uploadPath);
            return newBlobId;
        }
        catch
        {
            File.Delete(uploadPath);
            throw;
        }
    }

    public Stream OpenRead(string blobId) => new FileStream(
        PathFor(blobId), FileMode.Open, FileAccess.Read, FileShare.Read,
        BufferSize, useAsync: true);

    public void Delete(string blobId) => File.Delete(PathFor(blobId));

    private string PathFor(string blobId) => Path.Combine(blobsDirectory, blobId[..2], blobId);
}
