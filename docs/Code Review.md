# Code Review — Suggested Improvements

This document captures the results of a full code review of the Mergician frontend and backend,
plus supporting code (integration tests, bootstrapper). Each section describes a concrete improvement
with enough detail for an independent developer or LLM to implement the change.

Only findings that genuinely improve code quality, security, or maintainability are included —
no changes for the sake of changes.

---

## Priority Legend

| Priority | Meaning |
|----------|---------|
| **High** | Security issue or significant architectural concern — address soon |
| **Medium** | Meaningful code quality or performance improvement |
| **Low** | Cleanup, minor improvement, or edge-case fix |

---

## 1. [HIGH] Merge Group IDOR — Any Authenticated User Can View Any Merge Group

**Files:** `src/be/Mergician/Controllers/MergeGroupController.cs` (line ~45),
`src/be/Mergician/Services/Database/IMergeGroupRepository.cs`,
`src/be/Mergician/Services/Database/MergeGroupRepository.cs`

**Issue:** `MergeGroupController.Refresh(int mergeGroupId)` calls
`_mergeGroupRepository.GetMergeGroup(mergeGroupId)` without verifying that the current
authenticated user is associated with that merge group. Any authenticated user can enumerate
and view other users' merge groups by incrementing the integer ID in the URL.

**Fix:**
1. Add a repository method `GetMergeGroupForUser(int mergeGroupId, int gitlabUserId)` that
   JOINs against `users_in_merge_group` to restrict results to groups the user belongs to.
2. In `MergeGroupController.Refresh`, extract the GitLab user ID from the session (same way
   `ActivityController` does) and call the new method.
3. Return 404 (not 403) when the merge group doesn't exist *or* the user isn't associated,
   to avoid leaking the existence of groups.

**SQL for the new method:**
```sql
SELECT mg.id, mg.name
FROM merge_groups mg
JOIN users_in_merge_group umg ON umg.merge_group_id = mg.id
WHERE mg.id = @MergeGroupId AND umg.gitlab_user_id = @GitlabUserId
```

**Impact:** Information disclosure — users can see other users' branch names, MR titles,
and project names.

---

## 2. [HIGH] TLS Certificate Validation Disabled Globally for GitLab HTTP Client

**File:** `src/be/Mergician/Program.cs` (line ~49–52)

**Issue:** The `GitLabOAuth` named `HttpClient` has
`ServerCertificateCustomValidationCallback = (_, _, _, _) => true`, which disables all TLS
certificate validation. While needed for self-signed certs in the CI Lab environment, this
applies in production too, silently accepting MITM attacks.

**Fix:**
1. Add a `GitLab.SkipCertificateValidation` boolean setting to `MergicianSettings` (default `false`).
2. Only set the custom callback when this setting is `true`.
3. In `appsettings.Development.json` / environment variables for CI Lab, set it to `true`.
4. Log a warning at startup when this is enabled: "TLS certificate validation is disabled for
   GitLab connections. Do not use in production."

**Code sketch:**
```csharp
if (settings.GitLab.SkipCertificateValidation)
{
    handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
    Log.Warning("TLS certificate validation disabled for GitLab connections");
}
```

**Impact:** Prevents MITM attacks in production. Self-hosted GitLab instances with valid
certs or properly trusted CAs will work correctly without this bypass.

---

## 3. [MEDIUM] Duplicated TypeScript Interfaces Across Views

**Files:** `src/fe/src/views/HomeView.vue` (lines ~157–185),
`src/fe/src/views/MergeGroupDetailsView.vue` (lines ~170–195)

**Issue:** `BranchRecord`, `BranchBuildJob`, and `MergeGroup` interfaces are identically
defined in both views. Any backend schema change must be updated in two places.

**Fix:**
1. Create `src/fe/src/types/mergeGroup.ts`:
   ```typescript
   export interface BranchBuildJob {
     name: string
     status: string
     url?: string | null
   }

   export interface BranchRecord {
     branchName: string
     projectId: number
     projectName: string
     projectNameWithNamespace: string
     hasMergeRequest: boolean | null
     approvalsRequired: number | null
     approvalsGiven: number | null
     lastUpdated: string | null
     mergeRequestTitle?: string | null
     mergeRequestUrl?: string | null
     projectUrl?: string | null
     buildJobs?: BranchBuildJob[] | null
     branchInProjectId?: number | null
   }

   export interface MergeGroup {
     id: number
     name: string
     branches: BranchRecord[]
   }
   ```
2. Import from both views: `import type { MergeGroup, BranchRecord } from '@/types/mergeGroup'`
3. Remove the local interface definitions.

**Impact:** Prevents type drift and ensures backend schema changes only need one update.

---

## 4. [MEDIUM] Duplicated Polling Lifecycle Logic Between Views

**Files:** `src/fe/src/views/HomeView.vue` (startPolling/stopPolling),
`src/fe/src/views/MergeGroupDetailsView.vue` (startPolling/stopPolling)

**Issue:** Both views duplicate the identical fast-poll-then-normal-poll pattern:
constants (`FAST_POLL_INTERVAL_MS`, `NORMAL_POLL_INTERVAL_MS`, `FAST_POLL_DURATION_MS`),
`initialPhase` ref, timer management, and the setAppLoading integration. This is ~60 lines
of functionally identical code.

**Fix:** Extract into a `usePolling` composable:
```typescript
// composables/usePolling.ts
import { ref, onUnmounted } from 'vue'
import { useAppLoading } from './useAppLoading'

export function usePolling(pollFn: () => void | Promise<void>, options?: {
  fastIntervalMs?: number
  normalIntervalMs?: number
  fastDurationMs?: number
}) {
  const { setAppLoading } = useAppLoading()
  const initialPhase = ref(false)
  const fastInterval = options?.fastIntervalMs ?? 1000
  const normalInterval = options?.normalIntervalMs ?? 5000
  const fastDuration = options?.fastDurationMs ?? 5000

  let pollId: ReturnType<typeof setInterval> | null = null
  let fastTimeoutId: ReturnType<typeof setTimeout> | null = null

  function startPolling() {
    if (pollId !== null) return
    initialPhase.value = true
    setAppLoading(true)
    pollId = setInterval(pollFn, fastInterval)
    fastTimeoutId = setTimeout(() => {
      initialPhase.value = false
      setAppLoading(false)
      if (pollId !== null) {
        clearInterval(pollId)
        pollId = setInterval(pollFn, normalInterval)
      }
      fastTimeoutId = null
    }, fastDuration)
    pollFn()
  }

  function stopPolling() {
    if (pollId !== null) { clearInterval(pollId); pollId = null }
    if (fastTimeoutId !== null) { clearTimeout(fastTimeoutId); fastTimeoutId = null }
    setAppLoading(false)
  }

  onUnmounted(stopPolling)

  return { startPolling, stopPolling, initialPhase }
}
```
Each view would then just call:
```typescript
const { startPolling, stopPolling, initialPhase } = usePolling(pollDashboard)
```

**Impact:** Eliminates ~60 lines of duplicated code, ensures consistent polling behavior,
and makes it trivial to adjust polling parameters globally.

---

## 5. [MEDIUM] Duplicated CSS (Card Styles, Status Badges, Skeleton Shimmer)

**Files:** `src/fe/src/views/HomeView.vue` (style section),
`src/fe/src/views/MergeGroupDetailsView.vue` (style section)

**Issue:** The skeleton shimmer `@keyframes`, `.skeleton-shimmer`, `.skeleton-badge`,
`.card-status-badge`, `.status-ready/open/waiting`, `.card-accent`, and `.card-body` styles
are duplicated across both views' `<style scoped>` blocks.

**Fix:** Create a shared SCSS/CSS file `src/fe/src/styles/card.css` with the common
card and status badge styles. Import it in both views:
```vue
<style scoped>
@import '@/styles/card.css';
/* ... view-specific styles only ... */
</style>
```
Note: With scoped styles, the import will be scoped to each component. Alternatively, use
non-scoped styles for truly shared elements or extract them into a shared component.

**Impact:** Style changes (e.g. changing the shimmer animation speed or badge border-radius)
only need to happen in one place.

---

## 6. [MEDIUM] Sequential GitLab API Calls per Branch in Sync Loop

**File:** `src/be/Mergician/Services/Gitlab/UserActivitySyncService.cs`
(method `RefreshBranchDetails`, line ~303)

**Issue:** For each branch, 5 sequential API calls are made to GitLab:
1. `GetProject`
2. `GetMergeRequests`
3. `GetMergeRequestApprovals` (if MR exists)
4. `GetLatestExternalJobsForBranch` (which itself makes 2–3 calls)
5. `GetBranchDetails`

With 10 branches, this is 50+ sequential HTTP calls per poll cycle.

**Fix:**
1. Within `RefreshBranchDetails`: after `GetProject` returns and the project is valid,
   fire independent requests concurrently:
   ```csharp
   var mrTask = _gitlabService.GetMergeRequests(accessDetails, branch.ProjectId, branch.BranchName);
   var jobsTask = _gitlabPipelineService.GetLatestExternalJobsForBranch(
       accessDetails, branch.ProjectId, branch.BranchName, cancellationToken);
   var branchTask = _gitlabService.GetBranchDetails(accessDetails, branch.ProjectId, branch.BranchName);

   await Task.WhenAll(mrTask, jobsTask, branchTask);

   var mergeRequests = mrTask.Result;
   var buildJobs = jobsTask.Result;
   var branchDetails = branchTask.Result;
   ```
2. In `RefreshAllBranchDetails`, consider parallelizing across branches with
   `Parallel.ForEachAsync` or `SemaphoreSlim` (limit to 3–5 concurrent branches to avoid
   GitLab rate limiting).

**Impact:** Could reduce poll cycle time from ~10s to ~3s for a typical user, making the
dashboard feel significantly more responsive.

---

## 7. [MEDIUM] `avatar_url` vs `avatarUrl` Serialization Inconsistency

**Files:** `src/fe/src/composables/useCurrentUser.ts` (line ~3–8),
`src/be/Mergician/Entities/GitLabEntities.cs` (GitLabUserInfo class),
`src/be/Mergician/Controllers/AuthController.cs`

**Issue:** The `CurrentUser` TypeScript interface uses `avatar_url` (snake_case). The C#
`GitLabUserInfo` entity uses `AvatarUrl` (PascalCase). ASP.NET Core's default JSON
serialization produces `avatarUrl` (camelCase). The authentication handler uses
`SnakeCaseLower` for deserialization from GitLab, but the controller re-serializes with
ASP.NET defaults. The avatar display in `AppBar.vue` may be broken depending on which
serialization path is active.

**Fix:**
1. Check the actual JSON response from `GET /api/auth/me` — verify what key the avatar URL
   comes back under.
2. Add `[JsonPropertyName("avatar_url")]` to `GitLabUserInfo.AvatarUrl` to ensure consistent
   snake_case output, OR change the TypeScript interface to `avatarUrl` (camelCase) to match
   ASP.NET defaults.
3. The simplest safe fix: add `[JsonPropertyName]` attributes to all `GitLabUserInfo` fields
   to make serialization explicit and match what the frontend expects.

**Impact:** The user avatar in the app bar may not display. This should be verified empirically.

---

## 8. [MEDIUM] Hardcoded New Zealand Timezone in CleanupService

**File:** `src/be/Mergician/Services/CleanupService.cs` (line ~20–22)

**Issue:** The cleanup schedule reads:
```csharp
var nzTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
```
The copilot instructions state Mergician should work against "any self-hosted GitLab server."
This timezone assumption is developer-locale-specific.

**Fix:**
1. Either make the cleanup hour configurable via `MergicianSettings` — e.g.
   `Mergician.CleanupHourUtc` (default 14, which is ~3am NZST and a reasonable overnight
   hour for many timezones).
2. Or simplify to a 24-hour interval from startup: run cleanup every 24 hours. This is
   timezone-agnostic and simpler.

**Impact:** The cleanup runs at unexpected times for users in other timezones. Not critical
but violates the "Mergician must remain generic" constraint.

---

## 9. [MEDIUM] Integration Test `VersionAndLastUpdatedTest` Checks Source Files Instead of UI

**File:** `src/be/IntegrationTest/Tests/VersionAndLastUpdatedTest.cs` (lines ~45–59)

**Issue:** The test reads `HomeView.vue` and `AppBar.vue` from disk to verify they contain
certain strings (like `formatTimeAgo`). This tests the source code presence, not whether the
feature actually renders correctly in the browser. If template rendering were broken, this
test would still pass.

**Fix:** Use Playwright to:
1. Navigate to the dashboard after login.
2. Verify the version text is visible in the app bar (e.g. assert text matching
   `fe: <hash> | be: <hash>` is present).
3. Verify that time-ago text (e.g. "X minutes ago") renders on at least one dashboard card.
4. The source file checks can be kept as supplementary assertions but should not be the
   primary test.

**Impact:** Provides genuine confidence that the version display and time formatting work
as expected at runtime.

---

## 10. [LOW] Duplicated Login Flow in Integration Tests

**Files:** `src/be/IntegrationTest/Tests/AuthenticationTest.cs`,
`src/be/IntegrationTest/Tests/LogoutTest.cs`,
`src/be/IntegrationTest/Tests/DashboardTest.cs`,
`src/be/IntegrationTest/Tests/DashboardLiveUpdateTest.cs`

**Issue:** The OAuth login flow (navigate to Mergician → redirect to GitLab → fill credentials
→ handle authorize page → redirect back) is implemented 4 times with minor variations.

**Fix:** Create a shared `LoginHelper` method:
```csharp
public static class LoginHelper
{
    public static async Task LoginViaGitLabOAuth(
        IPage page,
        string mergicianUrl,
        string username,
        string password,
        string screenshotDir)
    {
        // Navigate to Mergician
        await page.GotoAsync(mergicianUrl);
        // Click sign-in button
        // Fill GitLab credentials
        // Handle authorize page if needed
        // Wait for redirect back to dashboard
    }
}
```
Each test then calls `await LoginHelper.LoginViaGitLabOAuth(page, ...)` instead of
duplicating ~40 lines.

**Impact:** Any change to the GitLab login UI only needs one fix. Reduces test boilerplate
by ~120 lines total.

---

## 11. [LOW] Duplicated `JsonSerializerOptions` Instances

**Files:** `src/be/Mergician/Services/Gitlab/GitlabService.cs` (line ~13),
`src/be/Mergician/Services/Gitlab/GitlabPipelineService.cs` (line ~9),
`src/be/Mergician/Services/Authentication/GitLabCookieAuthenticationHandler.cs` (line ~120)

**Issue:** Three separate `static readonly JsonSerializerOptions` instances with
`PropertyNameCaseInsensitive = true`. The auth handler adds `SnakeCaseLower` naming policy
which is inconsistent.

**Fix:** Define a single shared options instance:
```csharp
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
```
Replace all three usages. For the auth handler that needs snake_case, either use a separate
constant or rely on `[JsonPropertyName]` attributes on the entity.

**Impact:** Maintenance overhead and risk of options drift. Minor but trivial to fix.

---

## 12. [LOW] Duplicate Branch Deletion Logic Across Services

**Files:** `src/be/Mergician/Services/CleanupService.cs` (lines ~65–107),
`src/be/Mergician/Services/Gitlab/UserActivitySyncService.cs` (lines ~660–674)

**Issue:** Both services independently implement "check if branch is deleted → delete from DB
→ clean up empty merge groups." The `RemoveBranchAndCleanup` private method in
`UserActivitySyncService` and the equivalent code in `CleanupService` are functionally identical.

**Fix:** Extract into a shared method on `IMergeGroupRepository`:
```csharp
void DeleteBranchAndCleanupEmptyGroups(int branchInProjectId);
```
This method would:
1. Get the merge group IDs associated with the branch.
2. Remove the branch from all merge groups.
3. Delete the branch record.
4. Check each affected merge group — if empty, delete it.

**Impact:** Bug fixes to cleanup logic need to be applied in only one place.

---

## 13. [LOW] Integration Test `Program.cs` Has Repetitive Try-Catch Boilerplate

**File:** `src/be/IntegrationTest/Program.cs` (lines ~68–200)

**Issue:** Every test follows the exact same pattern: log test name, instantiate, try/run/catch
(add result)/finally (dispose). This is ~30 lines per test of boilerplate.

**Fix:** Create a helper:
```csharp
async Task RunTest<T>(string name) where T : IDisposable, new()
{
    var test = new T();
    try
    {
        Log.Information("");
        Log.Information("--- Test: {TestName} ---", name);
        await ((dynamic)test).Run();
        results.Add((name, true, null));
        Log.Information("PASS: {TestName}", name);
    }
    catch (Exception ex)
    {
        results.Add((name, false, ex.Message));
        Log.Error("FAIL: {TestName} - {Error}", name, ex.Message);
        allPassed = false;
        if (abortOnFirstFailure) throw;
    }
    finally
    {
        test.Dispose();
    }
}
```
Then the test execution becomes:
```csharp
await RunTest<AuthenticationTest>("Authentication");
await RunTest<LogoutTest>("Logout");
await RunTest<DashboardTest>("Dashboard");
// etc.
```

For proper type safety, define an interface `IIntegrationTest : IDisposable` with
`Task Run()` and have all test classes implement it.

**Impact:** Adding a new test requires 1 line instead of 30. Reduces ~200 lines of boilerplate.

---

## 14. [LOW] `formatTimeAgo` Doesn't Handle Future Timestamps

**File:** `src/fe/src/views/HomeView.vue` (function `formatTimeAgo`, line ~310)

**Issue:** If the server timestamp is slightly ahead of the client's clock, `diffMs` can be
negative, producing strings like "-2 seconds ago."

**Fix:** Add a guard at the top of the function:
```typescript
if (diffSec < 0) return 'just now'
```

**Impact:** Minor display glitch possible with clock drift between server and client.

---

## 15. [LOW] OAuth Token Exchange Errors Are Swallowed

**Files:** `src/be/Mergician/Services/Authentication/GitLabOAuthService.cs` (lines ~36–62, ~64–88)

**Issue:** When `ExchangeCodeForToken` or `RefreshToken` fails, the error body from GitLab
(containing error codes like `invalid_client`, `invalid_grant`) is logged but the method
returns `null`. The caller only knows it failed, not why.

**Fix:** Return a result type or throw a typed exception:
```csharp
public record TokenExchangeResult(
    GitLabTokenResponse? Token,
    string? Error,
    string? ErrorDescription);
```
The `AuthController.Callback` can then include the error details in the redirect:
```
/error?message=GitLab+token+exchange+failed:+invalid_client
```

**Impact:** Easier debugging of auth configuration issues for administrators.

---

## 16. [LOW] `BranchRecord` Has Too Many Constructor Parameters

**File:** `src/be/Mergician/Entities/Database/BranchRecord.cs`

**Issue:** The positional record has 13 parameters, 5 with defaults. This makes construction
calls hard to read and easy to get wrong with positional arguments.

**Fix:** Convert to a record with property syntax for optional fields:
```csharp
public record BranchRecord(
    string BranchName,
    int ProjectId,
    string ProjectName,
    string ProjectNameWithNamespace)
{
    public bool? HasMergeRequest { get; init; }
    public int? ApprovalsRequired { get; init; }
    public int? ApprovalsGiven { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
    public string? MergeRequestTitle { get; init; }
    public string? MergeRequestUrl { get; init; }
    public string? ProjectUrl { get; init; }
    public List<BranchBuildJob>? BuildJobs { get; init; }
    public int? BranchInProjectId { get; init; }
}
```

**Impact:** Improved readability and prevention of argument-ordering bugs.

---

## Summary Table

| # | Priority | Category | Finding |
|---|----------|----------|---------|
| 1 | High | Security | Merge group IDOR — any user can view any merge group |
| 2 | High | Security | TLS validation disabled globally for GitLab HTTP client |
| 3 | Medium | Duplication | Duplicated TypeScript interfaces across views |
| 4 | Medium | Duplication | Duplicated polling lifecycle logic |
| 5 | Medium | Duplication | Duplicated CSS (card styles, skeleton shimmer) |
| 6 | Medium | Performance | Sequential GitLab API calls per branch |
| 7 | Medium | Type Safety | avatar_url vs avatarUrl serialization mismatch |
| 8 | Medium | Maintainability | Hardcoded NZ timezone in CleanupService |
| 9 | Medium | Testing | VersionTest checks source files instead of runtime |
| 10 | Low | Duplication | Duplicated login flow in integration tests |
| 11 | Low | Duplication | Duplicated JsonSerializerOptions instances |
| 12 | Low | Architecture | Duplicate branch deletion logic across services |
| 13 | Low | Maintainability | Repetitive test harness boilerplate |
| 14 | Low | Frontend | formatTimeAgo doesn't handle future timestamps |
| 15 | Low | Error Handling | OAuth token exchange errors swallowed as null |
| 16 | Low | Maintainability | BranchRecord has too many constructor parameters |
