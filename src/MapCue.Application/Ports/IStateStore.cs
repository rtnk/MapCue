namespace MapCue.Application.Ports;

/// <summary>
/// Source of truth for graph, frontier and attempts (ADR-0003, SQLite in STEP-03).
/// A committed attempt is exactly one transaction.
/// </summary>
public interface IStateStore
{
    Task<T> InTransactionAsync<T>(Func<IUnitOfWork, Task<T>> work, CancellationToken ct);
}

/// <summary>Transactional scope. Repositories are added in STEP-03.</summary>
public interface IUnitOfWork
{
}
