using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace S3Harp.Core;

/// <summary>
/// Copies byte ranges between files. On Linux it goes through
/// <c>copy_file_range</c>, which reflink-capable filesystems (btrfs, XFS) satisfy
/// by sharing blocks; everywhere else, and whenever the kernel declines, the same
/// copy happens through buffered reads and writes.
/// </summary>
internal static partial class FileRange
{
    private const int BufferSize = 256 * 1024;

    public static void Copy(
        SafeFileHandle source, long sourceOffset,
        SafeFileHandle destination, long destinationOffset,
        long length)
    {
        while (length > 0 && OperatingSystem.IsLinux())
        {
            var copied = CopyFileRange(
                source, ref sourceOffset, destination, ref destinationOffset,
                (nuint)length, 0);
            if (copied <= 0)
            {
                break;
            }

            length -= copied;
        }

        if (length > 0)
        {
            CopyBuffered(source, sourceOffset, destination, destinationOffset, length);
        }
    }

    private static void CopyBuffered(
        SafeFileHandle source, long sourceOffset,
        SafeFileHandle destination, long destinationOffset,
        long length)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (length > 0)
            {
                var read = RandomAccess.Read(
                    source, buffer.AsSpan(0, (int)Math.Min(length, buffer.Length)), sourceOffset);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "The source blob ended before the requested range was copied.");
                }

                RandomAccess.Write(destination, buffer.AsSpan(0, read), destinationOffset);
                sourceOffset += read;
                destinationOffset += read;
                length -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [LibraryImport("libc", EntryPoint = "copy_file_range", SetLastError = true)]
    private static partial nint CopyFileRange(
        SafeFileHandle fdIn, ref long offsetIn, SafeFileHandle fdOut, ref long offsetOut,
        nuint length, uint flags);
}
