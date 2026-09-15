using System.Globalization;
using System.Xml.Linq;
using S3Harp.Core;
using S3Harp.Server.Authentication;

namespace S3Harp.Server;

/// <summary>
/// Routes each authenticated request to its S3 operation. S3 dispatches on
/// verb + path shape + query markers, so the operation table lives here as
/// explicit pattern matches.
/// </summary>
public sealed class S3RequestDispatcher(
    IMetadataIndex index, RootCredentials credentials, TimeProvider timeProvider)
{
    private static readonly XNamespace S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    public async Task<IResult> DispatchAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (bucket, key) = ParsePath(context.Request.Path.Value ?? "/");
        var cancellationToken = context.RequestAborted;
        return (context.Request.Method, bucket, key) switch
        {
            ("GET", "", null) => await ListBucketsAsync(cancellationToken).ConfigureAwait(false),
            ("PUT", not "", null) =>
                await CreateBucketAsync(context, bucket, cancellationToken).ConfigureAwait(false),
            ("HEAD", not "", null) =>
                await HeadBucketAsync(bucket, cancellationToken).ConfigureAwait(false),
            ("DELETE", not "", null) =>
                await DeleteBucketAsync(bucket, cancellationToken).ConfigureAwait(false),
            _ => new S3ErrorResult(S3Errors.NotImplemented),
        };
    }

    private static (string Bucket, string? Key) ParsePath(string path)
    {
        var trimmed = path.Trim('/');
        var separator = trimmed.IndexOf('/', StringComparison.Ordinal);
        return separator < 0
            ? (trimmed, null)
            : (trimmed[..separator], trimmed[(separator + 1)..]);
    }

    private async Task<IResult> ListBucketsAsync(CancellationToken cancellationToken)
    {
        var buckets = await index.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", standalone: null),
            new XElement(S3Namespace + "ListAllMyBucketsResult",
                new XElement(S3Namespace + "Owner",
                    new XElement(S3Namespace + "ID", credentials.AccessKeyId),
                    new XElement(S3Namespace + "DisplayName", credentials.AccessKeyId)),
                new XElement(S3Namespace + "Buckets",
                    buckets.Select(bucket => new XElement(S3Namespace + "Bucket",
                        new XElement(S3Namespace + "Name", bucket.Name),
                        new XElement(S3Namespace + "CreationDate", FormatTimestamp(bucket.CreatedAt)))))));
        return new S3XmlResult(StatusCodes.Status200OK, document);
    }

    private async Task<IResult> CreateBucketAsync(
        HttpContext context, string bucket, CancellationToken cancellationToken)
    {
        if (!BucketName.IsValid(bucket))
        {
            return new S3ErrorResult(S3Errors.InvalidBucketName);
        }

        var created = await index
            .TryCreateBucketAsync(bucket, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (!created)
        {
            return new S3ErrorResult(S3Errors.BucketAlreadyOwnedByYou);
        }

        context.Response.Headers.Location = "/" + bucket;
        return new S3StatusResult(StatusCodes.Status200OK);
    }

    private async Task<IResult> HeadBucketAsync(string bucket, CancellationToken cancellationToken) =>
        await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false)
            ? new S3StatusResult(StatusCodes.Status200OK)
            : new S3ErrorResult(S3Errors.NoSuchBucket);

    private async Task<IResult> DeleteBucketAsync(string bucket, CancellationToken cancellationToken) =>
        await index.TryDeleteBucketAsync(bucket, cancellationToken).ConfigureAwait(false)
            ? new S3StatusResult(StatusCodes.Status204NoContent)
            : new S3ErrorResult(S3Errors.NoSuchBucket);

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
