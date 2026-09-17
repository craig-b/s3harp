namespace S3Harp.TestSupport;

/// <summary>A fresh directory under the system temp path that disappears with the test.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string purpose)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"s3harp-{purpose}-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>The number of files under a subdirectory, or 0 when it does not exist yet.</summary>
    public int CountFiles(string subdirectory)
    {
        var directory = System.IO.Path.Combine(Path, subdirectory);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count()
            : 0;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
