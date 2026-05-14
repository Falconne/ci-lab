# Frontend Code Review — Mergician Vue.js App

> Reviewed: `src/fe/src/` (Vue 3 · Composition API · TypeScript · Vite)
>
> Focus: clean code, best practices, reliability, and maintainability.
> Trivialities (whitespace, import order, minor style) are excluded.

---

## Critical Issues

### 1. Race condition in auto-merge / auto-rebase settings sync

**File:** `src/fe/src/views/MergeGroupDetailsView.vue` (around the poll handler, `settingsUpdating` guard)

The poll response overwrites `autoMerge` / `autoRebase` / `autoMergeWarning` only when
`!settingsUpdating.value`. However `settingsUpdating` is set to `false` in the `finally`
block of the update request, so there is a window where:

1. User clicks the toggle → local state updates, `settingsUpdating = true`, request flies.
2. A concurrent poll fires, sees the *previous* `settingsUpdating = false` state.
3. Poll response arrives and overwrites the user's pending toggle before the save completes.

**Fix:** Track an in-flight update timestamp or version counter and only allow poll writes
when no update is newer than the poll response. Alternatively, suppress all poll-driven
settings writes while any settings request is queued *or* in-flight.

---

### 2. Memory leak — ResizeObserver instances accumulate on dynamic branch lists

**File:** `src/fe/src/components/MergeGroupCard.vue` (`setItemTitleRef` / `itemTitleObserver`)

A `ResizeObserver` is created once, but when branch items are removed from the list (which
happens on every poll cycle), `setItemTitleRef` is not called with `null` for the removed
elements. The observer continues watching detached DOM nodes. Over a long session this
accumulates leaked observers.

**Fix:** In `setItemTitleRef`, when called with `null` for a key that exists in the element
map, call `itemTitleObserver.unobserve(existingElement)` and remove the entry from the map
before returning.

---

### 3. Invalid date strings silently produce "Invalid Date" in the UI

**File:** `src/fe/src/utils/dateFormatting.ts`

```typescript
export function formatDateTime(isoString: string): string {
  if (!isoString) return ''
  return new Date(isoString).toLocaleString()   // "Invalid Date" if malformed
}
```

A falsy check does not catch empty strings that pass truthiness (`" "`) or malformed ISO
strings. `new Date(invalid).toLocaleString()` returns `"Invalid Date"` with no error.

**Fix:**
```typescript
export function formatDateTime(isoString: string): string {
  if (!isoString) return ''
  const date = new Date(isoString)
  if (isNaN(date.getTime())) return ''
  return date.toLocaleString()
}
```

---

### 4. Subscription toggle error leaves `subscriptionUpdating` permanently set

**File:** `src/fe/src/views/MergeGroupDetailsView.vue` (`toggleSubscription`)

If `response.ok` is true but `response.json()` throws (malformed JSON), the exception
bubbles out of the try-catch because the JSON parse sits inside the `if (response.ok)` block
but *outside* any inner try-catch. `subscriptionUpdating` is never cleared in the `finally`
block because the finally is unreachable, leaving the button permanently disabled.

**Fix:** Move the `response.json()` call inside the existing try-catch, or add a dedicated
inner try-catch around it so cleanup always runs.

---

## Reliability Issues

### 5. Polling loop can outlive the component on crash

**File:** `src/fe/src/composables/usePolling.ts`

`onUnmounted(stop)` is the only teardown mechanism. If the component throws during render
and Vue's error boundary prevents the unmount hook from firing, the polling loop keeps
running indefinitely with no component to receive results.

**Fix:** Introduce a closed-over `active` flag set to `false` in `onUnmounted`. Check it at
the top of each `loop()` iteration before scheduling the next tick. Alternatively, wrap the
entire composable in an `effectScope` tied to the component's lifecycle.

---

### 6. Failed navigation during startup leaves the UI in an ambiguous state

**File:** `src/fe/src/composables/useStartupCheck.ts`

When redirecting back home during a restart, a caught navigation failure is only `console.warn`-ed.
The user remains on a stale route (e.g. `/merge-group/123`) while the startup overlay
displays "Starting up…", which is contradictory and confusing.

**Fix:** Fall back to `window.location.href = '/'` if `router.replace` fails, so a clean
home state is guaranteed.

---

### 7. Drag-and-drop reorder sends empty list if queue disappears during drag

**File:** `src/fe/src/views/QueuesView.vue` (`onDragEnd`)

If a poll detects the queue was deleted (and sets `queueGroups.value = []`) between the
moment the user starts and ends a drag, `onDragEnd` will send an empty `orderedIds` array
to the backend. The backend likely silently accepts this, effectively deleting the queue
order.

**Fix:** Guard `onDragEnd` with a check:
```typescript
if (!queueGroups.value.length) return
```
Also consider disabling drag handles when a poll refresh is pending.

---

## Best Practices & Maintainability

### 8. Duplicate job deduplication logic across two components

**Files:** `src/fe/src/components/MergeGroupCard.vue`, `src/fe/src/components/MergeGroupGrid.vue`

The `STATUS_PRIORITY` array and `jobStatusPriority` function (used to de-duplicate build
jobs by status) are copy-pasted into both components. If the priority rules change, both
must be updated in sync.

**Fix:** Extract `deduplicateJobs(branches)` into `src/fe/src/utils/jobHelpers.ts` and
import it from both components.

---

### 9. `extractBackendError` duplicated and inconsistently applied

**Files:** `src/fe/src/views/MergeGroupDetailsView.vue`, `src/fe/src/components/DashboardFilter.vue`,
and other fetch call sites

`extractBackendError` is defined inline in `MergeGroupDetailsView.vue`. Similar but
subtly different patterns appear in other components. Some sites check `.error`, others
fall back to `response.statusText`, and some swallow errors silently.

**Fix:** Create `src/fe/src/utils/errorHelpers.ts` with a single exported
`extractBackendError(response, fallback)` utility and use it at every fetch error site.

---

### 10. Status codes used inconsistently — some constants, some magic numbers

**File:** `src/fe/src/utils/statusHelpers.ts`

`STATUS_LOADING = 0` and `STATUS_READY = 3` are declared at the top of the file but not
exported. Raw numeric literals (`0`, `1`, `2`, `3`) appear throughout `statusHelpers.ts`
and in components.

**Fix:** Replace with an exported numeric enum and use it everywhere:
```typescript
export enum MRStatus {
  Loading  = 0,
  Blocked  = 1,
  Waiting  = 2,
  Ready    = 3,
}
```

---

### 11. `groupStatusLabel` / `groupStatusClass` receive a synthetic partial object

**File:** `src/fe/src/views/MergeGroupDetailsView.vue` (computed properties `overallStatusLabel`, etc.)

```typescript
const overallStatusLabel = computed<string>(() =>
  groupStatusLabel({ branches: activities.value } as MergeGroup)
)
```

The `as MergeGroup` cast papers over a type mismatch — the helpers only need `branches` but
accept a full `MergeGroup`. This is a code smell that will break if the helper's
implementation ever accesses other `MergeGroup` fields.

**Fix:** Refactor the helper signatures to accept `branches: BranchWithActivity[]` directly,
removing the need for the cast.

---

### 12. `useViewMode` localStorage validation accepts any non-`'card'` value

**File:** `src/fe/src/composables/useViewMode.ts`

```typescript
const viewMode = ref<ViewMode>(stored === 'card' ? 'card' : 'grid')
```

This silently coerces any corrupted or unexpected localStorage value to `'grid'`. The type
`ViewMode` has exactly two valid values; an explicit check is safer:

```typescript
const viewMode = ref<ViewMode>(
  (stored === 'card' || stored === 'grid') ? stored : 'grid'
)
```

---

### 13. Deeply nested conditional template in branch card title

**File:** `src/fe/src/views/MergeGroupDetailsView.vue` (branch card template, ~180–271)

The branch card title block has four levels of nested `v-if` / `v-else` conditions covering
"has MR title + has MR URL", "has MR title + no URL", "no MR title", and various project
URL permutations. This is hard to read, diff, and test.

**Fix:** Extract a `BranchCardTitle.vue` component that accepts the branch `item` as a prop
and encapsulates all the conditional title/subtitle rendering.

---

### 14. `getMergeGroupId()` should be a computed property

**File:** `src/fe/src/views/MergeGroupDetailsView.vue`

```typescript
function getMergeGroupId(): string {
  const id = route.params.mergeGroupId
  return Array.isArray(id) ? id[0] : (id ?? '')
}
```

This is called multiple times in the view. Extracting it as a `computed` means it is
evaluated once per route change rather than on every call site:

```typescript
const mergeGroupId = computed<string>(() => {
  const id = route.params.mergeGroupId
  return Array.isArray(id) ? id[0] : (id ?? '')
})
```

---

### 15. Redundant `string | null` on optional fields in type definitions

**File:** `src/fe/src/types/mergeGroup.ts`

Several fields are declared as both optional (`?`) and nullable (`| null`):
```typescript
url?: string | null
mergeRequestTitle?: string | null
```

`undefined` and `null` are not equivalent in the API layer. Choose one pattern consistently:
- **Optional only** (`url?: string`) — the field may be absent from the object.
- **Required-nullable** (`url: string | null`) — the field is always present but may be null.

Mixing both forces every consumer to guard against three states: present, `undefined`, and `null`.

---

### 16. Hardcoded colour in subscription button breaks theming

**File:** `src/fe/src/views/MergeGroupDetailsView.vue` (`.subscription-untrack` CSS rule)

```css
.subscription-untrack {
  background-color: #000000 !important;
  color: white !important;
}
```

Hard-coded hex colours with `!important` bypass Vuetify's theming system. If a dark mode or
white-label theme is ever added, this button will be invisible or clash.

**Fix:** Use a Vuetify semantic colour prop (`color="surface-variant"` or similar) or a CSS
custom property tied to the active theme, and remove the `!important` overrides.

---

### 17. Clipboard error message is not actionable

**File:** `src/fe/src/components/DashboardFilter.vue` (clipboard read error handler)

```typescript
openMRError.value = 'Could not read from clipboard. Please paste manually.'
```

The message tells the user to "paste manually" — but they just tried to paste and it failed.
The real cause (permission denied, insecure context, no clipboard API) is swallowed.

**Fix:**
```typescript
openMRError.value = 'Clipboard access was denied. Please type or paste the URL directly into the field.'
```

---

## Summary

| Category | Count |
|---|---|
| Critical (race condition / memory / data loss) | 4 |
| Reliability | 3 |
| Best practices & maintainability | 10 |

### Highest-priority fixes

1. **Race condition in settings sync** — user toggles can be silently overwritten by a poll.
2. **ResizeObserver memory leak** — degrades performance in long-running sessions.
3. **`subscriptionUpdating` never cleared on JSON parse error** — permanently disables the button.
4. **Duplicate job deduplication logic** — maintenance hazard; extract to a shared util.
5. **Date formatting does not validate** — produces visible "Invalid Date" text on bad data.
