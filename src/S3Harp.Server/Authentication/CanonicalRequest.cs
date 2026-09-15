using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace S3Harp.Server.Authentication;

/// <summary>Builds the SigV4 canonical request string for an incoming HTTP request.</summary>
public static class CanonicalRequest
{
    public static string Build(
        HttpRequest request, IReadOnlyList<string> signedHeaders, string payloadHash)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(signedHeaders);

        var (path, query) = SplitRawTarget(request);
        var builder = new StringBuilder()
            .Append(request.Method).Append('\n')
            .Append(path).Append('\n')
            .Append(CanonicalizeQuery(query)).Append('\n');

        var orderedHeaders = signedHeaders.OrderBy(h => h, StringComparer.Ordinal).ToArray();
        foreach (var name in orderedHeaders)
        {
            builder.Append(name).Append(':')
                .Append(CanonicalizeHeaderValue(request.Headers[name])).Append('\n');
        }

        return builder.Append('\n')
            .AppendJoin(';', orderedHeaders).Append('\n')
            .Append(payloadHash)
            .ToString();
    }

    private static (string Path, string Query) SplitRawTarget(HttpRequest request)
    {
        // The raw target preserves the exact bytes the client signed; the parsed
        // Path property has already been decoded and would re-sign differently.
        var rawTarget = request.HttpContext.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget;
        if (string.IsNullOrEmpty(rawTarget))
        {
            return (request.PathBase.Add(request.Path).ToUriComponent(),
                request.QueryString.Value?.TrimStart('?') ?? string.Empty);
        }

        var separator = rawTarget.IndexOf('?', StringComparison.Ordinal);
        return separator < 0
            ? (rawTarget, string.Empty)
            : (rawTarget[..separator], rawTarget[(separator + 1)..]);
    }

    private static string CanonicalizeQuery(string query)
    {
        var parameters = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter =>
            {
                var separator = parameter.IndexOf('=', StringComparison.Ordinal);
                return separator < 0
                    ? (Name: parameter, Value: string.Empty)
                    : (Name: parameter[..separator], Value: parameter[(separator + 1)..]);
            })
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value}");
        return string.Join('&', parameters);
    }

    private static string CanonicalizeHeaderValue(StringValues values) =>
        string.Join(',', values.Select(v => CollapseWhitespace(v?.Trim() ?? string.Empty)));

    private static string CollapseWhitespace(string value)
    {
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
