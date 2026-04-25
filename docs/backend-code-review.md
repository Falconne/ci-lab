# Mergician Backend Code Review

Reviewed against the ASP.NET Core 9 backend at `src/be/Mergician/`.

---

## 1. Bugs / Correctness Issues

### 1.1 `DeadBranchesService.IsBranchGone` — Transient API failure silently removes tracked branches

**File:** `Services/DeadBranchesService.cs`, lines 122–130

When `GetBranchLookupResult` returns `Unavailable` (any non-404 error — network timeout, 5xx, rate-limit), `IsBranchGone` returns `true`. This causes `RemoveBranchIfGone` to delete the branch record and clean up its merge group. A transient GitLab API outage therefore silently destroys tracking data.

```csharp
if (branchLookup.IsUnavailable)
{
    _logger.LogError(...);
    return true;   // ← treats API failure the same as branch deletion
}
```

**Fix:** Return `false` (or re-throw) on `Unavailable` so the cleanup step is skipped until the API is responsive again.

---

### 1.2 `AutoMergeService.ProcessAutoMerge` — Exception inside `Task.WhenAll` prevents cleanup of successfully merged branches

**File:** `Services/AutoMerge/AutoMergeService.cs`, lines 347–411

The merge tasks are fired with `Task.WhenAll`. If any task throws an exception that isn't caught by `AutoMergeGitLabApiService.Merge` (e.g., `GitLabStartupRequiredException` from exhausted retries), `Task.WhenAll` propagates the exception before the results are inspected. The code that removes successfully merged branches from the database (lines 403–411) is never reached:

```csharp
var results = await Task.WhenAll(mergeTasks);  // throws if any task throws

// These lines are never reached on partial exception:
foreach (var (branch, _, _) in succeeded)
    _deadBranchesService.RemoveBranchAndCleanup(branch.Id);
```

The result is stale branch records for MRs that have already been merged, until the next cleanup cycle.

**Fix:** Wrap `Task.WhenAll` in a try/catch, collect `results` from `Task.WhenAll`-completed tasks even when some faulted, or handle `GitLabStartupRequiredException` inside each merge lambda.

---

### 1.3 `AuthController.Callback` — OAuth error response from GitLab causes model binding failure

**File:** `Controllers/AuthController.cs`, line 57

The callback signature is:
```csharp
public async Task<ActionResult> Callback([FromQuery] string code, [FromQuery] string state)
```

When a user denies authorization, GitLab sends `?error=access_denied&error_description=...` — no `code` parameter. ASP.NET Core model binding will reject the request with a 400 before the action method runs, giving the user a raw error page rather than a graceful redirect.

**Fix:** Make `code` nullable (`string? code`) and check for an `error` query parameter at the top of the method:
```csharp
public async Task<ActionResult> Callback(
    [FromQuery] string? code,
    [FromQuery] string? state,
    [FromQuery] string? error)
{
    if (error != null)
        return Redirect($"/?error=auth&message={Uri.EscapeDataString(error)}");
    ...
}
```

---

### 1.4 `MergeRequestLookupService.ParseMergeRequestUrl` — `int.Parse` throws on very large MR IIDs

**File:** `Services/GitLab/MergeRequestLookupService.cs`, line 46

```csharp
var mergeRequestIid = int.Parse(match.Groups["mergeRequestIid"].Value);
```

`int.Parse` throws `OverflowException` for MR IIDs above `int.MaxValue` (~2.1 billion), which is theoretically reachable on large, long-lived GitLab instances. The exception is unhandled here and would propagate as a 500.

**Fix:** Use `int.TryParse` and return `null` on failure, consistent with the rest of the parsing logic.

---

### 1.5 `MergeGroupRepository.GetOrCreateMergeGroup` — `ON CONFLICT DO UPDATE` is a no-op

**File:** `Services/Database/MergeGroupRepository.cs`, line 73

```sql
ON CONFLICT ON CONSTRAINT uq_merge_group_name
DO UPDATE SET name = EXCLUDED.name
```

`name` is the conflict key itself; setting it to `EXCLUDED.name` is always a no-op. If no other column needs updating, this should be `DO NOTHING`. The current form also causes a row lock and a write even when nothing changes, which adds unnecessary load.

**Fix:** Change to `DO NOTHING`.

---

### 1.6 `UserSyncContext.IsRunning` — non-volatile read outside lock

**File:** `Services/UserSyncContext.cs`, line 59

```csharp
public bool IsRunning => SyncTask is { IsCompleted: false };
```

`SyncTask` is assigned inside a `lock(context.StartLock)` block (`UserActivityBackgroundSyncService.cs`, line 142) but read outside that lock by the fast-path check at line 122. `Task` is a reference type and reference reads are atomic on x64, but the write is not guaranteed to be visible to other threads without a memory barrier. The double-checked-lock pattern requires the field to be `volatile` to be correct per the C# memory model.

**Fix:** Mark `SyncTask` as `volatile`, or fold the fast-path check inside the `lock` (the current fast path saves only a lock acquisition).

---

## 2. Performance Issues

### 2.1 `AutoMergeService` — Up to 5N GitLab API calls per 5-second cycle per merge group

**File:** `Services/AutoMerge/AutoMergeService.cs`, lines 143–174, 425–461

For each branch in a merge group, the 5-second loop performs:

1. `GetMergeRequests` (to find MR IID)
2. `GetDetailedMergeRequest` (to get detailed status)
3. `GetMergeRequestApprovals`
4. `GetLatestMergeRequestPipeline`
5. `GetPipelineJobs`

For a merge group with 10 branches, that is up to 50 sequential GitLab API calls per cycle, plus 5 more calls per branch for rebase checks. All these calls are sequential within each branch (no parallelism within `ProcessMergeGroup`).

**Fix:** Fetch MR details in parallel across branches using `Task.WhenAll`. Cache approval and pipeline data per cycle if the same project/MR appears in multiple groups. Consider coalescing the `GetMergeRequests` + `GetDetailedMergeRequest` calls using the existing `GetMergeRequestsByIid` path.

---

### 2.2 `MergeGroupRepository.GetMergeGroupsWithAutoSettings` — N+1 database queries

**File:** `Services/Database/MergeGroupRepository.cs`, lines 429–453

This method first fetches all merge group stubs in one query, then calls `GetBranchesFor` for each — a separate DB query per group. Called every 5 seconds by `AutoMergeService`, this is N+1 queries against the database:

```csharp
foreach (var record in records)
{
    result.Add(GetBranchesFor(connection, record));  // one query per group
}
```

**Fix:** Fetch all branches for all qualifying merge groups in a single JOIN query (the same pattern used by `GetMergeGroupsForUser`), then group them in memory.

---

### 2.3 `DeadBranchesService.RemoveBranchAndCleanup` — `GetEmptyMergeGroups` called per branch deletion

**File:** `Services/DeadBranchesService.cs`, lines 138–152

Each call to `RemoveBranchAndCleanup` issues `GetEmptyMergeGroups()` and then issues one `RemoveMergeGroup` per empty group. During `CleanupService.RunCleanup`, which iterates all tracked branches, this results in M×(1 + K) queries for M deleted branches with K empty groups per deletion.

**Fix:** In `CleanupService.RunCleanup`, collect all `removedBranchIds` and call a single batch cleanup at the end instead of per-branch. Alternatively, `RemoveBranch` can delete the merge group atomically via a `DELETE ... WHERE NOT EXISTS (SELECT ... FROM branches_in_merge_group)` SQL expression.

---

### 2.4 `UserActivityBackgroundSyncService.RefreshBranchDetails` — Duplicate GitLab branch endpoint calls per poll cycle

**File:** `Services/UserActivityBackgroundSyncService.cs`, lines 689–713 and `CleanupDeletedBranches`, lines 275–295

Each poll cycle calls both `CleanupDeletedBranches` (which calls `GetBranchLookupResult` → `GET /repository/branches/{name}`) and `RefreshAllBranchDetails` (which calls `GetBranchDetails` → same endpoint). These are separate calls to the same GitLab API endpoint for each tracked branch, doubling the API load per cycle.

**Fix:** Merge the existence check and detail fetch: if `GetBranchDetails` returns `null` (404), the branch is gone; otherwise use the returned data for the last-commit timestamp. This eliminates the separate `GetBranchLookupResult` call in the polling path.

---

### 2.5 Pipeline jobs fetched without pagination (truncated silently)

**Files:** `Services/AutoMerge/AutoMergeGitLabApiService.cs`, line 103; `Services/GitLab/GitLabPipelineService.cs`, line 131, line 165

Both `GetPipelineJobs` and `GetJobsFromPipeline` request `per_page=100` but don't paginate. GitLab pipelines with matrix jobs or large monorepos can exceed 100 jobs. The excess jobs are silently discarded, meaning build failures may be missed, causing auto-merge to incorrectly consider an MR ready.

**Fix:** Paginate using the `X-Next-Page` header (the `ExecutePaged` helper is already available) or increase the limit and add a log warning if a full page is returned.

---

## 3. Error Handling Gaps

### 3.1 `MergeGroupController` — No authorization check for merge group ownership

**File:** `Controllers/MergeGroupController.cs`

The following endpoints accept a `mergeGroupId` and act on it without verifying the requesting user is subscribed to (or owns) that group:

- `Refresh` (line 46) — returns any group's branch data to any authenticated user
- `UpdateSettings` (line 80) — any user can enable auto-merge on any group
- `ClearWarning` (line 113) — any user can clear warnings on any group
- `GetSubscription` / `Subscribe` / `Unsubscribe` (lines 124–186) — these do check subscription after the group lookup, which is correct, but `Refresh` and `UpdateSettings` do not

**Fix:** Add an `IsUserInMergeGroup` guard (the repository method already exists) to `Refresh`, `UpdateSettings`, and `ClearWarning` before taking action. Return 403 if the user is not subscribed.

---

### 3.2 `MergeGroupController.UpdateSettings` — Multiple un-transacted DB calls, silent failure on missing group

**File:** `Controllers/MergeGroupController.cs`, lines 94–107

```csharp
var existing = _mergeGroupRepository.GetMergeGroup(mergeGroupId);   // 1
if (existing == null) return NotFound(...);
_mergeGroupRepository.UpdateAutoMergeSettings(mergeGroupId, ...);   // 2
_mergeGroupRepository.UpdateAutoMergeWarning(mergeGroupId, null);   // 3
var updated = _mergeGroupRepository.GetMergeGroup(mergeGroupId);    // 4
return Ok(updated);
```

If the merge group is deleted between call 1 and call 2 (e.g., all its branches are cleaned up), calls 2 and 3 silently do nothing and call 4 returns `null`, which is then passed to `Ok(null)` — returning HTTP 200 with a null body instead of 404.

**Fix:** Wrap calls 2–3 in a transaction, or check the rowcount returned by `UpdateAutoMergeSettings` and return 404 if zero rows were affected.

---

### 3.3 `AutoMergeService.IsBranchReadyToMerge` — No pipeline treated as ready

**File:** `Services/AutoMerge/AutoMergeService.cs`, lines 440–461

If `latestPipeline == null` (the MR has never triggered a pipeline), `buildJobs` is empty and `MRStatusCalculator` sees no build failures, so the branch can be auto-merged with no CI signal at all. This may be desirable for projects without CI, but projects that have CI and simply have a pending pipeline would also pass through here if the pipeline fetch fails silently (returns null on API error).

**Fix:** If the project is expected to have pipelines (configurable per merge group, or inferred from the branch's history), treat a missing pipeline as `Waiting` rather than `Ready`.

---

### 3.4 `CleanupService` — Hard-coded New Zealand timezone violates "generic product" requirement

**File:** `Services/CleanupService.cs`, lines 13–16, 132–155

```csharp
private static readonly TimeZoneInfo _nzTimeZone =
    TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
```

The cleanup job is hard-coded to run at 3 AM NZST. This bakes in an assumption that all deployments are in New Zealand, violating the instruction that Mergician must work generically against any GitLab server. On a Linux container that does not have the `Pacific/Auckland` timezone data, `FindSystemTimeZoneById` will throw at startup.

**Fix:** Make the schedule UTC-based (e.g., 3 AM UTC) or expose a `CleanupScheduleUtcHour` configuration setting. Remove the NZST hard-coding.

---

## 4. Security Concerns

### 4.1 TLS certificate validation globally disabled for all GitLab API calls

**File:** `Program.cs`, lines 52–56

```csharp
builder.Services.AddHttpClient("GitLabOAuth")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    });
```

Every outbound call to GitLab — including those transmitting OAuth secrets, access tokens, and refresh tokens — is made with certificate validation disabled. An attacker who can intercept network traffic (e.g., on a shared network or via a compromised proxy) can perform a MITM attack and capture all credentials.

**Fix:** Remove `ServerCertificateCustomValidationCallback` entirely to restore .NET's default validation. If the GitLab server uses a self-signed certificate, add a configurable CA bundle path or accept a specific certificate thumbprint instead of disabling all validation.

---

### 4.2 `X-Forwarded-Proto` header trusted unconditionally

**File:** `Services/Authentication/CookieSecurity.cs`

`ShouldUseSecureCookies` trusts any `X-Forwarded-Proto: https` header, even when the application is exposed directly to the internet without a reverse proxy. An attacker can send this header to force `Secure = true` on cookies — this is not dangerous on its own, but in combination with other issues it means the Secure flag cannot be relied upon as a trust signal.

**Fix:** Introduce a `Mergician:TrustForwardedHeaders` configuration flag. Only read `X-Forwarded-Proto` when that flag is set, and use ASP.NET Core's `ForwardedHeadersMiddleware` (`UseForwardedHeaders`) instead of manual header inspection. This is the standard ASP.NET Core pattern for reverse proxy deployments.

---

### 4.3 `oauth_state` cookie not path-scoped

**File:** `Controllers/AuthController.cs`, lines 40–50

The `oauth_state` cookie is set without a `Path` attribute and therefore defaults to `/`. This means the CSRF state token is sent with every request to the application (static files, API calls, etc.) for the 10-minute lifetime, unnecessarily widening the window during which it could be read by a compromised middleware or logged.

**Fix:** Set `Path = "/api/auth/callback"` on the `oauth_state` cookie so it is only transmitted to the callback endpoint.

---

## 5. Design / Architecture Improvements

### 5.1 `IMergeGroupRepository.UpdateBranchDetails` — 12-parameter method

**File:** `Services/Database/IMergeGroupRepository.cs`, lines 75–88

`UpdateBranchDetails` has 12 parameters. This is hard to call correctly and makes call sites fragile when parameters need to be added or reordered. The method is called in two places (`UserActivityBackgroundSyncService.RefreshBranchDetails` and previously in background sync), and the call sites are long and error-prone.

**Fix:** Introduce a `BranchDetailsUpdate` record:
```csharp
public record BranchDetailsUpdate(
    bool HasMergeRequest,
    string? MergeRequestTitle,
    string? MergeRequestUrl,
    string? ProjectUrl,
    int? ApprovalsRequired,
    int? ApprovalsGiven,
    List<BranchBuildJob> BuildJobs,
    bool? NeedsRebase,
    DateTimeOffset? LastCommitTime,
    string? LastCommitMessage,
    int MrStatus,
    string? MrStatusReasons);
```

---

### 5.2 `CacheService` — Single expiry for all entries; stale project data persists 24 hours

**File:** `Services/CacheService.cs`

The project cache uses a single global expiry (default 24 hours). If a GitLab project is renamed or moved to a different namespace (which affects `NameWithNamespace`, used in deletion-scheduled detection), the old name is served for up to 24 hours. This could cause `IsScheduledForDeletion` to return incorrect results.

Additionally, the cache is entirely cleared when it expires — meaning a sudden burst of requests after expiry generates N simultaneous uncached API calls (cache stampede).

**Fix:** Use per-entry expiry with a shorter TTL (e.g., 30 minutes), or invalidate a project's cache entry when its data changes. For the stampede issue, consider a `GetOrCreate` pattern with lazy population.

---

### 5.3 `GetMergeGroupsForUser` — Fragile Dapper multi-map with positional column splitting

**File:** `Services/Database/MergeGroupRepository.cs`, lines 133–170

```csharp
connection.Query<int, string, bool, bool, string?, BranchWithActivity, ...>(
    ...,
    splitOn: "MergeGroupName,AutoMerge,AutoRebase,AutoMergeWarning,Id")
```

This relies on the exact column order in the SELECT for Dapper's `splitOn`. Adding, removing, or reordering any column silently breaks the mapping in a way that produces incorrect data (wrong properties populated) rather than a compile-time or runtime error. The 6-type-parameter generic is also difficult to read.

**Fix:** Use two separate queries (one for merge group stubs, one for branches) and join them in memory. This is already the pattern used by `GetMergeGroupsWithAutoSettings` → `GetBranchesFor`, and avoids the fragile multi-map entirely.

---

### 5.4 `CleanupService` injects `IServiceProvider` to resolve a singleton

**File:** `Services/CleanupService.cs`, lines 100–103

```csharp
using var scope = _serviceProvider.CreateScope();
var mergeGroupRepository = scope.ServiceProvider.GetRequiredService<IMergeGroupRepository>();
```

`IMergeGroupRepository` is registered as a singleton (`Program.cs`, line 46). Creating a DI scope to resolve a singleton is unnecessary indirection. `_serviceProvider` exists only for this one line.

**Fix:** Inject `IMergeGroupRepository` directly into `CleanupService` instead of `IServiceProvider`. Remove the `_serviceProvider` field.

---

### 5.5 `IsPossibleDefaultBranch` — Hard-coded list of three branch names

**File:** `Services/GitLab/GitLabService.cs`, lines 30–33

```csharp
return branchName is "main" or "master" or "develop";
```

This list does not cover other common default branch names (`trunk`, `dev`, `release`, etc.) and cannot be customized per GitLab project. Push events and MR syncs for these branches would be silently skipped if a team uses `develop` as their integration branch for feature work (as opposed to the true default).

**Fix:** Query GitLab's `projects/{id}` response for `default_branch` and compare against that instead of a hard-coded list. Cache the result per-project alongside other project info.

---

## 6. Code Duplication

### 6.1 Pipeline job mapping duplicated between `GitLabPipelineService` and `AutoMergeGitLabApiService`

**Files:** `Services/GitLab/GitLabPipelineService.cs`, lines 181–186; `Services/AutoMerge/AutoMergeGitLabApiService.cs`, lines 106–111

Both services map `GitLabPipelineJob` → `BranchBuildJob` with identical logic:

```csharp
// GitLabPipelineService:
jobs.Select(job => new BranchBuildJob(
    job.Name.IsEmpty() ? "job" : job.Name,
    job.Status.IsEmpty() ? "unknown" : job.Status,
    job.WebUrl.IsEmpty() ? null : job.WebUrl))

// AutoMergeGitLabApiService:
jobs.Select(job => new BranchBuildJob(
    job.Name.IsEmpty() ? "job" : job.Name,
    job.Status.IsEmpty() ? "unknown" : job.Status,
    job.WebUrl.IsEmpty() ? null : job.WebUrl))
```

`AutoMergeGitLabApiService.GetPipelineJobs` also duplicates the GitLab API call that already exists in `GitLabPipelineService.GetJobsFromPipeline`.

**Fix:** Move the shared job-mapping logic into a static helper (`GitLabPipelineJob.ToBranchBuildJob()`). Have `AutoMergeGitLabApiService` delegate to `GitLabPipelineService` for pipeline job fetching (or inject `GitLabPipelineService` into it).

---

### 6.2 Branch-to-merge-group association logic duplicated between `SyncExistingMergeRequests` and `FetchNewUserActivityFromGitLab`

**File:** `Services/UserActivityBackgroundSyncService.cs`, lines 384–406, 511–534

Both methods contain near-identical blocks:

```csharp
var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(branchName, project);
var mergeGroup = _mergeGroupRepository.GetOrCreateMergeGroup(branchName);
var isNewToMergeGroup = mergeGroup.Branches.NotAny(b => b.Id == branchRecord.Id);
if (isNewToMergeGroup) _mergeGroupRepository.EnsureBranchInMergeGroup(mergeGroup.Id, branchRecord.Id);
_mergeGroupRepository.EnsureUserInMergeGroup(userId, mergeGroup.Id);
```

**Fix:** Extract this into a private helper method `EnsureBranchTracked(project, branchName, userId)`.

---

## 7. Missing Edge Case Handling

### 7.1 `GetMergeRequestsByIid` filters by `state=opened` — closed MRs silently not found

**File:** `Services/GitLab/GitLabService.cs`, line 479

When a user uses "Add by MR URL" with a merged or closed MR, `GetMergeRequestsByIid` returns an empty list and the operation fails with "Merge request not found" — even though the MR exists. The error message directs the user to "check the URL and ensure you have access", which is misleading.

**Fix:** Either drop the `state=opened` filter (and handle the case of a merged MR's source branch), or return a distinct error code for "MR exists but is already merged/closed" so the UI can show a clearer message.

---

### 7.2 `UserActivityBackgroundSyncService` — No handling for 401 in background thread

**File:** `Services/UserActivityBackgroundSyncService.cs`

The background sync thread uses the user's access token from `context.AccessUser`. The authentication handler refreshes this token at the time of the next HTTP request, but between requests the token may expire. Background API calls with an expired token receive 401, which `GitLabService` catches and returns as an empty result (e.g., empty MR list). The branch is then updated as having no open MR, giving incorrect data until the token is refreshed.

This is most noticeable when `_inactivityTimeout` (5 minutes) is close to the GitLab access token TTL. In CI Lab, GitLab issues tokens with a 2-hour TTL, so this is unlikely in practice, but worth addressing for correctness.

**Fix:** On 401 from any background GitLab call, log a warning and skip the update for that branch rather than persisting incorrect "no MR" data. The next poll after token refresh will correct the state.

---

### 7.3 `AutoMergeService.ProcessAutoRebase` — Rebase conflict detection relies on a fixed 5-second delay

**File:** `Services/AutoMerge/AutoMergeService.cs`, lines 238–249

After triggering a rebase, the service waits exactly 5 seconds, then polls once for conflict status:

```csharp
await Task.Delay(_rebaseCheckDelay, cancellationToken);
var updatedMergeRequest = await _apiService.GetDetailedMergeRequest(...);
if (updatedMergeRequest is not { HasConflicts: true }) continue;
```

If the rebase hasn't completed within 5 seconds (common for large repositories), `HasConflicts` will still be `false` on a still-running rebase, and the conflict check is skipped. The conflict will be detected in a future cycle's `needsRebase` check instead, but by then another merge attempt may have already been initiated if auto-merge is enabled simultaneously.

**Fix:** Poll for rebase completion using `RebaseInProgress` rather than a fixed delay, with a reasonable timeout (e.g., 30 seconds), before checking for conflicts.

---

## 8. Dead Code

### 8.1 `IMergeGroupRepository.GetMergeGroupIdsForBranch` is never called

**Files:** `Services/Database/IMergeGroupRepository.cs`, line 69; `Services/Database/MergeGroupRepository.cs`, line 304

`GetMergeGroupIdsForBranch` is defined in the interface and implemented in the repository but has no callers anywhere in the codebase.

**Fix:** Remove the method from the interface and implementation unless there is a planned future use (in which case, document the intent with a TODO comment).

---

## Summary: Top 5 Most Impactful Findings

| # | Finding | Impact |
|---|---------|--------|
| **1** | **[4.1] TLS certificate validation disabled globally** (`Program.cs:53`) | All GitLab communication — including OAuth secrets, access tokens, and refresh tokens — is transmitted with no certificate validation. A network-level MITM attack can harvest all user credentials. |
| **2** | **[3.1] No merge group ownership checks in controller** (`MergeGroupController.cs`) | Any authenticated user can read any merge group's data, enable auto-merge on groups they don't own, and clear warnings — a horizontal privilege escalation affecting every user. |
| **3** | **[1.1] Transient API failure removes tracked branches** (`DeadBranchesService.cs:122`) | A brief GitLab outage causes `IsBranchGone` to return `true`, permanently deleting branch records and their merge groups from the database, with no recovery path. |
| **4** | **[2.1] Up to 5N sequential GitLab API calls per 5-second cycle** (`AutoMergeService.cs:143`) | For merge groups with many branches, the auto-merge polling loop makes N×5 sequential API calls every 5 seconds, likely hitting the rate limiter and degrading overall application responsiveness. |
| **5** | **[1.3] OAuth callback breaks on user-denied authorization** (`AuthController.cs:57`) | If a user clicks "Deny" on the GitLab OAuth consent screen, GitLab redirects back with `?error=access_denied` (no `code` param), and ASP.NET model binding rejects the request with a raw 400 error instead of a user-friendly redirect. |
