namespace S3Harp.Core;

/// <summary>A bucket known to the index.</summary>
public sealed record BucketInfo(string Name, DateTimeOffset CreatedAt);

/// <summary>
/// The metadata index: the authoritative record of buckets and, as the engine grows,
/// the key → blob mapping. Implementations guarantee atomicity per operation and
/// name-ordered listings.
/// </summary>
public interface IMetadataIndex
{
    /// <summary>Creates the bucket; reports false when the name is already taken.</summary>
    Task<bool> TryCreateBucketAsync(
        string name, DateTimeOffset createdAt, CancellationToken cancellationToken);

    Task<bool> BucketExistsAsync(string name, CancellationToken cancellationToken);

    /// <summary>All buckets, ordered by name (ordinal).</summary>
    Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken);

    /// <summary>Deletes the bucket; reports false when it is unknown.</summary>
    Task<bool> TryDeleteBucketAsync(string name, CancellationToken cancellationToken);
}
