using System.Text;
using S3Harp.TestSupport;
using Xunit;

namespace S3Harp.Core.Tests;

/// <summary>
/// Contention tests against the real SQLite index and blob store: S3's model is
/// per-operation atomicity with last-writer-wins, and the server must hold it
/// without spurious failures or leaked blob files.
/// </summary>
public sealed class ConcurrencyStressTests : IDisposable
{
    private const int WriterCount = 16;

    private readonly TempDirectory root = new("stress");

    private readonly SqliteMetadataIndex index;
    private readonly StorageEngine engine;

    public ConcurrencyStressTests()
    {
        index = new SqliteMetadataIndex(Path.Combine(root.Path, "index.db"));
        engine = new StorageEngine(
            index,
            new BlobStore(root.Path),
            TimeProvider.System,
            new StorageLimits(MinimumPartSize: 1)
        );
    }

    [Fact]
    public async Task ParallelOverwrites_OfOneKey_AllSucceedAndLeaveExactlyOneBlob()
    {
        await CreateBucket();
        var bodies = Enumerable
            .Range(0, WriterCount)
            .Select(i => $"body-{i}-" + new string((char)('a' + i), 512))
            .ToArray();

        var outcomes = await Task.WhenAll(
            bodies.Select(body =>
                Task.Run(
                    async () =>
                    {
                        using var content = new MemoryStream(Encoding.UTF8.GetBytes(body));
                        return await engine.PutObjectAsync(
                            "alpha",
                            "contested",
                            content,
                            new ObjectAttributes(
                                null,
                                ContentHeaders.None,
                                new Dictionary<string, string>()
                            ),
                            ChecksumAlgorithm.Crc64Nvme,
                            null,
                            Token
                        );
                    },
                    Token
                )
            )
        );

        Assert.All(outcomes, outcome => Assert.IsType<PutObjectOutcome.Stored>(outcome));
        var download = await engine.GetObjectAsync("alpha", "contested", Token);
        Assert.NotNull(download);
        Assert.Contains(await download.ReadContentAsync(Token), bodies);
        Assert.Equal(1, root.CountFiles("blobs"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root.Path, "uploads")));
    }

    [Fact]
    public async Task ReadersDuringOverwrites_AlwaysReceiveACompleteBody()
    {
        await CreateBucket();
        var bodies = Enumerable
            .Range(0, 8)
            .Select(i => $"body-{i}-" + new string((char)('a' + i), 2048))
            .ToArray();
        var validBodies = bodies.ToHashSet(StringComparer.Ordinal);
        await Put("readable", bodies[0]);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var writer = Task.Run(
            async () =>
            {
                for (var round = 0; round < 100; round++)
                {
                    await Put("readable", bodies[round % bodies.Length]);
                }

                await stop.CancelAsync();
            },
            Token
        );
        var readers = Enumerable
            .Range(0, 4)
            .Select(_ =>
                Task.Run(
                    async () =>
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            var download = await engine.GetObjectAsync("alpha", "readable", Token);
                            Assert.NotNull(download);
                            Assert.Contains(await download.ReadContentAsync(Token), validBodies);
                        }
                    },
                    Token
                )
            );

        await Task.WhenAll([writer, .. readers]);
    }

    [Fact]
    public async Task ParallelUploads_OfTheSamePartNumber_LeaveExactlyOnePartBlob()
    {
        await CreateBucket();
        var uploadId = await engine.InitiateUploadAsync(
            "alpha",
            "assembled",
            new ObjectAttributes(null, ContentHeaders.None, new Dictionary<string, string>()),
            ChecksumAlgorithm.Crc64Nvme,
            ChecksumType.FullObject,
            Token
        );
        Assert.NotNull(uploadId);

        var outcomes = await Task.WhenAll(
            Enumerable
                .Range(0, WriterCount)
                .Select(i =>
                    Task.Run(
                        async () =>
                        {
                            using var content = new MemoryStream(
                                Encoding.UTF8.GetBytes($"part-body-{i}")
                            );
                            return await engine.UploadPartAsync(
                                "alpha",
                                "assembled",
                                uploadId,
                                1,
                                content,
                                Token
                            );
                        },
                        Token
                    )
                )
        );

        Assert.All(outcomes, outcome => Assert.True(outcome.UploadExists));
        Assert.Single(await index.ListPartsAsync("alpha", "assembled", uploadId, Token));
        Assert.Equal(1, root.CountFiles("blobs"));
    }

    public void Dispose()
    {
        index.Dispose();
        root.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task CreateBucket() =>
        Assert.True(await index.TryCreateBucketAsync("alpha", DateTimeOffset.UtcNow, Token));

    private async Task Put(string key, string body)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var outcome = await engine.PutObjectAsync(
            "alpha",
            key,
            content,
            new ObjectAttributes(null, ContentHeaders.None, new Dictionary<string, string>()),
            ChecksumAlgorithm.Crc64Nvme,
            null,
            Token
        );
        Assert.Equal(PutObjectStatus.Stored, outcome.Status);
    }
}
