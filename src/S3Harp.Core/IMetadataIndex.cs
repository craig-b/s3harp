namespace S3Harp.Core;

/// <summary>A bucket known to the index.</summary>
public sealed record BucketInfo(string Name, DateTimeOffset CreatedAt);

/// <summary>
/// An object's metadata: the key → blob mapping and everything served in headers.
/// <paramref name="PartSizes"/> lists the size of each part, in order, of an object
/// assembled by a multipart upload; it is empty for an object stored in one piece.
/// <paramref name="Checksum"/> is the integrity checksum stored with the object;
/// objects recorded before checksums were kept have none.
/// </summary>
public sealed record ObjectRecord(
    string Key,
    string BlobId,
    long Size,
    string ETag,
    IReadOnlyList<long> PartSizes,
    Checksum? Checksum,
    string? ContentType,
    ContentHeaders ContentHeaders,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset LastModified);

/// <summary>
/// The standard HTTP content headers stored with an object and replayed on
/// every GET and HEAD, alongside its content type.
/// </summary>
public sealed record ContentHeaders(
    string? CacheControl = null,
    string? ContentDisposition = null,
    string? ContentEncoding = null,
    string? ContentLanguage = null,
    string? Expires = null)
{
    public static ContentHeaders None { get; } = new();
}

/// <summary>Names the object at a key by ETag, or any object at the key when the ETag is null.</summary>
public sealed record ETagCondition(string? ETag)
{
    public static ETagCondition AnyObject { get; } = new(ETag: null);

    public bool Matches(ObjectRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return ETag is null || string.Equals(ETag, record.ETag, StringComparison.Ordinal);
    }
}

public enum WriteConditionResult
{
    Satisfied,
    ObjectMissing,
    PreconditionFailed,
}

/// <summary>
/// What must be true of the object already at a key for a write to proceed:
/// an object the write must replace, or one it must not.
/// </summary>
public sealed record WriteCondition(
    ETagCondition? MustMatch = null, ETagCondition? MustNotMatch = null)
{
    public WriteConditionResult Check(ObjectRecord? existing)
    {
        if (MustMatch is not null)
        {
            if (existing is null)
            {
                return WriteConditionResult.ObjectMissing;
            }

            if (!MustMatch.Matches(existing))
            {
                return WriteConditionResult.PreconditionFailed;
            }
        }

        return MustNotMatch is not null && existing is not null && MustNotMatch.Matches(existing)
            ? WriteConditionResult.PreconditionFailed
            : WriteConditionResult.Satisfied;
    }
}

public enum PutObjectStatus
{
    Stored,
    BucketMissing,
    ObjectMissing,
    PreconditionFailed,
}

/// <summary>The outcome of storing an object record.</summary>
public sealed record PutObjectResult(PutObjectStatus Status, string? ReplacedBlobId)
{
    public static PutObjectResult BucketMissing { get; } = new(PutObjectStatus.BucketMissing, null);

    /// <summary>The refusal a failed write condition maps to.</summary>
    public static PutObjectResult Refused(WriteConditionResult condition) => condition switch
    {
        WriteConditionResult.ObjectMissing => new(PutObjectStatus.ObjectMissing, null),
        _ => new(PutObjectStatus.PreconditionFailed, null),
    };
}

/// <summary>
/// What must be true of the object at a key for a delete to remove it: each term
/// given must match the object, its last-modified time to the second.
/// </summary>
public sealed record DeleteCondition(
    string? ETag = null, long? Size = null, DateTimeOffset? LastModified = null)
{
    public bool Matches(ObjectRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return (ETag is null || string.Equals(ETag, record.ETag, StringComparison.Ordinal))
            && (Size is null || Size == record.Size)
            && (LastModified is null
                || LastModified.Value.ToUnixTimeSeconds() == record.LastModified.ToUnixTimeSeconds());
    }
}

public enum DeleteObjectStatus
{
    Deleted,
    NotFound,
    PreconditionFailed,
}

/// <summary>The outcome of deleting an object record: on deletion, the blob id it released.</summary>
public sealed record DeleteObjectResult(DeleteObjectStatus Status, string? BlobId)
{
    public static DeleteObjectResult NotFound { get; } = new(DeleteObjectStatus.NotFound, null);

    public static DeleteObjectResult PreconditionFailed { get; } =
        new(DeleteObjectStatus.PreconditionFailed, null);
}

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
    ContentHeaders ContentHeaders,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset InitiatedAt);

/// <summary>A part uploaded into a multipart upload.</summary>
public sealed record PartRecord(
    int PartNumber, string BlobId, long Size, string ETag, DateTimeOffset LastModified);

/// <summary>The outcome of storing a part record.</summary>
public sealed record PutPartResult(bool UploadExists, string? ReplacedBlobId);

public enum CompleteUploadStatus
{
    Completed,
    NoSuchUpload,
    InvalidPart,
    InvalidPartOrder,
    EntityTooSmall,
    ObjectMissing,
    PreconditionFailed,
}

/// <summary>
/// The outcome of completing an upload: on completion, the blob ids it released —
/// the parts and any object record the completion replaced.
/// </summary>
public sealed record CompleteUploadResult(
    CompleteUploadStatus Status, string? ReplacedBlobId, IReadOnlyList<string> PartBlobIds)
{
    public static CompleteUploadResult NoSuchUpload { get; } =
        new(CompleteUploadStatus.NoSuchUpload, null, []);

    /// <summary>The refusal a failed write condition maps to.</summary>
    public static CompleteUploadResult Refused(WriteConditionResult condition) => condition switch
    {
        WriteConditionResult.ObjectMissing => new(CompleteUploadStatus.ObjectMissing, null, []),
        _ => new(CompleteUploadStatus.PreconditionFailed, null, []),
    };
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

    /// <summary>
    /// Deletes the bucket when it exists and holds zero objects, aborting any
    /// in-progress uploads atomically with the deletion.
    /// </summary>
    Task<DeleteBucketOutcome> DeleteBucketAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the record atomically, replacing any record at the same key when the
    /// condition, if any, holds against that record. The result carries the
    /// replaced record's blob id so its file can be reclaimed.
    /// </summary>
    Task<PutObjectResult> PutObjectAsync(
        string bucket, ObjectRecord record, WriteCondition? condition,
        CancellationToken cancellationToken);

    Task<ObjectRecord?> FindObjectAsync(
        string bucket, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> records whose keys start with the prefix and
    /// order at or above <paramref name="fromKey"/>, in ordinal key order.
    /// </summary>
    Task<IReadOnlyList<ObjectRecord>> ScanObjectsAsync(
        string bucket, string prefix, string fromKey, int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes the record atomically when the condition, if any, holds against it,
    /// returning its blob id so its file can be reclaimed.
    /// </summary>
    Task<DeleteObjectResult> DeleteObjectAsync(
        string bucket, string key, DeleteCondition? condition, CancellationToken cancellationToken);

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
    /// parts, when the condition, if any, holds against the record already at the key.
    /// A refused completion leaves the upload and its parts in place.
    /// </summary>
    Task<CompleteUploadResult> CompleteUploadAsync(
        string bucket, string uploadId, ObjectRecord record, WriteCondition? condition,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes the upload and its parts, returning the part blob ids; null when the
    /// upload is unknown.
    /// </summary>
    Task<IReadOnlyList<string>?> DeleteUploadAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken);
}
