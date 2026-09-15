using System.Collections.Concurrent;

namespace S3Harp.Core;

/// <summary>A metadata index held entirely in memory; the fast test double for the seam.</summary>
public sealed class InMemoryMetadataIndex : IMetadataIndex
{
    private readonly ConcurrentDictionary<string, BucketInfo> buckets = new(StringComparer.Ordinal);

    public Task<bool> TryCreateBucketAsync(
        string name, DateTimeOffset createdAt, CancellationToken cancellationToken) =>
        Task.FromResult(buckets.TryAdd(name, new BucketInfo(name, createdAt)));

    public Task<bool> BucketExistsAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(buckets.ContainsKey(name));

    public Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BucketInfo>>(
            [.. buckets.Values.OrderBy(b => b.Name, StringComparer.Ordinal)]);

    public Task<bool> TryDeleteBucketAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(buckets.TryRemove(name, out _));
}
