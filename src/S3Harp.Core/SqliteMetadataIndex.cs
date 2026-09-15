using System.Globalization;
using Microsoft.Data.Sqlite;

namespace S3Harp.Core;

/// <summary>The durable metadata index, backed by a SQLite database file.</summary>
public sealed class SqliteMetadataIndex : IMetadataIndex, IDisposable
{
    private readonly string connectionString;

    public SqliteMetadataIndex(string databasePath)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS buckets (
                name TEXT PRIMARY KEY,
                created_at TEXT NOT NULL
            );
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
            command.Parameters.AddWithValue(
                "$created_at", createdAt.ToString("O", CultureInfo.InvariantCulture));
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
                        reader.GetString(0),
                        DateTimeOffset.Parse(
                            reader.GetString(1), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind)));
                }

                return buckets;
            }
        }
    }

    public async Task<bool> TryDeleteBucketAsync(string name, CancellationToken cancellationToken)
    {
        var connection = OpenConnection();
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM buckets WHERE name = $name";
            command.Parameters.AddWithValue("$name", name);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
    }

    public void Dispose() => SqliteConnection.ClearPool(new SqliteConnection(connectionString));

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
