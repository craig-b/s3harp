using System.Runtime.InteropServices;

namespace S3Harp.Core;

/// <summary>Filesystem durability helpers beyond what System.IO exposes.</summary>
internal static partial class DurableFile
{
    /// <summary>
    /// On Linux, fsyncs a directory so a just-renamed entry survives power loss.
    /// Other platforms rely on their own rename durability semantics.
    /// </summary>
    public static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var descriptor = Open(path, ReadOnlyFlag);
        if (descriptor < 0)
        {
            return;
        }

        _ = Fsync(descriptor);
        _ = Close(descriptor);
    }

    private const int ReadOnlyFlag = 0;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync")]
    private static partial int Fsync(int fileDescriptor);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fileDescriptor);
}
