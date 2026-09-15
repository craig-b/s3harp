namespace S3Harp.Core;

/// <summary>A metadata index held entirely in memory; the fast test double for the seam.</summary>
public sealed class InMemoryMetadataIndex : IMetadataIndex
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, BucketState> buckets = new(StringComparer.Ordinal);

    public Task<bool> TryCreateBucketAsync(
        string name, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(
                buckets.TryAdd(name, new BucketState(new BucketInfo(name, createdAt))));
        }
    }

    public Task<bool> BucketExistsAsync(string name, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(buckets.ContainsKey(name));
        }
    }

    public Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<BucketInfo>>(
                [.. buckets.Values.Select(b => b.Info).OrderBy(b => b.Name, StringComparer.Ordinal)]);
        }
    }

    public Task<DeleteBucketResult> DeleteBucketAsync(
        string name, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!buckets.TryGetValue(name, out var bucket))
            {
                return Task.FromResult(DeleteBucketResult.NotFound);
            }

            if (bucket.Objects.Count > 0)
            {
                return Task.FromResult(DeleteBucketResult.NotEmpty);
            }

            buckets.Remove(name);
            return Task.FromResult(DeleteBucketResult.Deleted);
        }
    }

    public Task<PutObjectResult> PutObjectAsync(
        string bucket, ObjectRecord record, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!buckets.TryGetValue(bucket, out var state))
            {
                return Task.FromResult(new PutObjectResult(BucketExists: false, null));
            }

            state.Objects.TryGetValue(record.Key, out var replaced);
            state.Objects[record.Key] = record;
            return Task.FromResult(new PutObjectResult(BucketExists: true, replaced?.BlobId));
        }
    }

    public Task<ObjectRecord?> FindObjectAsync(
        string bucket, string key, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(
                buckets.TryGetValue(bucket, out var state)
                && state.Objects.TryGetValue(key, out var record)
                    ? record
                    : null);
        }
    }

    public Task<string?> DeleteObjectAsync(
        string bucket, string key, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (buckets.TryGetValue(bucket, out var state)
                && state.Objects.Remove(key, out var record))
            {
                return Task.FromResult<string?>(record.BlobId);
            }

            return Task.FromResult<string?>(null);
        }
    }

    private sealed class BucketState(BucketInfo info)
    {
        public BucketInfo Info { get; } = info;

        public Dictionary<string, ObjectRecord> Objects { get; } = new(StringComparer.Ordinal);
    }
}
