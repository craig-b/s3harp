using System.Text;
using Xunit;

namespace S3Harp.Core.Tests;

public sealed class ObjectListingTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"s3harp-list-{Guid.NewGuid():N}");

    private readonly InMemoryMetadataIndex index = new();
    private readonly StorageEngine engine;

    public ObjectListingTests()
    {
        engine = new StorageEngine(index, new BlobStore(root), new FixedTimeProvider(Now));
    }

    [Fact]
    public async Task WithoutADelimiter_ListsEveryKeyInOrder()
    {
        await Seed();

        var listing = await List();

        Assert.Equal(
            ["a.txt", "docs/one.txt", "docs/two.txt", "photos/2026/pic.jpg", "z.txt"],
            listing.Objects.Select(o => o.Key));
        Assert.Empty(listing.CommonPrefixes);
        Assert.False(listing.IsTruncated);
    }

    [Fact]
    public async Task APrefix_LimitsTheListingToMatchingKeys()
    {
        await Seed();

        var listing = await List(prefix: "docs/");

        Assert.Equal(["docs/one.txt", "docs/two.txt"], listing.Objects.Select(o => o.Key));
    }

    [Fact]
    public async Task ADelimiter_GroupsNestedKeysIntoCommonPrefixes()
    {
        await Seed();

        var listing = await List(delimiter: "/");

        Assert.Equal(["a.txt", "z.txt"], listing.Objects.Select(o => o.Key));
        Assert.Equal(["docs/", "photos/"], listing.CommonPrefixes);
    }

    [Fact]
    public async Task PrefixAndDelimiter_GroupOneLevelBelowThePrefix()
    {
        await Seed();

        var listing = await List(prefix: "photos/", delimiter: "/");

        Assert.Empty(listing.Objects);
        Assert.Equal(["photos/2026/"], listing.CommonPrefixes);
    }

    [Fact]
    public async Task MaxKeys_TruncatesAndTheNextFromKeyResumesWithoutOverlap()
    {
        await Seed();

        var first = await List(delimiter: "/", maxKeys: 2);
        var second = await List(delimiter: "/", maxKeys: 10, fromKey: first.NextFromKey!);

        Assert.True(first.IsTruncated);
        Assert.Equal(["a.txt"], first.Objects.Select(o => o.Key));
        Assert.Equal(["docs/"], first.CommonPrefixes);
        Assert.False(second.IsTruncated);
        Assert.Equal(["z.txt"], second.Objects.Select(o => o.Key));
        Assert.Equal(["photos/"], second.CommonPrefixes);
        Assert.Null(second.NextFromKey);
    }

    [Fact]
    public async Task SingleEntryPages_WalkTheWholeBucket()
    {
        await Seed();
        var entries = new List<string>();
        string fromKey = "";

        while (true)
        {
            var page = await List(delimiter: "/", maxKeys: 1, fromKey: fromKey);
            entries.AddRange(page.Objects.Select(o => o.Key));
            entries.AddRange(page.CommonPrefixes);
            if (!page.IsTruncated)
            {
                break;
            }

            fromKey = page.NextFromKey!;
        }

        Assert.Equal(["a.txt", "docs/", "photos/", "z.txt"], entries);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task Seed()
    {
        Assert.True(await index.TryCreateBucketAsync("alpha", Now, Token));
        foreach (var key in new[]
        {
            "docs/one.txt", "photos/2026/pic.jpg", "a.txt", "z.txt", "docs/two.txt",
        })
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes(key));
            await engine.PutObjectAsync(
                "alpha", key, content, null, new Dictionary<string, string>(), Token);
        }
    }

    private Task<ObjectListing> List(
        string prefix = "", string? delimiter = null, string fromKey = "", int maxKeys = 1000) =>
        engine.ListObjectsAsync("alpha", prefix, delimiter, fromKey, maxKeys, Token);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
