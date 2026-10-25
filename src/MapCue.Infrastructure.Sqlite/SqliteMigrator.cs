using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using MapCue.Domain.Graph;
using Microsoft.Data.Sqlite;

namespace MapCue.Infrastructure.Sqlite;

/// <summary>A numbered SQL migration (<c>NNNN_name.sql</c>) with the SHA-256 of its text.</summary>
public sealed record Migration(int Version, string Name, string Sql)
{
    public string Checksum => ContentHash.Sha256(Sql.ReplaceLineEndings("\n"));
}

/// <summary>The schema of the database is newer than this build, or an applied migration was changed.</summary>
public sealed class SchemaMismatchException(string message) : Exception(message);

/// <summary>
/// Applies numbered migrations (ADR-0003). Each migration runs in its own transaction together with its row in
/// <c>schema_migrations</c> (version, name, checksum, applied_at); <c>PRAGMA user_version</c> follows the latest version.
/// A database with an unknown (newer) version or with a changed checksum is refused instead of being "repaired".
/// </summary>
public static partial class SqliteMigrator
{
    /// <summary>Migrations embedded in this assembly, in version order.</summary>
    public static IReadOnlyList<Migration> Embedded { get; } = LoadEmbedded();

    public static int LatestVersion => Embedded[^1].Version;

    /// <summary>Applies pending migrations up to <paramref name="targetVersion"/> (default: all). Returns the versions applied.</summary>
    public static IReadOnlyList<int> Migrate(SqliteConnection connection, IReadOnlyList<Migration>? migrations = null, int? targetVersion = null, string appliedAt = "")
    {
        ArgumentNullException.ThrowIfNull(connection);
        migrations ??= Embedded;
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    INTEGER NOT NULL PRIMARY KEY,
                    name       TEXT NOT NULL,
                    checksum   TEXT NOT NULL,
                    applied_at TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        var applied = new Dictionary<int, (string Name, string Checksum)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT version, name, checksum FROM schema_migrations ORDER BY version";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                applied[reader.GetInt32(0)] = (reader.GetString(1), reader.GetString(2));
            }
        }

        var known = migrations.ToDictionary(m => m.Version);
        foreach (var (version, row) in applied)
        {
            if (!known.TryGetValue(version, out var migration))
            {
                throw new SchemaMismatchException($"Database has migration {version} ({row.Name}) that this MapCue build does not know; use a newer MapCue.");
            }

            if (migration.Checksum != row.Checksum)
            {
                throw new SchemaMismatchException($"Migration {version} ({row.Name}) was changed after it was applied (checksum mismatch).");
            }
        }

        var result = new List<int>();
        foreach (var migration in migrations.Where(m => !applied.ContainsKey(m.Version) && (targetVersion is null || m.Version <= targetVersion)).OrderBy(m => m.Version))
        {
            using var tx = connection.BeginTransaction();
            using (var apply = connection.CreateCommand())
            {
                apply.Transaction = tx;
                apply.CommandText = migration.Sql;
                apply.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = tx;
                record.CommandText = "INSERT INTO schema_migrations (version, name, checksum, applied_at) VALUES ($v, $n, $c, $a)";
                record.Parameters.AddWithValue("$v", migration.Version);
                record.Parameters.AddWithValue("$n", migration.Name);
                record.Parameters.AddWithValue("$c", migration.Checksum);
                record.Parameters.AddWithValue("$a", appliedAt);
                record.ExecuteNonQuery();
            }

            using (var userVersion = connection.CreateCommand())
            {
                userVersion.Transaction = tx;
                userVersion.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {migration.Version}");
                userVersion.ExecuteNonQuery();
            }

            tx.Commit();
            result.Add(migration.Version);
        }

        return result;
    }

    /// <summary>Parses <c>NNNN_name.sql</c> resources; versions must be unique and contiguous from 1.</summary>
    public static IReadOnlyList<Migration> Parse(IEnumerable<(string FileName, string Sql)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var migrations = new List<Migration>();
        foreach (var (fileName, sql) in files)
        {
            var match = MigrationName().Match(fileName);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Migration file '{fileName}' does not match NNNN_name.sql.");
            }

            migrations.Add(new Migration(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value, sql));
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new InvalidOperationException($"Migrations must be numbered 1..n without gaps; found {migrations[i].Version} at position {i + 1}.");
            }
        }

        return migrations;
    }

    private static List<Migration> LoadEmbedded()
    {
        const string prefix = "MapCue.Migrations.";
        var assembly = Assembly.GetExecutingAssembly();
        var files = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (n[prefix.Length..], reader.ReadToEnd());
            });
        return [.. Parse(files)];
    }

    [GeneratedRegex(@"^(\d{4})_([a-z0-9_]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationName();
}
