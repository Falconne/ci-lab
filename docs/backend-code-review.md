# Backend Code Review — Mergician ASP.NET Core App

> Reviewed: `src/be/Mergician/` (C# · ASP.NET Core · Dapper · SQLite)
>
> Focus: clean code, best practices, reliability, and maintainability.
> Trivial formatting and style matters are excluded.

---

## Summary

The Mergician backend is well-engineered overall. The async/await patterns are correct, dependency injection is clean, error handling is consistent, and logging is thorough. Two medium-priority issues were found in repository transaction handling, and one low-priority thread-safety observation is noted.

---

## Medium Priority

### 1. Transactions opened before guard checks

**Files:** `MergeQueueRepository.cs` lines 49–64, 111–124

In `AddMergeGroupToQueue` and `RemoveMergeGroupFromQueue` a database transaction is opened, then the first thing inside the method body is a guard query that may cause an early return:

```csharp
// AddMergeGroupToQueue
using var transaction = connection.BeginTransaction();

var existing = connection.QueryFirstOrDefault<int?>(..., transaction);
if (existing != null)
{
    _logger.LogDebug(...);
    return;   // ← transaction disposed & rolled back — never needed
}
```

The transaction is wasted on the no-op path. More importantly, the pattern obscures intent: a reader has to trace the whole method to understand that the guard check is the first real step, not something that benefits from a transaction.

**Suggested fix:** Perform the guard query before opening the transaction, then open the transaction only when there is actual work to do:

```csharp
public void AddMergeGroupToQueue(int mergeGroupId, IReadOnlyCollection<int> projectIds)
{
    using var connection = _connectionFactory.CreateConnection();
    connection.Open();

    var existing = connection.QueryFirstOrDefault<int?>(
        "SELECT queue_id FROM merge_queue_entry WHERE merge_group_id = @MergeGroupId",
        new { MergeGroupId = mergeGroupId });

    if (existing != null)
    {
        _logger.LogDebug(...);
        return;
    }

    using var transaction = connection.BeginTransaction();
    // ... rest of the method unchanged
    transaction.Commit();
}
```

The same applies to `RemoveMergeGroupFromQueue` (read the entry outside the transaction, return early if not found, then open the transaction only when the delete + resequence work needs to happen).

---

### 2. Success log emitted before `transaction.Commit()`

**File:** `MergeQueueRepository.cs` lines 136–141

In `RemoveMergeGroupFromQueue`, the "removed merge group" log line is written *before* the commit:

```csharp
_logger.LogInformation(
    "MergeQueueRepository: removed merge group {MergeGroupId} from queue {QueueId}",
    mergeGroupId,
    queueId);

transaction.Commit();   // ← if this throws, the log above is a lie
```

If `Commit()` fails (e.g. SQLite I/O error), the log records a successful removal that never happened. This misleads anyone diagnosing a data inconsistency.

**Suggested fix:** Move the log line to after the commit — or at minimum swap the two statements:

```csharp
transaction.Commit();

_logger.LogInformation(
    "MergeQueueRepository: removed merge group {MergeGroupId} from queue {QueueId}",
    mergeGroupId,
    queueId);
```

---

## Low Priority

### 3. `SyncTask` property has no formal memory-visibility guarantee

**File:** `UserSyncContext.cs` line 27; `UserActivityBackgroundSyncService.cs` lines 90–94

`SyncTask` is a plain auto-property:

```csharp
public Task? SyncTask { get; set; }
```

It is assigned under `context.StartLock` in `EnsureSyncRunning`, but it is read in `StopAsync` without acquiring any lock:

```csharp
var tasks = _userContexts.Values
    .Select(c => c.SyncTask)   // ← no lock here
    ...
```

On current .NET runtimes a reference-sized write/read is effectively atomic, and the `lock` in `EnsureSyncRunning` provides a happens-before for the writer. However, `StopAsync` has no memory barrier that guarantees it sees the latest written value on every platform. The formal fix is either a `volatile` field backing the property, or reading `SyncTask` inside the same lock that writes it.

In practice the risk is minimal because `StopAsync` is called during graceful shutdown (well after all `EnsureSyncRunning` calls have completed), but the code as written relies on an implicit assumption that is worth making explicit.

**Suggested fix:**

```csharp
private volatile Task? _syncTask;

public Task? SyncTask
{
    get => _syncTask;
    set => _syncTask = value;
}
```

---

## Positive Observations

These areas are worth calling out as done well:

- **Async patterns** — `async`/`await` used correctly throughout; no sync-over-async antipatterns (`.Result`, `.Wait()`); `CancellationToken` propagated to every async call.
- **`OperationCanceledException` handling** — consistently filtered with `when (ct.IsCancellationRequested)` guard clauses so unrelated timeouts are not silently swallowed.
- **Transaction disposal** — all transactions use `using var`, guaranteeing disposal (and implicit rollback if uncommitted) even on exception paths.
- **`IHttpClientFactory` usage** — `HttpClient` instances are obtained via the factory, avoiding the well-known socket exhaustion problem.
- **Lock discipline** — `ReaderWriterLockSlim` used correctly for the `AccessUser` property; `ConcurrentDictionary` used for the user context map.
- **Resource management** — all `IDbConnection` objects are wrapped in `using`; no raw `new HttpClient()` calls.
- **Error handling** — background services catch exceptions at the loop boundary and log them before continuing, preventing a single bad iteration from killing the service.
- **Retry logic** — `GitLabApiClient.ExecuteCore` uses a typed `_retryDelays` array with a clear distinction between retriable (5xx) and non-retriable (4xx) errors.
- **Configuration** — no secrets are hardcoded; all environment-specific values come from `MergicianSettings` bound from configuration.
