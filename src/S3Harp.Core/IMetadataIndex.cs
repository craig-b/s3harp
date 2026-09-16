namespace S3Harp.Core;

/// <summary>A bucket known to the index.</summary>
public sealed record BucketInfo(string Name, DateTimeOffset CreatedAt);

/// <summary>An object's metadata: the key → blob mapping and everything served in headers.</summary>
public sealed record ObjectRecord(
    string Key,
    string BlobId,
    long Size,
    string ETag,
    string? ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset LastModified);

/// <summary>The outcome of storing an object record.</summary>
public sealed record PutObjectResult(bool BucketExists, string? ReplacedBlobId);

public enum DeleteBucketResult
{
    Deleted,
    NotFound,
    NotEmpty,
}

/// <summary>
/// The metadata index: the authoritative record of buckets and the key → blob mapping.
/// Implementations guarantee atomicity per operation and name-ordered listings.
/// </summary>
public interface IMetadataIndex
{
    /// <summary>Creates the bucket; reports false when the name is already taken.</summary>
    Task<bool> TryCreateBucketAsync(
        string name, DateTimeOffset createdAt, CancellationToken cancellationToken);

    Task<bool> BucketExistsAsync(string name, CancellationToken cancellationToken);

    /// <summary>All buckets, ordered by name (ordinal).</summary>
    Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken);

    /// <summary>Deletes the bucket when it exists and holds zero objects.</summary>
    Task<DeleteBucketResult> DeleteBucketAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the record atomically, replacing any record at the same key.
    /// The result carries the replaced record's blob id so its file can be reclaimed.
    /// </summary>
    Task<PutObjectResult> PutObjectAsync(
        string bucket, ObjectRecord record, CancellationToken cancellationToken);

    Task<ObjectRecord?> FindObjectAsync(
        string bucket, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> records whose keys start with the prefix and
    /// order at or above <paramref name="fromKey"/>, in ordinal key order.
    /// </summary>
    Task<IReadOnlyList<ObjectRecord>> ScanObjectsAsync(
        string bucket, string prefix, string fromKey, int limit,
        CancellationToken cancellationToken);

    /// <summary>Removes the record, returning its blob id; null when the key is unknown.</summary>
    Task<string?> DeleteObjectAsync(
        string bucket, string key, CancellationToken cancellationToken);
}
