# Mergician Code Review Recommendations

Date: 2026-02-20

## Scope Reviewed

- Backend: `src/be/Mergician` (controllers, authentication, GitLab services, database services, entities, config)
- Frontend: `src/fe/src` (app shell, dashboard view, composables, app bar)

Only recommendations with clear quality, maintainability, correctness, reliability, or non-trivial performance impact are listed below.

## Recommendations

### 1) Correct approval metric semantics in activity resolution (High)

**Where**
- `src/be/Mergician/Services/Gitlab/GitlabActivityService.cs` (approval mapping in `ResolveBranchActivity`, around line 370)
- `src/be/Mergician/Entities/GitLabApprovalState.cs`

**Issue**
- `ApprovalsRequired` is currently set from `approvalsGiven` (`ApprovalsRequired = approvalsGiven > 0 ? approvalsGiven : null`), which can report fully approved status even when required approvals are higher.

**Recommendation**
- Extend `GitLabApprovalState` to deserialize required-approval data from GitLab’s approvals payload and map `ApprovalsRequired` from that field, not from `ApprovedBy.Count`.

**Why it matters**
- Fixes a correctness bug in the dashboard’s core merge-readiness signal.

---

### 2) Implement full event pagination for GitLab activity fetches (High)

**Where**
- `src/be/Mergician/Services/Gitlab/GitlabService.cs` (`FetchEvents`, around line 80)

**Issue**
- Event fetch uses `per_page=100` but only reads one page. Active users can exceed this and silently lose events.

**Recommendation**
- Paginate through all event pages (using `X-Next-Page` or explicit page loop) until complete, then apply existing time filtering.

**Why it matters**
- Prevents missing branch activity and inconsistent dashboard state in higher-activity environments.

---

### 3) Clamp initial fetch window to 14 days when `lastPoll` is stale (High)

**Where**
- `src/be/Mergician/Services/Gitlab/GitlabActivityService.cs` (TODO near line 100)

**Issue**
- Initial stream uses `fetchSince = lastPoll ?? since`; if `lastPoll` is very old, fetches much larger history than intended.

**Recommendation**
- Use `fetchSince = lastPoll.HasValue && lastPoll.Value > since ? lastPoll.Value : since`.

**Why it matters**
- Reduces unnecessary GitLab calls and startup latency while matching the intended 14-day dashboard horizon.

---

### 4) Distinguish "branch missing" from API/auth/connectivity failures (High)

**Where**
- `src/be/Mergician/Services/Gitlab/GitlabService.cs` (`BranchExists`, around line 150)
- `src/be/Mergician/Services/Gitlab/GitlabActivityService.cs` (deletion paths)

**Issue**
- Any non-success response is treated as "branch does not exist", which can trigger incorrect DB deletions during transient GitLab/API failures.

**Recommendation**
- Treat 404 as "does not exist"; treat 401/403/5xx/network failures as operational errors (retry/log/skip deletion for that cycle).

**Why it matters**
- Protects data integrity and avoids accidental branch/group removals on transient outages.

---

### 5) Replace full-table branch scans during refresh deletions (Medium)

**Where**
- `src/be/Mergician/Services/Gitlab/GitlabActivityService.cs` (`StreamRefreshBranchStatus`, around line 208)
- `src/be/Mergician/Services/Database/IMergeGroupRepository.cs`
- `src/be/Mergician/Services/Database/MergeGroupRepository.cs`

**Issue**
- On branch-missing refresh events, code loads all branches and then searches in memory for one match.

**Recommendation**
- Add repository lookup by `(branchName, projectId)` and query only the needed row.

**Why it matters**
- Avoids repeated full-table reads in refresh loops and scales better with larger tracked branch sets.

---

### 6) Improve real-time resilience when SSE stream drops (Medium)

**Where**
- Frontend: `src/fe/src/views/HomeView.vue` (`eventSource.onerror`, around line 321)
- Backend: `src/be/Mergician/Controllers/ActivityController.cs` (SSE stream methods)

**Issue**
- On SSE error, frontend closes the stream but does not start fallback polling. Backend also emits no heartbeat events, increasing proxy idle-timeout risk.

**Recommendation**
- On SSE error, immediately switch to polling mode. Add periodic SSE heartbeat comments/events server-side.

**Why it matters**
- Prevents silent stale dashboards and improves reliability under real network/proxy conditions.

---

### 7) Consolidate duplicated current-user bootstrap fetches (Medium)

**Where**
- `src/fe/src/components/AppBar.vue` (`/api/auth/me`, around line 41)
- `src/fe/src/views/HomeView.vue` (`/api/auth/me`, around line 502)

**Issue**
- User identity is fetched independently in multiple components on initial load.

**Recommendation**
- Centralize auth/user bootstrap in a composable or store and share reactive state.

**Why it matters**
- Reduces duplicate network calls, keeps auth state consistent, and simplifies future auth/UI changes.

---

### 8) Replace anonymous controller response objects with typed DTOs (Medium)

**Where**
- `src/be/Mergician/Controllers/VersionController.cs` (`new { version = ... }`)
- `src/be/Mergician/Controllers/ActivityController.cs` (`new { error = ... }`)

**Issue**
- Anonymous response objects are less explicit and harder to evolve/document consistently.

**Recommendation**
- Introduce response records/POCOs under `Entities` for these payloads.

**Why it matters**
- Improves API contract clarity and aligns with existing repository coding standards.

---

### 9) Strengthen auth cookie policy for HTTPS deployments (Medium)

**Where**
- `src/be/Mergician/Controllers/AuthController.cs`
- `src/be/Mergician/Services/Authentication/GitLabCookieAuthenticationHandler.cs`

**Issue**
- Auth cookies do not explicitly set `Secure`; this is less robust for non-local HTTPS deployments.

**Recommendation**
- Set cookie `Secure` based on HTTPS (e.g., `Request.IsHttps` or forwarded scheme-aware configuration), and keep existing `HttpOnly` + `SameSite` settings.

**Why it matters**
- Improves session-token transport security without changing product behavior.

## Notes

- The codebase is generally clean and structured; no broad architectural rewrite is recommended.
- Prioritize items 1–4 first (correctness + reliability), then 5–9 for maintainability/resilience hardening.
