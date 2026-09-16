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
/// The outcome of deleting a bucket: on success, the part blob ids of the
/// in-progress uploads the deletion aborted, so their files can be reclaimed.
/// </summary>
public sealed record DeleteBucketOutcome(
    DeleteBucketResult Status, IReadOnlyList<string> ReleasedBlobIds)
{
    public static DeleteBucketOutcome NotFound { get; } = new(DeleteBucketResult.NotFound, []);

    public static DeleteBucketOutcome NotEmpty { get; } = new(DeleteBucketResult.NotEmpty, []);
}

/// <summary>An in-progress multipart upload.</summary>
public sealed record MultipartUpload(
    string UploadId,
    string Key,
    string? ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset InitiatedAt);

/// <summary>A part uploaded into a multipart upload.</summary>
public sealed record PartRecord(int PartNumber, string BlobId, long Size, string ETag);

/// <summary>The outcome of storing a part record.</summary>
public sealed record PutPartResult(bool UploadExists, string? ReplacedBlobId);

/// <summary>
/// The outcome of completing an upload: the blob ids the completion released —
/// the parts and any object record the completion replaced.
/// </summary>
public sealed record CompleteUploadResult(
    string? ReplacedBlobId, IReadOnlyList<string> PartBlobIds);

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

    /// <summary>
    /// Deletes the bucket when it exists and holds zero objects, aborting any
    /// in-progress uploads atomically with the deletion.
    /// </summary>
    Task<DeleteBucketOutcome> DeleteBucketAsync(string name, CancellationToken cancellationToken);

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

    /// <summary>Registers the upload; reports false when the bucket is unknown.</summary>
    Task<bool> TryCreateUploadAsync(
        string bucket, MultipartUpload upload, CancellationToken cancellationToken);

    Task<MultipartUpload?> FindUploadAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken);

    /// <summary>Stores the part atomically, replacing any part with the same number.</summary>
    Task<PutPartResult> PutPartAsync(
        string bucket, string key, string uploadId, PartRecord part,
        CancellationToken cancellationToken);

    /// <summary>The upload's parts, ordered by part number.</summary>
    Task<IReadOnlyList<PartRecord>> ListPartsAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken);

    /// <summary>The bucket's in-progress uploads, ordered by key then upload id.</summary>
    Task<IReadOnlyList<MultipartUpload>> ListUploadsAsync(
        string bucket, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically stores the assembled object record and removes the upload with its
    /// parts; null when the upload is unknown.
    /// </summary>
    Task<CompleteUploadResult?> CompleteUploadAsync(
        string bucket, string uploadId, ObjectRecord record,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes the upload and its parts, returning the part blob ids; null when the
    /// upload is unknown.
    /// </summary>
    Task<IReadOnlyList<string>?> DeleteUploadAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken);
}
