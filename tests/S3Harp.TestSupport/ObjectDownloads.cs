using System.Text;
using S3Harp.Core;
using Xunit;

namespace S3Harp.TestSupport;

public static class ObjectDownloads
{
    /// <summary>The download's content as UTF-8 text; a missing download fails the test.</summary>
    public static async Task<string> ReadContentAsync(
        this ObjectDownload? download,
        CancellationToken cancellationToken
    )
    {
        Assert.NotNull(download);
        await using (download.Content)
        {
            using var buffer = new MemoryStream();
            await download.Content.CopyToAsync(buffer, cancellationToken);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}
