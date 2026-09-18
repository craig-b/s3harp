using System.Text;
using S3Harp.TestSupport;
using Xunit;

namespace S3Harp.Core.Tests;

public sealed class BlobStoreTests : IDisposable
{
    private readonly TempDirectory root = new("blobs");

    private readonly BlobStore store;

    public BlobStoreTests() => store = new BlobStore(root.Path);

    [Fact]
    public async Task WrittenBlob_ReadsBackIdentical()
    {
        var content = "Hello, S3Harp!"u8.ToArray();

        var result = await Write(content);

        using var reader = store.OpenRead(result.BlobId);
        using var decoded = new MemoryStream();
        await reader.CopyToAsync(decoded, Token);
        Assert.Equal(content, decoded.ToArray());
    }

    [Fact]
    public async Task WriteResult_ReportsSizeAndContentMd5()
    {
        var result = await Write("hello world"u8.ToArray());

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
        var first = await Write("Hello, "u8.ToArray());
        var second = await Write("S3Harp!"u8.ToArray());

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
        var original = await Write("copy me"u8.ToArray());

        var copyId = await store.CopyAsync(original.BlobId, Token);

        Assert.NotEqual(original.BlobId, copyId);
        using var reader = store.OpenRead(copyId);
        using var decoded = new MemoryStream();
        await reader.CopyToAsync(decoded, Token);
        Assert.Equal("copy me", Encoding.UTF8.GetString(decoded.ToArray()));
    }

    [Fact]
    public async Task CopiedRange_ReadsBackAsThoseBytesWithTheirDigests()
    {
        var source = await Write("Hello, S3Harp!"u8.ToArray());

        var copied = await store.CopyRangeAsync(
            source.BlobId,
            new ByteRange(7, 13),
            ChecksumAlgorithm.Crc32,
            Token
        );

        using var stream = store.OpenRead(copied.BlobId);
        using var reader = new StreamReader(stream);
        Assert.Equal("S3Harp!", await reader.ReadToEndAsync(Token));
        Assert.Equal(7, copied.Size);
        Assert.Equal(SecondPartMd5, copied.ContentMd5Hex);
        Assert.Equal("0oUPLw==", copied.Checksum);
    }

    [Fact]
    public async Task FailedWrite_LeavesNoFilesBehind()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.WriteAsync(new FailingStream(), ChecksumAlgorithm.Crc64Nvme, Token)
        );

        Assert.Empty(Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories));
    }

    public void Dispose() => root.Dispose();

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

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Write_ReportsTheContentChecksumOfTheRequestedAlgorithm()
    {
        var written = await Write("Hello, S3Harp!"u8.ToArray(), ChecksumAlgorithm.Crc32);

        Assert.Equal("NadAdg==", written.Checksum);
    }

    [Fact]
    public async Task ComputeChecksum_HashesAStoredBlob()
    {
        var written = await Write("Hello, S3Harp!"u8.ToArray());

        var sha256 = await store.ComputeChecksumAsync(
            written.BlobId,
            ChecksumAlgorithm.Sha256,
            Token
        );

        Assert.Equal("Aj0Lx1vWnbGF+irlCT3Pa4HNGctHtn3/Q49ApNekoy8=", sha256);
    }

    /// <summary>The MD5 of "S3Harp!".</summary>
    private const string SecondPartMd5 = "76881423a29bf44fbb150195f6e671ea";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<BlobWriteResult> Write(
        byte[] content,
        ChecksumAlgorithm checksum = ChecksumAlgorithm.Crc64Nvme
    )
    {
        using var stream = new MemoryStream(content);
        return await store.WriteAsync(stream, checksum, Token);
    }
}
