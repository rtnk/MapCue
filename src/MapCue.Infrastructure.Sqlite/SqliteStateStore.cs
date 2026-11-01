using MapCue.Application.Ports;
using Microsoft.Data.Sqlite;

namespace MapCue.Infrastructure.Sqlite;

/// <summary>
/// <see cref="IStateStore"/> on SQLite (ADR-0003): WAL, <c>foreign_keys=ON</c>, <c>synchronous=NORMAL</c>, <c>busy_timeout</c>, one
/// connection and one writer. Every <see cref="InTransactionAsync{T}"/> is a single <c>BEGIN IMMEDIATE</c> transaction that is rolled
/// back when the work throws (or is cancelled), so a failed attempt commit leaves no partial state.
/// </summary>
public sealed class SqliteStateStore : IStateStore, IAsyncDisposable, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<bool> _inTransaction = new();

    private SqliteStateStore(SqliteConnection connection, string databasePath)
    {
        _connection = connection;
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    /// <summary>Opens (or creates) the database and applies pending migrations up to <paramref name="targetVersion"/> (default: all).</summary>
    public static SqliteStateStore Open(string databasePath, IClock clock, int? targetVersion = null, IReadOnlyList<Migration>? migrations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(clock);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 30,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
                pragmas.ExecuteNonQuery();
            }

            SqliteMigrator.Migrate(connection, migrations, targetVersion, Db.Time(clock.UtcNow));
            return new SqliteStateStore(connection, databasePath);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public async Task<T> InTransactionAsync<T>(Func<IUnitOfWork, Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_inTransaction.Value)
        {
            throw new InvalidOperationException("Transactions of the state store must not be nested.");
        }

        await _gate.WaitAsync(ct);
        _inTransaction.Value = true;
        try
        {
            ct.ThrowIfCancellationRequested();
            using var tx = _connection.BeginTransaction(deferred: false);
            T result;
            try
            {
                result = await work(new SqliteUnitOfWork(_connection, tx));
                ct.ThrowIfCancellationRequested();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            return result;
        }
        finally
        {
            _inTransaction.Value = false;
            _gate.Release();
        }
    }

    /// <summary>Executes read-only SQL (tests and diagnostics); not part of the port.</summary>
    public async Task<IReadOnlyList<IReadOnlyList<object?>>> QueryAsync(string sql, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<IReadOnlyList<object?>>();
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class SqliteUnitOfWork(SqliteConnection connection, SqliteTransaction transaction) : IUnitOfWork
    {
        public IGraphRepository Graph { get; } = new SqliteGraphRepository(connection, transaction);

        public IFrontierRepository Frontier { get; } = new SqliteFrontierRepository(connection, transaction);

        public IEventOutbox Events { get; } = new SqliteEventOutbox(connection, transaction);

        public IAgentCallRepository AgentCalls { get; } = new SqliteAgentCallRepository(connection, transaction);

        public IRunRepository Runs { get; } = new SqliteRunRepository(connection, transaction);

        public IScreenContractRepository Contracts { get; } = new SqliteScreenContractRepository(connection, transaction);
    }
}
