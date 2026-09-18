using S3Harp.Core;

namespace S3Harp.Server;

internal enum RangeOutcome
{
    WholeObject,
    Partial,
    Unsatisfiable,
}

/// <summary>A resolved byte range within an object.</summary>
internal readonly record struct RangeEvaluation(RangeOutcome Outcome, long From, long To);

/// <summary>
/// Resolves an HTTP <c>Range</c> header against an object's size, following S3's
/// reading: one satisfiable <c>bytes</c> range is served partially, a syntactically
/// valid range beyond the object is unsatisfiable, and everything else — absent,
/// malformed, or multi-range headers — serves the whole object.
/// </summary>
internal static class RangeHeader
{
    private const string Prefix = "bytes=";

    public static RangeEvaluation Evaluate(string? header, long objectSize)
    {
        if (
            string.IsNullOrEmpty(header)
            || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || header.Contains(',', StringComparison.Ordinal)
        )
        {
            return new RangeEvaluation(RangeOutcome.WholeObject, 0, 0);
        }

        var range = header.AsSpan(Prefix.Length);
        var separator = range.IndexOf('-');
        if (separator < 0)
        {
            return new RangeEvaluation(RangeOutcome.WholeObject, 0, 0);
        }

        var firstPart = range[..separator];
        var secondPart = range[(separator + 1)..];
        if (firstPart.IsEmpty)
        {
            // A suffix range: the last N bytes of the object.
            if (!DecimalDigits.TryParseInt64(secondPart, out var suffixLength))
            {
                return new RangeEvaluation(RangeOutcome.WholeObject, 0, 0);
            }

            return suffixLength == 0 || objectSize == 0
                ? new RangeEvaluation(RangeOutcome.Unsatisfiable, 0, 0)
                : new RangeEvaluation(
                    RangeOutcome.Partial,
                    Math.Max(0, objectSize - suffixLength),
                    objectSize - 1
                );
        }

        if (!DecimalDigits.TryParseInt64(firstPart, out var from))
        {
            return new RangeEvaluation(RangeOutcome.WholeObject, 0, 0);
        }

        var to = objectSize - 1;
        if (!secondPart.IsEmpty)
        {
            if (!DecimalDigits.TryParseInt64(secondPart, out var requestedTo))
            {
                return new RangeEvaluation(RangeOutcome.WholeObject, 0, 0);
            }

            if (requestedTo < from)
            {
                return new RangeEvaluation(RangeOutcome.WholeObject, 0, 0);
            }

            to = Math.Min(requestedTo, objectSize - 1);
        }

        return from >= objectSize
            ? new RangeEvaluation(RangeOutcome.Unsatisfiable, 0, 0)
            : new RangeEvaluation(RangeOutcome.Partial, from, to);
    }
}

/// <summary>
/// Reads the <c>x-amz-copy-source-range</c> header of a part copy, which S3 accepts
/// only as one closed <c>bytes=first-last</c> range; an absent header means the
/// whole source.
/// </summary>
internal static class CopySourceRange
{
    private const string Prefix = "bytes=";

    public static bool TryParse(string? header, out ByteRange? range)
    {
        range = null;
        if (string.IsNullOrEmpty(header))
        {
            return true;
        }

        if (!header.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var bounds = header.AsSpan(Prefix.Length);
        var separator = bounds.IndexOf('-');
        if (
            separator < 0
            || !DecimalDigits.TryParseInt64(bounds[..separator], out var from)
            || !DecimalDigits.TryParseInt64(bounds[(separator + 1)..], out var to)
            || to < from
        )
        {
            return false;
        }

        range = new ByteRange(from, to);
        return true;
    }
}
