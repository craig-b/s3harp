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

    public async Task<DeleteBucketResult> DeleteBucketAsync(
        string name, CancellationToken cancellationToken)
    {
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM buckets WHERE name = $name";
            command.Parameters.AddWithValue("$name", name);
            try
            {
                var deleted = await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                return deleted == 1 ? DeleteBucketResult.Deleted : DeleteBucketResult.NotFound;
            }
            catch (SqliteException exception)
                when (exception.SqliteErrorCode == SqliteConstraintViolation)
            {
                return DeleteBucketResult.NotEmpty;
            }
        }
    }

    public async Task<PutObjectResult> PutObjectAsync(
        string bucket, ObjectRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var transaction = connection.BeginTransaction();
            await using (transaction.ConfigureAwait(false))
            {
                var find = connection.CreateCommand();
                find.Transaction = transaction;
                find.CommandText =
                    "SELECT blob_id FROM objects WHERE bucket = $bucket AND key = $key";
                find.Parameters.AddWithValue("$bucket", bucket);
                find.Parameters.AddWithValue("$key", record.Key);
                var replaced = (string?)await find.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);

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
                upsert.Parameters.AddWithValue(
                    "$content_type", (object?)record.ContentType ?? DBNull.Value);
                upsert.Parameters.AddWithValue(
                    "$metadata", JsonSerializer.Serialize(record.Metadata));
                upsert.Parameters.AddWithValue(
                    "$last_modified", FormatTimestamp(record.LastModified));
                try
                {
                    await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException exception)
                    when (exception.SqliteErrorCode == SqliteConstraintViolation)
                {
                    return new PutObjectResult(BucketExists: false, null);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PutObjectResult(BucketExists: true, replaced);
            }
        }
    }

    public async Task<ObjectRecord?> FindObjectAsync(
        string bucket, string key, CancellationToken cancellationToken)
    {
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
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

    public void Dispose() => SqliteConnection.ClearPool(new SqliteConnection(connectionString));

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
