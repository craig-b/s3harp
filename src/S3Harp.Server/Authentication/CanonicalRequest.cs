using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace S3Harp.Server.Authentication;

/// <summary>Builds the SigV4 canonical request string for an incoming HTTP request.</summary>
internal static class CanonicalRequest
{
    public static string Build(
        HttpRequest request,
        IReadOnlyList<string> signedHeaders,
        string payloadHash,
        bool omitSignatureParameter = false
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(signedHeaders);

        var (path, query) = SplitRawTarget(request);
        var builder = new StringBuilder()
            .Append(request.Method)
            .Append('\n')
            .Append(path)
            .Append('\n')
            .Append(CanonicalizeQuery(query, omitSignatureParameter))
            .Append('\n');

        var orderedHeaders = signedHeaders.Order(StringComparer.Ordinal).ToArray();
        foreach (var name in orderedHeaders)
        {
            builder
                .Append(name)
                .Append(':')
                .Append(CanonicalizeHeaderValue(request.Headers[name]))
                .Append('\n');
        }

        return builder
            .Append('\n')
            .AppendJoin(';', orderedHeaders)
            .Append('\n')
            .Append(payloadHash)
            .ToString();
    }

    private static (string Path, string Query) SplitRawTarget(HttpRequest request)
    {
        // The raw target preserves the exact bytes the client signed; the parsed
        // Path property has already been decoded and would re-sign differently.
        var rawTarget = request
            .HttpContext.Features.GetRequiredFeature<IHttpRequestFeature>()
            .RawTarget;
        if (string.IsNullOrEmpty(rawTarget))
        {
            return (
                request.PathBase.Add(request.Path).ToUriComponent(),
                request.QueryString.Value?.TrimStart('?') ?? string.Empty
            );
        }

        var separator = rawTarget.IndexOf('?', StringComparison.Ordinal);
        return separator < 0
            ? (rawTarget, string.Empty)
            : (rawTarget[..separator], rawTarget[(separator + 1)..]);
    }

    private static string CanonicalizeQuery(string query, bool omitSignatureParameter)
    {
        var parameters = new List<(string Name, string Value)>();
        var span = query.AsSpan();
        foreach (var range in span.Split('&'))
        {
            var parameter = span[range];
            if (parameter.IsEmpty)
            {
                continue;
            }

            var separator = parameter.IndexOf('=');
            var name = separator < 0 ? parameter : parameter[..separator];
            if (omitSignatureParameter && name is "X-Amz-Signature")
            {
                continue;
            }

            var value = separator < 0 ? [] : parameter[(separator + 1)..];
            parameters.Add((name.ToString(), value.ToString()));
        }

        parameters.Sort(
            static (left, right) =>
                string.CompareOrdinal(left.Name, right.Name) is var byName and not 0
                    ? byName
                    : string.CompareOrdinal(left.Value, right.Value)
        );

        var builder = new StringBuilder(query.Length);
        foreach (var (name, value) in parameters)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(name).Append('=').Append(value);
        }

        return builder.ToString();
    }

    private static string CanonicalizeHeaderValue(StringValues values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append(CollapseWhitespace(value.AsSpan().Trim()));
        }

        return builder.ToString();
    }

    /// <summary>Runs of spaces and tabs become one space, as SigV4 canonicalization requires.</summary>
    private static string CollapseWhitespace(ReadOnlySpan<char> value)
    {
        if (!value.Contains('\t') && !value.Contains("  ", StringComparison.Ordinal))
        {
            return value.ToString();
        }

        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var character in value)
        {
            var isSpace = character is ' ' or '\t';
            if (!isSpace)
            {
                builder.Append(character);
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
            }

            previousWasSpace = isSpace;
        }

        return builder.ToString();
    }
}
