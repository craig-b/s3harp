using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace S3Harp.Core;

/// <summary>The durable metadata index, backed by a SQLite database file.</summary>
public sealed class SqliteMetadataIndex : IMetadataIndex, IDisposable
{
    private const int SqliteConstraintViolation = 19;

    private readonly string connectionString;

    public SqliteMetadataIndex(string databasePath)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
        }.ToString();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS buckets (
                name TEXT PRIMARY KEY,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS objects (
                bucket TEXT NOT NULL REFERENCES buckets(name),
                key TEXT NOT NULL,
                blob_id TEXT NOT NULL,
                size INTEGER NOT NULL,
                etag TEXT NOT NULL,
                content_type TEXT,
                metadata TEXT NOT NULL,
                last_modified TEXT NOT NULL,
                PRIMARY KEY (bucket, key)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS uploads (
                upload_id TEXT PRIMARY KEY,
                bucket TEXT NOT NULL REFERENCES buckets(name),
                key TEXT NOT NULL,
                content_type TEXT,
                metadata TEXT NOT NULL,
                initiated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS parts (
                upload_id TEXT NOT NULL REFERENCES uploads(upload_id),
                part_number INTEGER NOT NULL,
                blob_id TEXT NOT NULL,
                size INTEGER NOT NULL,
                etag TEXT NOT NULL,
                PRIMARY KEY (upload_id, part_number)
            ) WITHOUT ROWID;
            """;
        command.ExecuteNonQuery();
    }

    public async Task<bool> TryCreateBucketAsync(
        string name, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO buckets (name, created_at) VALUES ($name, $created_at) " +
                "ON CONFLICT (name) DO NOTHING";
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$created_at", FormatTimestamp(createdAt));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
    }

    public async Task<bool> BucketExistsAsync(string name, CancellationToken cancellationToken)
    {
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM buckets WHERE name = $name";
            command.Parameters.AddWithValue("$name", name);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
    }

    public async Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(
        CancellationToken cancellationToken)
    {
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT name, created_at FROM buckets ORDER BY name";
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var buckets = new List<BucketInfo>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    buckets.Add(new BucketInfo(
                        reader.GetString(0), ParseTimestamp(reader.GetString(1))));
                }

                return buckets;
            }
        }
    }

    public async Task<DeleteBucketOutcome> DeleteBucketAsync(
        string name, CancellationToken cancellationToken)
    {
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var transaction = connection.BeginTransaction();
            await using (transaction.ConfigureAwait(false))
            {
                var deleteParts = connection.CreateCommand();
                deleteParts.Transaction = transaction;
                deleteParts.CommandText = """
                    DELETE FROM parts
                    WHERE upload_id IN (SELECT upload_id FROM uploads WHERE bucket = $name)
                    RETURNING blob_id
                    """;
                deleteParts.Parameters.AddWithValue("$name", name);
                var partBlobs = await ReadStringsAsync(deleteParts, cancellationToken)
                    .ConfigureAwait(false);

                var deleteUploads = connection.CreateCommand();
                deleteUploads.Transaction = transaction;
                deleteUploads.CommandText = "DELETE FROM uploads WHERE bucket = $name";
                deleteUploads.Parameters.AddWithValue("$name", name);
                await deleteUploads.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                var deleteBucket = connection.CreateCommand();
                deleteBucket.Transaction = transaction;
                deleteBucket.CommandText = "DELETE FROM buckets WHERE name = $name";
                deleteBucket.Parameters.AddWithValue("$name", name);
                int deleted;
                try
                {
                    deleted = await deleteBucket.ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SqliteException exception)
                    when (exception.SqliteErrorCode == SqliteConstraintViolation)
                {
                    // Objects still reference the bucket; the transaction rolls back
                    // with the uploads intact.
                    return DeleteBucketOutcome.NotEmpty;
                }

                if (deleted == 0)
                {
                    return DeleteBucketOutcome.NotFound;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new DeleteBucketOutcome(DeleteBucketResult.Deleted, partBlobs);
            }
        }
    }

    public async Task<PutObjectResult> PutObjectAsync(
        string bucket, ObjectRecord record, WriteCondition? condition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var transaction = connection.BeginTransaction();
            await using (transaction.ConfigureAwait(false))
            {
                var replaced = await FindObjectAsync(
                    connection, transaction, bucket, record.Key, cancellationToken)
                    .ConfigureAwait(false);
                if (condition?.Check(replaced) is { } refusal
                    && refusal != WriteConditionResult.Satisfied)
                {
                    return PutObjectResult.Refused(refusal);
                }

                try
                {
                    await UpsertObjectAsync(connection, transaction, bucket, record, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SqliteException exception)
                    when (exception.SqliteErrorCode == SqliteConstraintViolation)
                {
                    return PutObjectResult.BucketMissing;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PutObjectResult(PutObjectStatus.Stored, replaced?.BlobId);
            }
        }
    }

    public async Task<ObjectRecord?> FindObjectAsync(
        string bucket, string key, CancellationToken cancellationToken)
    {
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            return await FindObjectAsync(connection, transaction: null, bucket, key, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<ObjectRecord?> FindObjectAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT key, blob_id, size, etag, content_type, metadata, last_modified
            FROM objects WHERE bucket = $bucket AND key = $key
            """;
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$key", key);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadObjectRecord(reader)
                : null;
        }
    }

    public async Task<IReadOnlyList<ObjectRecord>> ScanObjectsAsync(
        string bucket, string prefix, string fromKey, int limit,
        CancellationToken cancellationToken)
    {
        var lowerBound = string.CompareOrdinal(fromKey, prefix) > 0 ? fromKey : prefix;
        var upperBound = KeyRange.PrefixSuccessor(prefix);
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT key, blob_id, size, etag, content_type, metadata, last_modified
                FROM objects
                WHERE bucket = $bucket AND key >= $lower AND ($upper IS NULL OR key < $upper)
                ORDER BY key
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$bucket", bucket);
            command.Parameters.AddWithValue("$lower", lowerBound);
            command.Parameters.AddWithValue("$upper", (object?)upperBound ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var records = new List<ObjectRecord>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    records.Add(ReadObjectRecord(reader));
                }

                return records;
            }
        }
    }

    public async Task<string?> DeleteObjectAsync(
        string bucket, string key, CancellationToken cancellationToken)
    {
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM objects WHERE bucket = $bucket AND key = $key RETURNING blob_id";
            command.Parameters.AddWithValue("$bucket", bucket);
            command.Parameters.AddWithValue("$key", key);
            return (string?)await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<bool> TryCreateUploadAsync(
        string bucket, MultipartUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO uploads (upload_id, bucket, key, content_type, metadata, initiated_at)
                VALUES ($upload_id, $bucket, $key, $content_type, $metadata, $initiated_at)
                """;
            command.Parameters.AddWithValue("$upload_id", upload.UploadId);
            command.Parameters.AddWithValue("$bucket", bucket);
            command.Parameters.AddWithValue("$key", upload.Key);
            command.Parameters.AddWithValue(
                "$content_type", (object?)upload.ContentType ?? DBNull.Value);
            command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(upload.Metadata));
            command.Parameters.AddWithValue("$initiated_at", FormatTimestamp(upload.InitiatedAt));
            try
            {
                return await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false) == 1;
            }
            catch (SqliteException exception)
                when (exception.SqliteErrorCode == SqliteConstraintViolation)
            {
                return false;
            }
        }
    }

    public async Task<MultipartUpload?> FindUploadAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken)
    {
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var command = CreateFindUploadCommand(connection, bucket, key, uploadId);
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? new MultipartUpload(
                        uploadId,
                        key,
                        reader.IsDBNull(0) ? null : reader.GetString(0),
                        JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(1))!,
                        ParseTimestamp(reader.GetString(2)))
                    : null;
            }
        }
    }

    public async Task<PutPartResult> PutPartAsync(
        string bucket, string key, string uploadId, PartRecord part,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);

        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var transaction = connection.BeginTransaction();
            await using (transaction.ConfigureAwait(false))
            {
                if (!await UploadExistsAsync(connection, transaction, bucket, key, uploadId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return new PutPartResult(UploadExists: false, null);
                }

                var find = connection.CreateCommand();
                find.Transaction = transaction;
                find.CommandText =
                    "SELECT blob_id FROM parts WHERE upload_id = $upload_id AND part_number = $number";
                find.Parameters.AddWithValue("$upload_id", uploadId);
                find.Parameters.AddWithValue("$number", part.PartNumber);
                var replaced = (string?)await find.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);

                var upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO parts (upload_id, part_number, blob_id, size, etag)
                    VALUES ($upload_id, $number, $blob_id, $size, $etag)
                    ON CONFLICT (upload_id, part_number) DO UPDATE SET
                        blob_id = excluded.blob_id, size = excluded.size, etag = excluded.etag
                    """;
                upsert.Parameters.AddWithValue("$upload_id", uploadId);
                upsert.Parameters.AddWithValue("$number", part.PartNumber);
                upsert.Parameters.AddWithValue("$blob_id", part.BlobId);
                upsert.Parameters.AddWithValue("$size", part.Size);
                upsert.Parameters.AddWithValue("$etag", part.ETag);
                await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PutPartResult(UploadExists: true, replaced);
            }
        }
    }

    public async Task<IReadOnlyList<PartRecord>> ListPartsAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken)
    {
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p.part_number, p.blob_id, p.size, p.etag
                FROM parts p
                JOIN uploads u ON u.upload_id = p.upload_id
                WHERE u.upload_id = $upload_id AND u.bucket = $bucket AND u.key = $key
                ORDER BY p.part_number
                """;
            command.Parameters.AddWithValue("$upload_id", uploadId);
            command.Parameters.AddWithValue("$bucket", bucket);
            command.Parameters.AddWithValue("$key", key);
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var parts = new List<PartRecord>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    parts.Add(new PartRecord(
                        reader.GetInt32(0), reader.GetString(1),
                        reader.GetInt64(2), reader.GetString(3)));
                }

                return parts;
            }
        }
    }

    public async Task<IReadOnlyList<MultipartUpload>> ListUploadsAsync(
        string bucket, CancellationToken cancellationToken)
    {
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT upload_id, key, content_type, metadata, initiated_at
                FROM uploads WHERE bucket = $bucket
                ORDER BY key, upload_id
                """;
            command.Parameters.AddWithValue("$bucket", bucket);
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var uploads = new List<MultipartUpload>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    uploads.Add(new MultipartUpload(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3))!,
                        ParseTimestamp(reader.GetString(4))));
                }

                return uploads;
            }
        }
    }

    public async Task<CompleteUploadResult> CompleteUploadAsync(
        string bucket, string uploadId, ObjectRecord record, WriteCondition? condition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var transaction = connection.BeginTransaction();
            await using (transaction.ConfigureAwait(false))
            {
                if (!await UploadExistsAsync(
                        connection, transaction, bucket, record.Key, uploadId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return CompleteUploadResult.NoSuchUpload;
                }

                var replaced = await FindObjectAsync(
                    connection, transaction, bucket, record.Key, cancellationToken)
                    .ConfigureAwait(false);
                if (condition?.Check(replaced) is { } refusal
                    && refusal != WriteConditionResult.Satisfied)
                {
                    return CompleteUploadResult.Refused(refusal);
                }

                var partBlobs = await DeleteUploadRowsAsync(
                    connection, transaction, uploadId, cancellationToken).ConfigureAwait(false);
                await UpsertObjectAsync(connection, transaction, bucket, record, cancellationToken)
                    .ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new CompleteUploadResult(
                    CompleteUploadStatus.Completed, replaced?.BlobId, partBlobs);
            }
        }
    }

    public async Task<IReadOnlyList<string>?> DeleteUploadAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken)
    {
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var transaction = connection.BeginTransaction();
            await using (transaction.ConfigureAwait(false))
            {
                if (!await UploadExistsAsync(connection, transaction, bucket, key, uploadId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return null;
                }

                var partBlobs = await DeleteUploadRowsAsync(
                    connection, transaction, uploadId, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return partBlobs;
            }
        }
    }

    public void Dispose() => SqliteConnection.ClearPool(new SqliteConnection(connectionString));

    private static SqliteCommand CreateFindUploadCommand(
        SqliteConnection connection, string bucket, string key, string uploadId)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT content_type, metadata, initiated_at
            FROM uploads
            WHERE upload_id = $upload_id AND bucket = $bucket AND key = $key
            """;
        command.Parameters.AddWithValue("$upload_id", uploadId);
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$key", key);
        return command;
    }

    private static async Task<bool> UploadExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT 1 FROM uploads WHERE upload_id = $upload_id AND bucket = $bucket AND key = $key";
        command.Parameters.AddWithValue("$upload_id", uploadId);
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task UpsertObjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string bucket,
        ObjectRecord record,
        CancellationToken cancellationToken)
    {
        var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO objects
                (bucket, key, blob_id, size, etag, content_type, metadata, last_modified)
            VALUES
                ($bucket, $key, $blob_id, $size, $etag, $content_type, $metadata, $last_modified)
            ON CONFLICT (bucket, key) DO UPDATE SET
                blob_id = excluded.blob_id,
                size = excluded.size,
                etag = excluded.etag,
                content_type = excluded.content_type,
                metadata = excluded.metadata,
                last_modified = excluded.last_modified
            """;
        upsert.Parameters.AddWithValue("$bucket", bucket);
        upsert.Parameters.AddWithValue("$key", record.Key);
        upsert.Parameters.AddWithValue("$blob_id", record.BlobId);
        upsert.Parameters.AddWithValue("$size", record.Size);
        upsert.Parameters.AddWithValue("$etag", record.ETag);
        upsert.Parameters.AddWithValue("$content_type", (object?)record.ContentType ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(record.Metadata));
        upsert.Parameters.AddWithValue("$last_modified", FormatTimestamp(record.LastModified));
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> DeleteUploadRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var deleteParts = connection.CreateCommand();
        deleteParts.Transaction = transaction;
        deleteParts.CommandText =
            "DELETE FROM parts WHERE upload_id = $upload_id RETURNING blob_id";
        deleteParts.Parameters.AddWithValue("$upload_id", uploadId);
        var partBlobs = await ReadStringsAsync(deleteParts, cancellationToken).ConfigureAwait(false);

        var deleteUpload = connection.CreateCommand();
        deleteUpload.Transaction = transaction;
        deleteUpload.CommandText = "DELETE FROM uploads WHERE upload_id = $upload_id";
        deleteUpload.Parameters.AddWithValue("$upload_id", uploadId);
        await deleteUpload.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return partBlobs;
    }

    /// <summary>Runs the command and collects the first column of every row.</summary>
    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        SqliteCommand command, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                values.Add(reader.GetString(0));
            }
        }

        return values;
    }

    private static ObjectRecord ReadObjectRecord(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt64(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5))!,
        ParseTimestamp(reader.GetString(6)));

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
