using System.Text;
using Xunit;

namespace S3Harp.Core.Tests;

public sealed class BlobStoreTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"s3harp-blobs-{Guid.NewGuid():N}");

    private readonly BlobStore store;

    public BlobStoreTests()
    {
        store = new BlobStore(root);
    }

    [Fact]
    public async Task WrittenBlob_ReadsBackIdentical()
    {
        var content = Encoding.UTF8.GetBytes("Hello, S3Harp!");

        var result = await Write(content);

        using var reader = store.OpenRead(result.BlobId);
        using var decoded = new MemoryStream();
        await reader.CopyToAsync(decoded, Token);
        Assert.Equal(content, decoded.ToArray());
    }

    [Fact]
    public async Task WriteResult_ReportsSizeAndContentMd5()
    {
        var result = await Write(Encoding.UTF8.GetBytes("hello world"));

        Assert.Equal(11, result.Size);
        Assert.Equal("5eb63bbbe01eeed093cb22bb8f5acdc3", result.ContentMd5Hex);
    }

    [Fact]
    public async Task EveryWrite_ReceivesItsOwnBlobId()
    {
        var first = await Write([1, 2, 3]);
        var second = await Write([1, 2, 3]);

        Assert.NotEqual(first.BlobId, second.BlobId);
    }

    [Fact]
    public async Task DeletedBlob_IsGone()
    {
        var result = await Write([1, 2, 3]);

        store.Delete(result.BlobId);

        Assert.Throws<FileNotFoundException>(() => store.OpenRead(result.BlobId));
    }

    [Fact]
    public async Task ConcatenatedBlobs_ReadBackAsTheJoinedContent()
    {
        var first = await Write(Encoding.UTF8.GetBytes("Hello, "));
        var second = await Write(Encoding.UTF8.GetBytes("S3Harp!"));

        var result = await store.ConcatenateAsync([first.BlobId, second.BlobId], Token);

        Assert.Equal(14, result.Size);
        using var reader = store.OpenRead(result.BlobId);
        using var decoded = new MemoryStream();
        await reader.CopyToAsync(decoded, Token);
        Assert.Equal("Hello, S3Harp!", Encoding.UTF8.GetString(decoded.ToArray()));
    }

    [Fact]
    public async Task CopiedBlob_ReadsBackIdenticalUnderANewId()
    {
        var original = await Write(Encoding.UTF8.GetBytes("copy me"));

        var copyId = await store.CopyAsync(original.BlobId, Token);

        Assert.NotEqual(original.BlobId, copyId);
        using var reader = store.OpenRead(copyId);
        using var decoded = new MemoryStream();
        await reader.CopyToAsync(decoded, Token);
        Assert.Equal("copy me", Encoding.UTF8.GetString(decoded.ToArray()));
    }

    [Fact]
    public async Task FailedWrite_LeavesNoFilesBehind()
    {
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.WriteAsync(new FailingStream(), Token));

        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidDataException("The payload failed verification.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<BlobWriteResult> Write(byte[] content)
    {
        using var stream = new MemoryStream(content);
        return await store.WriteAsync(stream, Token);
    }
}
