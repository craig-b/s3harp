namespace S3Harp.Server;

/// <summary>The one line an operator reads first: where the server listens, stores, and whom it serves.</summary>
public static class StartupSummary
{
    public static string Describe(S3HarpOptions options, IEnumerable<string> urls)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(urls);
        return $"S3Harp listening on {string.Join(", ", urls)}, storing data in {options.DataDirectory}, "
            + $"serving buckets under {options.Domain}, access key {options.AccessKeyId}";
    }
}
