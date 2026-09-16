using S3Harp.Core;

namespace S3Harp.Server;

/// <summary>
/// Resolves a <c>partNumber</c> query against an object's stored part layout,
/// following S3's reading: a part of a multipart object is served as the byte
/// range it occupies, part 1 of an object stored in one piece is the whole
/// object, and any other part number names a part the object does not have.
/// </summary>
public static class ObjectPart
{
    /// <summary>The range the part occupies; null when the object has no such part.</summary>
    public static RangeEvaluation? Select(int partNumber, ObjectRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var sizes = record.PartSizes;
        if (sizes.Count == 0)
        {
            return partNumber == 1 ? new RangeEvaluation(RangeOutcome.WholeObject, 0, 0) : null;
        }

        if (partNumber < 1 || partNumber > sizes.Count)
        {
            return null;
        }

        long from = 0;
        for (var i = 0; i < partNumber - 1; i++)
        {
            from += sizes[i];
        }

        return new RangeEvaluation(RangeOutcome.Partial, from, from + sizes[partNumber - 1] - 1);
    }
}
