using System.Diagnostics;
using S3Harp.Core;

namespace S3Harp.Server;

/// <summary>The bucket operations: list, create, head and delete.</summary>
internal sealed partial class S3RequestDispatcher
{
    private async Task<IResult> ListBucketsAsync(CancellationToken cancellationToken)
    {
        var buckets = await index.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
        var document = S3Xml.Element(
            "ListAllMyBucketsResult",
            S3Xml.Element(
                "Owner",
                S3Xml.Element("ID", credentials.AccessKeyId),
                S3Xml.Element("DisplayName", credentials.AccessKeyId)
            ),
            S3Xml.Element(
                "Buckets",
                buckets.Select(bucket =>
                    S3Xml.Element(
                        "Bucket",
                        S3Xml.Element("Name", bucket.Name),
                        S3Xml.Element("CreationDate", FormatTimestamp(bucket.CreatedAt))
                    )
                )
            )
        );
        return new S3XmlResult(StatusCodes.Status200OK, document);
    }

    private async Task<IResult> CreateBucketAsync(
        HttpContext context,
        string bucket,
        CancellationToken cancellationToken
    )
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

    private async Task<IResult> HeadBucketAsync(
        string bucket,
        CancellationToken cancellationToken
    ) =>
        await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false)
            ? new S3StatusResult(StatusCodes.Status200OK)
            : new S3ErrorResult(S3Errors.NoSuchBucket);

    private async Task<IResult> DeleteBucketAsync(
        string bucket,
        CancellationToken cancellationToken
    ) =>
        await engine.DeleteBucketAsync(bucket, cancellationToken).ConfigureAwait(false) switch
        {
            DeleteBucketResult.Deleted => new S3StatusResult(StatusCodes.Status204NoContent),
            DeleteBucketResult.NotEmpty => new S3ErrorResult(S3Errors.BucketNotEmpty),
            DeleteBucketResult.NotFound => new S3ErrorResult(S3Errors.NoSuchBucket),
            _ => throw new UnreachableException(),
        };
}
