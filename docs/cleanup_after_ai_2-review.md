# Branch Review: `cleanup_after_ai_2`

Review of changes in this branch against `main`. Files changed:
- `src/be/Mergician/Services/AutoMerge/AutoMergeService.cs`
- `src/be/Mergician/Services/MergeQueueService.cs`
- `src/be/Mergician/Services/MonitoredProjectsService.cs`
- `src/be/Mergician/Services/MergeGroupManagementService.cs`
- `src/be/Mergician/Services/MergePermissionService.cs`
- `src/be/Mergician/Services/GitLab/GitLabPipelineService.cs`
- `src/be/Mergician/Services/GitLab/MergeRequestLookupService.cs`
- `src/be/Mergician/Entities/GitLabPipelineJob.cs`
- `docs/cleanup-branch-review.md` (deleted)

---

## Bugs

### 1. Exponential backoff condition is inverted — `AutoMergeService.cs` line 585

The condition controlling exponential backoff for repeated merge failures has the `!` removed, which breaks exponential backoff entirely for regular (non-permission-denied) errors.

**Current code (this branch):**
```csharp
// Reset exponential backoff when transitioning away from a permission-denied failure.
var currentBackoff = hasPriorState && current!.IsPermissionDenied
    ? current.Backoff
    : TimeSpan.Zero;
```

**Effect:** `currentBackoff` is non-zero only when the prior failure *was* permission-denied, meaning regular merge error backoff always resets to `_mergeBackoffInitial` on every cycle. Exponential doubling never occurs for regular errors.

**Expected condition** (restoring the `!`):
```csharp
var currentBackoff = hasPriorState && !current!.IsPermissionDenied
    ? current.Backoff
    : TimeSpan.Zero;
```

This correctly carries over the prior backoff for regular errors (enabling exponential growth), and resets to zero when transitioning *away* from a permission-denied failure — which is what the comment describes.

---

## Log Inaccuracies

### 2. Misleading log message in `MonitoredProjectsService.DisableLabelRemovedGroups`

The `DisableLabelRemovedGroups` method was simplified to check `group.Branches.Any(...)` over all branches, but the debug log on line 257 still says *"at least one monitored-project MR"*:

```csharp
_logger.LogDebug(
    "MonitoredProjectsService: merge group {MergeGroupId} '{MergeGroupName}' still has '{Label}' label on at least one monitored-project MR",
    ...);
```

Because `labeledBranches` is only populated from monitored projects, the functional result is the same — but the log message is now misleading since the code no longer explicitly filters for monitored project branches. The message should be updated to reflect that any branch in the group matched, e.g. *"still has '{Label}' label on at least one MR"*.

---

## Potential Behaviour Changes

### 3. Auto-rebase is now skipped when a group is not queue-eligible — `AutoMergeService.cs`

Previously, `EvaluateAndUpdateQueueMembership` was `void` and the auto-rebase step ran regardless of queue eligibility. Now the method returns `bool` and the caller does:

```csharp
if (!_mergeQueueService.EvaluateAndUpdateQueueMembership(group, branchMergeRequestDetails))
{
    return;
}
```

This means groups that are not queue-eligible (e.g. a branch with a broken pipeline, `draft_status`, etc.) will also skip the auto-rebase step. This is arguably the correct behaviour — rebasing branches against a blocked group wastes CI resources — but it is a behaviour change worth being aware of. Groups where `need_rebase` is the only blocking condition are unaffected since `need_rebase` is now included in `_allowedStatuses`.

### 4. `AutoMergeByLabel` label check no longer restricted to monitored projects — `AutoMergeService.ProcessAutoMerge`

Old code checked whether any MR from a **monitored project** in the group had the auto-merge label. The new code checks **all** MRs in the group:

```csharp
// Before
var monitoredProjectIds = _monitoredProjectRepository.GetAllProjectIds().ToHashSet();
var hasLabel = branchMergeRequestDetails.Any(x => monitoredProjectIds.Contains(x.Branch.ProjectId)
                                                  && x.MergeRequest.Labels.Contains(...));

// After
var hasLabel = branchMergeRequestDetails.Any(x => x.MergeRequest.Labels.Contains(...));
```

This is consistent with the `MonitoredProjectsService` simplification. A group with a mix of monitored and non-monitored project MRs can now trigger auto-merge via a label on any MR in the group. This change appears intentional.

---

## Minor Observations

### 5. Dead code path in `ProcessAutoMerge` — `AutoMergeService.cs`

The intra-group blocking feature is disabled by passing `[]` as `preComputedIntraGroupBlockedIds`. As a result, the logging block guarded by `if (preComputedIntraGroupBlockedIds.Count > 0)` and the early abort for `branchesToMergeNow.Count == 0` (circular dependency) are currently unreachable. These are intentionally left in place pending re-enablement, which is fine, but worth noting so they are not removed by accident during future cleanup.

### 6. `_allowedStatuses` / `_indeterminateStatuses` as instance fields — `MergeQueueService.cs`

These two `string[]` fields are constant sets that never change after construction. They would be better declared as `private static readonly` to make intent clear and avoid per-instance allocation. A `HashSet<string>` would also give O(1) lookup instead of O(n) linear scan, though the arrays are small enough that this is not a practical concern.

---

## Confirmed Correct Changes

- **Circular dependency handling**: Changed from silently merging all branches to logging an error and aborting. Correct improvement.
- **`ProcessAutoRebase` returns `bool`**: Now returns `false` immediately on rebase conflict (previously used `break` and then auto-merge still proceeded). This is a genuine bug fix.
- **`need_rebase` added to `_allowedStatuses`**: Allows groups with branches that need rebasing to remain queue-eligible, enabling auto-rebase to run within the queue framework.
- **Indeterminate status handling in `IsQueueEligible`**: Returning `null` for `checking`/`preparing`/`unchecked` statuses prevents premature queue decisions while GitLab is still computing merge state.
- **`MergeRequestLookupService` regex**: Adding `.*` at the end is harmless and makes explicit that trailing query strings or fragment identifiers in pasted MR URLs are tolerated.
- **`GitLabPipelineJob.Stage` removed**: No usages found anywhere in the codebase. Clean removal.
- **`MergePermissionService` consts inlined**: `MinMergeAccessLevel` and `MinViewAccessLevel` constants removed and their values (`GitLabAccessLevel.Developer` / `GitLabAccessLevel.Reporter`) used directly. No behaviour change; the enum values are self-documenting.
