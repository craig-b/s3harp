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

    public Task<DeleteBucketOutcome> DeleteBucketAsync(
        string name, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!buckets.TryGetValue(name, out var bucket))
            {
                return Task.FromResult(DeleteBucketOutcome.NotFound);
            }

            if (bucket.Objects.Count > 0)
            {
                return Task.FromResult(DeleteBucketOutcome.NotEmpty);
            }

            buckets.Remove(name);
            var partBlobs = bucket.Uploads.Values
                .SelectMany(upload => upload.Parts.Values)
                .Select(part => part.BlobId)
                .ToList();
            return Task.FromResult(new DeleteBucketOutcome(DeleteBucketResult.Deleted, partBlobs));
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

    public Task<IReadOnlyList<ObjectRecord>> ScanObjectsAsync(
        string bucket, string prefix, string fromKey, int limit,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!buckets.TryGetValue(bucket, out var state))
            {
                return Task.FromResult<IReadOnlyList<ObjectRecord>>([]);
            }

            return Task.FromResult<IReadOnlyList<ObjectRecord>>([.. state.Objects.Values
                .Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)
                    && string.CompareOrdinal(o.Key, fromKey) >= 0)
                .OrderBy(o => o.Key, StringComparer.Ordinal)
                .Take(limit)]);
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

    public Task<bool> TryCreateUploadAsync(
        string bucket, MultipartUpload upload, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(
                buckets.TryGetValue(bucket, out var state)
                && state.Uploads.TryAdd(upload.UploadId, new UploadState(upload)));
        }
    }

    public Task<MultipartUpload?> FindUploadAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(FindUploadState(bucket, key, uploadId)?.Info);
        }
    }

    public Task<PutPartResult> PutPartAsync(
        string bucket, string key, string uploadId, PartRecord part,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (FindUploadState(bucket, key, uploadId) is not { } upload)
            {
                return Task.FromResult(new PutPartResult(UploadExists: false, null));
            }

            upload.Parts.TryGetValue(part.PartNumber, out var replaced);
            upload.Parts[part.PartNumber] = part;
            return Task.FromResult(new PutPartResult(UploadExists: true, replaced?.BlobId));
        }
    }

    public Task<IReadOnlyList<PartRecord>> ListPartsAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<PartRecord>>(
                FindUploadState(bucket, key, uploadId) is { } upload
                    ? [.. upload.Parts.Values]
                    : []);
        }
    }

    public Task<IReadOnlyList<MultipartUpload>> ListUploadsAsync(
        string bucket, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<MultipartUpload>>(
                buckets.TryGetValue(bucket, out var state)
                    ? [.. state.Uploads.Values.Select(u => u.Info)
                        .OrderBy(u => u.Key, StringComparer.Ordinal)
                        .ThenBy(u => u.UploadId, StringComparer.Ordinal)]
                    : []);
        }
    }

    public Task<CompleteUploadResult?> CompleteUploadAsync(
        string bucket, string uploadId, ObjectRecord record,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (FindUploadState(bucket, record.Key, uploadId) is not { } upload
                || !buckets.TryGetValue(bucket, out var state))
            {
                return Task.FromResult<CompleteUploadResult?>(null);
            }

            state.Objects.TryGetValue(record.Key, out var replaced);
            state.Objects[record.Key] = record;
            state.Uploads.Remove(uploadId);
            return Task.FromResult<CompleteUploadResult?>(new CompleteUploadResult(
                replaced?.BlobId, [.. upload.Parts.Values.Select(p => p.BlobId)]));
        }
    }

    public Task<IReadOnlyList<string>?> DeleteUploadAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (FindUploadState(bucket, key, uploadId) is not { } upload
                || !buckets.TryGetValue(bucket, out var state))
            {
                return Task.FromResult<IReadOnlyList<string>?>(null);
            }

            state.Uploads.Remove(uploadId);
            return Task.FromResult<IReadOnlyList<string>?>(
                [.. upload.Parts.Values.Select(p => p.BlobId)]);
        }
    }

    private UploadState? FindUploadState(string bucket, string key, string uploadId) =>
        buckets.TryGetValue(bucket, out var state)
        && state.Uploads.TryGetValue(uploadId, out var upload)
        && string.Equals(upload.Info.Key, key, StringComparison.Ordinal)
            ? upload
            : null;

    private sealed class BucketState(BucketInfo info)
    {
        public BucketInfo Info { get; } = info;

        public Dictionary<string, ObjectRecord> Objects { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, UploadState> Uploads { get; } = new(StringComparer.Ordinal);
    }

    private sealed class UploadState(MultipartUpload info)
    {
        public MultipartUpload Info { get; } = info;

        public SortedDictionary<int, PartRecord> Parts { get; } = [];
    }
}
