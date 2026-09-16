namespace S3Harp.Server;

/// <summary>
/// The domain S3Harp is served under. A bucket may ride in the host as a label in
/// front of it (<c>bucket.localhost</c>), the way SDKs address S3 by default.
/// </summary>
public sealed record ServiceDomain(string Name)
{
    /// <summary>Every <c>*.localhost</c> name resolves to loopback without any setup.</summary>
    public static ServiceDomain Default { get; } = new("localhost");
}

/// <summary>
/// The bucket and key a request names, in either of S3's addressing styles:
/// virtual-hosted, where the host is <c>bucket.domain</c> and the whole path is
/// the key, or path style, where the bucket leads the path. Only the leading
/// slash of the path is structural: every later character, including a trailing
/// slash, belongs to the key.
/// </summary>
public static class RequestTarget
{
    public static (string Bucket, string? Key) Resolve(string host, string path, string domain)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(domain);
        var withoutRoot = path.StartsWith('/') ? path[1..] : path;
        if (TryBucketFromHost(host, domain, out var hostedBucket))
        {
            return (hostedBucket, KeyOrNull(withoutRoot));
        }

        var separator = withoutRoot.IndexOf('/', StringComparison.Ordinal);
        return separator < 0
            ? (withoutRoot, null)
            : (withoutRoot[..separator], KeyOrNull(withoutRoot[(separator + 1)..]));
    }

    private static bool TryBucketFromHost(string host, string domain, out string bucket)
    {
        var suffix = "." + domain;
        if (host.Length > suffix.Length
            && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            bucket = host[..^suffix.Length];
            return true;
        }

        bucket = "";
        return false;
    }

    private static string? KeyOrNull(string key) => key.Length > 0 ? key : null;
}
