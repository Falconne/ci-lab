# Mergician Frontend Code Review — Actionable Suggestions

---

## 1. Logic Bugs

### 1.1 `itemStatusLabel` returns "Waiting" during loading state (false positive)

**Files:** `src/views/MergeGroupDetailsView.vue` (line 387)

**Problem:** The function uses `if (!item.hasMergeRequest)` which is `true` for both `null` (data still loading) and `false` (no MR exists). During the loading phase — when `hasMergeRequest` is `null` — every branch shows "Waiting" status with a warning-colored chip. The status should only be "Waiting" when `hasMergeRequest === false`.

The `v-chip` at line 196 gates on `item.hasMergeRequest !== null`, so the chip is hidden during loading, but `itemStatusLabel` is also called by `itemStatusClass` (line 362) which controls the accent bar color. This means the accent bar shows orange/waiting color for all branches during loading, even those that will resolve to "Ready".

Additionally, `overallStatusLabel` (line 348) maps statuses and calls `itemStatusLabel` for all items — during loading, every item returns "Waiting", making the overall status "Waiting" even when the guard at line 50 (`v-if="isFullyLoaded"`) hides it.

**Fix:** Use strict equality check:

```typescript
function itemStatusLabel(item: BranchWithActivity): string {
  if (item.hasMergeRequest === null) {
    return 'Loading'
  }
  if (item.hasMergeRequest === false) {
    return 'Waiting'
  }
  if (item.approvalsRequired != null && item.approvalsGiven != null) {
    return item.approvalsGiven >= item.approvalsRequired ? 'Ready' : 'Open'
  }
  return 'Open'
}
```

Also update `itemStatusClass` and `itemStatusColor` to handle the new `'Loading'` value, mapping it to a neutral color (e.g. `'grey'`).

---

### 1.2 `updateSettings` reverts both toggles on failure, even if only one changed

**Files:** `src/views/MergeGroupDetailsView.vue` (lines 540–541)

**Problem:** When the PUT request fails, the code does:
```typescript
autoMerge.value = !newAutoMerge
autoRebase.value = !newAutoRebase
```
This inverts **both** values regardless of which one the user actually toggled. For example, if `autoMerge` was `true` and `autoRebase` was `true`, and the user toggles only `autoRebase` off (sending `autoMerge=true, autoRebase=false`), a failure reverts to `autoMerge=false, autoRebase=true` — the exact opposite of the correct previous state.

**Fix:** Capture the previous values before the request and restore them on failure:

```typescript
async function updateSettings(newAutoMerge: boolean, newAutoRebase: boolean) {
  const mergeGroupId = getMergeGroupId()
  if (!mergeGroupId) return

  const prevAutoMerge = autoMerge.value
  const prevAutoRebase = autoRebase.value

  settingsUpdating.value = true
  try {
    const response = await fetchBackend(`/api/merge-groups/${mergeGroupId}/settings`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ autoMerge: newAutoMerge, autoRebase: newAutoRebase })
    })

    if (!response.ok) {
      console.error('Failed to update settings, status', response.status)
      errorMessage.value = 'Failed to update auto merge settings.'
      autoMerge.value = prevAutoMerge
      autoRebase.value = prevAutoRebase
      return
    }

    const data: MergeGroup = await response.json()
    autoMerge.value = data.autoMerge
    autoRebase.value = data.autoRebase
    autoMergeWarning.value = data.autoMergeWarning
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('Failed to update settings:', err)
    errorMessage.value = 'Failed to update auto merge settings.'
    autoMerge.value = prevAutoMerge
    autoRebase.value = prevAutoRebase
  } finally {
    settingsUpdating.value = false
  }
}
```

---

### 1.3 "Open MR as Merge Group" button is invisible while loading

**Files:** `src/views/HomeView.vue` (lines 80–91)

**Problem:** The button has `v-if="... && !openMrLoading"` AND `:loading="openMrLoading"`. Since the `v-if` removes the button from the DOM when `openMrLoading` is `true`, the user never sees the Vuetify loading spinner. The button simply disappears on click with no feedback.

**Fix:** Remove `!openMrLoading` from the `v-if` condition so the button remains visible with its loading spinner:

```html
<v-btn
  v-if="isMrUrlFilter && filteredMergeGroups.length === 0"
  color="primary"
  variant="flat"
  size="small"
  prepend-icon="mdi-open-in-app"
  class="ml-2 text-none open-mr-btn"
  :loading="openMrLoading"
  :disabled="openMrLoading"
  @click="openMrAsGroup"
>
  Open MR as Merge Group
</v-btn>
```

---

### 1.4 Potential infinite reload loop on `vite:preloadError`

**Files:** `src/main.ts` (lines 8–11)

**Problem:** The handler unconditionally calls `window.location.reload()` when a chunk preload fails. If the new deployment is also broken (e.g. incomplete upload, CDN failure), this causes an infinite reload loop.

**Fix:** Use `sessionStorage` to debounce the reload:

```typescript
window.addEventListener('vite:preloadError', (event) => {
  const lastReload = sessionStorage.getItem('mergician-chunk-reload')
  const now = Date.now()
  if (lastReload && now - parseInt(lastReload, 10) < 10_000) {
    console.error('[Mergician] Chunk preload failed after recent reload — not reloading again', event)
    return
  }
  console.warn('[Mergician] Chunk preload failed — reloading to pick up new version', event)
  sessionStorage.setItem('mergician-chunk-reload', now.toString())
  window.location.reload()
})
```

---

### 1.5 Concurrent poll requests can overlap and cause state races

**Files:** `src/views/HomeView.vue` (`pollDashboard`), `src/views/MergeGroupDetailsView.vue` (`pollMergeGroup`)

**Problem:** `setInterval` fires on a fixed cadence regardless of whether the previous request completed. During the fast-poll phase (1 s interval), if a request takes longer than 1 s, multiple requests run concurrently. Their responses arrive out of order and the last one to resolve "wins", potentially replacing newer data with stale data.

**Fix:** Add a guard (same pattern used by `useStartupCheck.refreshStartupStatus`):

```typescript
let pollInProgress = false

async function pollDashboard() {
  if (pollInProgress) return
  pollInProgress = true
  try {
    // ... existing poll logic
  } finally {
    pollInProgress = false
  }
}
```

Apply the same pattern to `pollMergeGroup` in `MergeGroupDetailsView.vue`.

---

## 2. DRY / Maintainability

### 2.1 Extract shared TypeScript interfaces into a dedicated types file

**Files:** `src/views/HomeView.vue` (lines 245–281), `src/views/MergeGroupDetailsView.vue` (lines 283–312)

**Problem:** `BranchWithActivity`, `BranchBuildJob`, and `MergeGroup` are defined identically in both views. Any field change must be applied in two places.

**Fix:** Create `src/types/mergeGroup.ts`:

```typescript
export interface BranchBuildJob {
  name: string
  status: string
  url?: string | null
}

export interface BranchWithActivity {
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
  id: number
}

export interface MergeGroup {
  id: number
  name: string
  branches: BranchWithActivity[]
  autoMerge: boolean
  autoRebase: boolean
  autoMergeWarning: string | null
}
```

Then import from both views: `import type { MergeGroup, BranchWithActivity } from '@/types/mergeGroup'`.

`GroupPartition` and `GroupStatus` can stay in `HomeView.vue` since they're only used there.

---

### 2.2 Extract polling logic into a reusable composable

**Files:** `src/views/HomeView.vue` (lines 283–286, 568–601), `src/views/MergeGroupDetailsView.vue` (lines 314–316, 340–341, 686–719)

**Problem:** Both views implement the exact same fast-poll → normal-poll lifecycle with identical constants (`FAST_POLL_INTERVAL_MS`, `NORMAL_POLL_INTERVAL_MS`, `FAST_POLL_DURATION_MS`), identical `startPolling` / `stopPolling` functions, and identical `onUnmounted` cleanup.

**Fix:** Create `src/composables/usePolling.ts`:

```typescript
import { ref, onUnmounted } from 'vue'
import { useAppLoading } from '@/composables/useAppLoading'

interface UsePollingOptions {
  fastIntervalMs?: number
  normalIntervalMs?: number
  fastDurationMs?: number
}

export function usePolling(pollFn: () => Promise<void>, options: UsePollingOptions = {}) {
  const {
    fastIntervalMs = 1000,
    normalIntervalMs = 5000,
    fastDurationMs = 5000,
  } = options

  const { setAppLoading } = useAppLoading()
  const initialPhase = ref(false)

  let pollIntervalId: ReturnType<typeof setInterval> | null = null
  let fastPollTimeoutId: ReturnType<typeof setTimeout> | null = null
  let pollInProgress = false

  async function guardedPoll() {
    if (pollInProgress) return
    pollInProgress = true
    try {
      await pollFn()
    } finally {
      pollInProgress = false
    }
  }

  function start() {
    if (pollIntervalId !== null) return
    initialPhase.value = true
    setAppLoading(true)
    pollIntervalId = setInterval(guardedPoll, fastIntervalMs)

    fastPollTimeoutId = setTimeout(() => {
      initialPhase.value = false
      setAppLoading(false)
      if (pollIntervalId !== null) {
        clearInterval(pollIntervalId)
        pollIntervalId = setInterval(guardedPoll, normalIntervalMs)
      }
      fastPollTimeoutId = null
    }, fastDurationMs)

    guardedPoll()
  }

  function stop() {
    if (pollIntervalId !== null) {
      clearInterval(pollIntervalId)
      pollIntervalId = null
    }
    if (fastPollTimeoutId !== null) {
      clearTimeout(fastPollTimeoutId)
      fastPollTimeoutId = null
    }
    setAppLoading(false)
  }

  onUnmounted(stop)

  return { initialPhase, start, stop }
}
```

Usage in views: `const { initialPhase, start: startPolling, stop: stopPolling } = usePolling(pollDashboard)`.

---

### 2.3 Extract duplicated status CSS into a shared stylesheet

**Files:** `src/views/HomeView.vue` (lines 862–897), `src/views/MergeGroupDetailsView.vue` (lines 771–797, 812–820)

**Problem:** The following CSS blocks are duplicated verbatim between both views:
- `.card-status-badge` and `.status-dot` rules
- `.status-ready`, `.status-open`, `.status-waiting` color rules
- `.card-accent` and its status variants
- `.card-body` styles
- Skeleton shimmer `@keyframes` and `.skeleton-shimmer`, `.skeleton-badge` rules

**Fix:** Create `src/assets/status-badges.css` (or `.scss`) with the shared styles, and import it in both views:

```vue
<style scoped>
@import '@/assets/status-badges.css';
/* ... view-specific styles only ... */
</style>
```

Note: Because `scoped` styles are transformed per-component, importing a shared file into `<style scoped>` still scopes each component's copy. This is the correct approach for shared styles that should remain scoped.

---

### 2.4 Extract status helper functions into a shared module

**Files:** `src/views/HomeView.vue` (lines 323–374), `src/views/MergeGroupDetailsView.vue` (lines 347–435)

**Problem:** Both views implement variants of:
- `itemApprovalsText` — behaves differently between views (HomeView returns `''`, Details returns `'Not available'`)
- `approvalIconColor` — only in HomeView
- `approvalsTooltip` — only in HomeView
- `jobStatusIcon` / `jobStatusColor` — only in MergeGroupDetailsView
- `getGroupStatus` / `groupStatusLabel` / `groupStatusClass` — only in HomeView but the concept is replicated in MergeGroupDetailsView's `overallStatusLabel`/`overallStatusClass`

**Fix:** Create `src/utils/statusHelpers.ts` to hold the shared logic. Each view can import what it needs and pass view-specific defaults (like the "not available" text) as parameters:

```typescript
import type { BranchWithActivity } from '@/types/mergeGroup'

export function getItemApprovalsText(
  item: BranchWithActivity,
  fallback = ''
): string {
  if (!item.hasMergeRequest || item.approvalsGiven == null || item.approvalsRequired == null) {
    return fallback
  }
  return `${item.approvalsGiven}/${item.approvalsRequired}`
}

export function getApprovalIconColor(item: BranchWithActivity): string {
  if (!item.hasMergeRequest || item.approvalsGiven == null || item.approvalsRequired == null) {
    return 'grey'
  }
  return item.approvalsGiven >= item.approvalsRequired ? 'green' : 'grey'
}

export function getJobStatusIcon(status: string): string {
  const normalized = status.toLowerCase()
  if (normalized === 'success') return 'mdi-check-circle'
  if (normalized === 'failed' || normalized === 'failure') return 'mdi-close-circle'
  if (normalized === 'running') return 'mdi-progress-clock'
  if (normalized === 'pending') return 'mdi-timer-sand'
  if (normalized === 'canceled' || normalized === 'cancelled') return 'mdi-cancel'
  return 'mdi-help-circle'
}

export function getJobStatusColor(status: string): string {
  const normalized = status.toLowerCase()
  if (normalized === 'success') return 'success'
  if (normalized === 'failed' || normalized === 'failure') return 'error'
  if (normalized === 'running') return 'info'
  if (normalized === 'pending') return 'warning'
  if (normalized === 'canceled' || normalized === 'cancelled') return 'secondary'
  return 'default'
}
```

---

### 2.5 Extract `formatTimeAgo` and `formatDateTime` into a shared utility

**Files:** `src/views/HomeView.vue` (lines 494–512)

**Problem:** Date formatting logic is embedded directly in the view's `<script setup>`. If the details view ever needs relative time display (or a third view is added), the logic would need to be duplicated again. These are pure functions with no component dependencies.

**Fix:** Create `src/utils/dateFormatting.ts`:

```typescript
export function formatDateTime(isoString: string): string {
  if (!isoString) return ''
  return new Date(isoString).toLocaleString()
}

export function formatTimeAgo(isoString: string, now: number): string {
  if (!isoString) return ''
  const date = new Date(isoString)
  const diffMs = now - date.getTime()
  const diffSec = Math.floor(diffMs / 1000)

  if (diffSec < 60) return diffSec === 1 ? '1 second ago' : `${diffSec} seconds ago`
  const diffMin = Math.floor(diffSec / 60)
  if (diffMin < 60) return diffMin === 1 ? '1 minute ago' : `${diffMin} minutes ago`
  const diffHour = Math.floor(diffMin / 60)
  if (diffHour < 24) return diffHour === 1 ? '1 hour ago' : `${diffHour} hours ago`
  const diffDay = Math.floor(diffHour / 24)
  return diffDay === 1 ? '1 day ago' : `${diffDay} days ago`
}
```

Pass `now.value` from the view when calling `formatTimeAgo`.

---

## 3. Vue 3 / Composition API Best Practices

### 3.1 `loadSubscription()` is fire-and-forget in `onMounted` — errors silently swallowed

**Files:** `src/views/MergeGroupDetailsView.vue` (line 747)

**Problem:** `loadSubscription()` is called without `await` and without `.catch()`. If it throws (e.g. network error that isn't a `StartupRequiredError`), the rejection goes unhandled. The `try/catch` inside `loadSubscription` catches most errors, but if a future refactor adds a throwing path before the `try`, it would be silent.

**Fix:** Either `await` it or chain `.catch()`:

```typescript
onMounted(async () => {
  // ...
  startPolling()
  await loadSubscription()
})
```

Or if you specifically want it non-blocking:

```typescript
loadSubscription().catch(err => {
  console.error('[Mergician] Failed to load subscription:', err)
})
```

---

### 3.2 `useAppLoading` uses module-level state without `provide`/`inject`

**Files:** `src/composables/useAppLoading.ts`

**Problem:** The `loading` ref is a module-level singleton. This works because there's only one app instance, but it violates Vue 3 conventions for shared state. If the app were ever SSR-rendered or tested in isolation, shared module state would leak between requests/tests.

**Fix (low priority):** This is acceptable for a single-instance SPA. No code change needed now, but consider adding a comment noting that this is intentionally a global singleton to avoid confusion during future maintenance.

---

### 3.3 `useStartupCheck` mixes composable pattern with exported standalone functions

**Files:** `src/composables/useStartupCheck.ts`

**Problem:** The module exports both:
- A composable (`useStartupCheck()`) that returns reactive readonly refs
- Standalone functions (`enterStartupMode`, `isStartupReady`, `refreshStartupStatus`) that directly mutate module-level state

This dual pattern makes it unclear which function should be used where and creates implicit coupling between `useBackendFetch.ts` and the startup module's internal state.

**Fix:** This is a design concern, not a bug. Consider documenting the intent at the top of the file with a comment explaining that the standalone exports exist specifically for `useBackendFetch.ts` to call from outside a component context:

```typescript
/**
 * Startup state management.
 *
 * This module exports both a composable (useStartupCheck) for components
 * and standalone functions (enterStartupMode, isStartupReady) for use by
 * non-component modules like useBackendFetch.
 */
```

---

## 4. TypeScript Best Practices

### 4.1 API responses are not validated — `response.json()` returns `any`

**Files:** `src/views/HomeView.vue` (line 541), `src/views/MergeGroupDetailsView.vue` (lines 450, 470, 545, 650), `src/components/AppBar.vue` (line 81)

**Problem:** Throughout the codebase, `response.json()` is called and either assigned directly to a typed variable or used with property access, with no runtime validation. If the backend changes its response shape, the frontend silently works with `undefined` properties instead of failing with a useful error.

Examples:
```typescript
const data: MergeGroup[] = await response.json() // No validation
const data: MergeGroup = await response.json()   // No validation
const data = await response.json()               // Implicitly `any`
```

**Fix:** At minimum, add type assertions at API boundaries. For critical data, add a lightweight validation check:

```typescript
const data: MergeGroup[] = await response.json()
if (!Array.isArray(data)) {
  console.error('[Mergician] Unexpected dashboard response shape', data)
  return
}
```

For the `backendVersion` in AppBar:

```typescript
const data = await response.json() as { version?: string }
backendVersion.value = data.version || 'unknown'
```

---

### 4.2 `__APP_VERSION__` declared in two different files

**Files:** `src/version.d.ts`, `src/composables/useVersionCheck.ts` (line 3)

**Problem:** The global `__APP_VERSION__` is declared as `declare const` in both `src/version.d.ts` and re-declared locally inside `useVersionCheck.ts`. The local declaration shadows or duplicates the global one.

**Fix:** Remove the `declare const __APP_VERSION__: string` line from `useVersionCheck.ts` since `version.d.ts` already provides it globally.

---

### 4.3 Use `as const` for status string maps to improve type narrowing

**Files:** `src/views/MergeGroupDetailsView.vue` (lines 417–435)

**Problem:** `jobStatusIcon` and `jobStatusColor` use chains of `if` statements returning string literals. A `Record` map with `as const` would be more maintainable and type-safe.

**Fix:**

```typescript
const JOB_STATUS_ICONS: Record<string, string> = {
  success: 'mdi-check-circle',
  failed: 'mdi-close-circle',
  failure: 'mdi-close-circle',
  running: 'mdi-progress-clock',
  pending: 'mdi-timer-sand',
  canceled: 'mdi-cancel',
  cancelled: 'mdi-cancel',
}

export function getJobStatusIcon(status: string): string {
  return JOB_STATUS_ICONS[status.toLowerCase()] ?? 'mdi-help-circle'
}
```

Same pattern for `jobStatusColor`.

---

## 5. Performance

### 5.1 `now` ref updates every 60s but `formatTimeAgo` shows seconds precision

**Files:** `src/views/HomeView.vue` (lines 300, 505, 663)

**Problem:** The `now` ref updates every 60 seconds (`setInterval(() => { now.value = Date.now() }, 60000)`), but `formatTimeAgo` renders at seconds granularity (e.g. "5 seconds ago"). This means a timestamp from 5 seconds ago displays as "5 seconds ago" and stays frozen for the next 55 seconds until the next tick, showing "65 seconds ago" → "1 minute ago". This creates a jarring jump.

**Fix:**:
**Lower the interval to 10 seconds** for more natural updates (minimal performance cost):
   ```typescript
   timeIntervalId = setInterval(() => { now.value = Date.now() }, 10000)
   ```

---

### 5.2 `getPartitionKey` re-creates `Date` objects in a computed that runs on every reactivity change

**Files:** `src/views/HomeView.vue` (lines 451–466)

**Problem:** `getPartitionKey` creates `new Date()` and calls `setHours()` on every invocation. Since it's called once per group inside `partitionedGroups` (a computed), this runs every time any dependency changes (including the `now` ref, which triggers every 60s). For a large number of groups, this creates unnecessary `Date` allocations.

**Fix:** Compute `todayMidnight` once outside the loop in `partitionedGroups`:

```typescript
const partitionedGroups = computed<GroupPartition[]>(() => {
  const todayMidnight = new Date()
  todayMidnight.setHours(0, 0, 0, 0)
  const todayMs = todayMidnight.getTime()

  function getPartitionKey(group: MergeGroup): string {
    const ts = groupLatestTimestamp(group)
    if (!ts) return 'today'
    const groupDate = new Date(ts)
    groupDate.setHours(0, 0, 0, 0)
    const daysAgo = Math.floor((todayMs - groupDate.getTime()) / 86400000)
    if (daysAgo === 0) return 'today'
    if (daysAgo === 1) return 'yesterday'
    if (daysAgo < 7) return 'last7days'
    return 'older'
  }

  // ... rest of partitionedGroups logic
})
```

---

## 6. Accessibility

### 6.1 Merge group cards in `HomeView` are non-focusable, non-keyboard-accessible

**Files:** `src/views/HomeView.vue` (lines 122–228)

**Problem:** Each merge group card is a `<div>` with `@click="openMergeGroupDetails(group)"` and `cursor: pointer`. Keyboard users cannot focus or activate these cards. Screen readers do not announce them as interactive elements.

**Fix:** Add `tabindex="0"`, `role="link"`, an `aria-label`, and a `@keydown.enter` handler:

```html
<div
  v-for="group in partition.groups"
  :key="group.id.toString()"
  class="merge-group-card"
  :data-merge-group-id="group.id"
  role="link"
  tabindex="0"
  :aria-label="`Merge group ${group.name}`"
  @click="openMergeGroupDetails(group)"
  @keydown.enter="openMergeGroupDetails(group)"
>
```

---

### 6.2 AppBar title area uses `@click` on a `<div>` without keyboard support

**Files:** `src/components/AppBar.vue` (line 6)

**Problem:** The `.app-title-link` div has `@click="goHome"` and `cursor: pointer` but is not focusable or keyboard-activatable. This should be a link or button for accessibility.

**Fix:** Use a `<router-link>` or `<a>` tag instead:

```html
<router-link to="/" class="d-flex align-center app-title-link text-white" style="text-decoration: none;">
  <v-icon icon="mdi-source-merge" class="mr-2" />
  Mergician
  <v-divider vertical class="mx-4 title-divider" />
  <span class="page-title">{{ pageTitle }}</span>
</router-link>
```

Remove the `goHome` function since `<router-link>` handles navigation natively with full keyboard and screen reader support.

---

### 6.3 Document title is never updated per route

**Files:** `src/router/index.ts`, `src/App.vue`

**Problem:** The `<title>` is always "Mergician" regardless of which page the user is on. Screen reader users and tab-switchers can't distinguish between the dashboard and a specific merge group.

**Fix:** Add a global `afterEach` guard in the router:

```typescript
router.afterEach((to) => {
  const title = to.meta?.title as string | undefined
  document.title = title ? `${title} — Mergician` : 'Mergician'
})
```

For the merge group details page, update the title when the merge group name is known (in `MergeGroupDetailsView.vue`):

```typescript
// Inside pollMergeGroup, after updating mergeGroupName:
document.title = `${data.name} — Mergician`
```

---

### 6.4 Icon-only buttons lack accessible labels

**Files:** `src/views/HomeView.vue` (lines 63–72 paste button, 146–158 open-in-new button)

**Problem:** Several icon-only buttons have visual tooltips but no `aria-label`. The "open in new tab" button has `title="Open in new tab"` which works but tooltips are not consistently applied.

**Fix:** Add `aria-label` to all icon-only buttons:

```html
<v-btn
  icon
  size="x-small"
  variant="text"
  color="grey"
  class="paste-btn"
  aria-label="Paste from clipboard"
  @click="pasteFromClipboard"
>
```

---

## 7. Error Handling

### 7.1 `pollDashboard` silently stops polling on 503 with no recovery path

**Files:** `src/views/HomeView.vue` (lines 530–534), `src/views/MergeGroupDetailsView.vue` (lines 639–643)

**Problem:** When the backend returns 503, polling is permanently stopped and the user sees "Database is unavailable" with no retry mechanism. The user must manually reload the page.

**Fix:** Instead of stopping polling, show the error but continue polling so the dashboard self-heals when the database recovers:

```typescript
if (response.status === 503) {
  errorMessage.value = 'Database is temporarily unavailable. Retrying...'
  return // Don't stop polling — let the next interval retry
}
```

---

### 7.2 No catch-all / 404 route

**Files:** `src/router/index.ts`

**Problem:** If a user navigates to an unknown path (e.g. `/settings`, `/nonexistent`), the router matches nothing and renders a blank page inside `<v-main>`.

**Fix:** Add a catch-all route:

```typescript
{
  path: '/:pathMatch(.*)*',
  name: 'not-found',
  redirect: '/',
}
```

Or create a proper `NotFoundView.vue` component if you want to show a user-friendly 404 message instead of silently redirecting.

---

## 8. Security

### 8.1 Clipboard read via `navigator.clipboard.readText()` should verify focus

**Files:** `src/views/HomeView.vue` (lines 622–629)

**Problem:** `navigator.clipboard.readText()` requires the document to have focus and the user to have granted permission. The `catch` block handles failures silently, which is fine, but the user gets no indication that the paste failed (e.g. if they denied the permission prompt).

**Fix:** Show a brief non-intrusive error to the user on failure:

```typescript
async function pasteFromClipboard() {
  try {
    const text = await navigator.clipboard.readText()
    filterText.value = text
  } catch (err) {
    console.warn('[Mergician] Failed to read from clipboard:', err)
    errorMessage.value = 'Could not read from clipboard. Please paste manually.'
  }
}
```

---

### 8.2 Error messages from backend API responses rendered without sanitization

**Files:** `src/views/HomeView.vue` (line 651), `src/views/MergeGroupDetailsView.vue` (line 512)

**Problem:** Error messages from the backend (`data?.error`) are rendered into `v-alert` via text interpolation (`{{ openMrError }}`). Vue's template interpolation auto-escapes HTML, so this is **not** an XSS vulnerability. However, if a future change uses `v-html` instead, it would become one. No code change is needed now, but note: **never use `v-html`** to render backend error messages.

---

## 9. Code Organization

### 9.1 Views are too large — `HomeView.vue` is 1047 lines

**Files:** `src/views/HomeView.vue`, `src/views/MergeGroupDetailsView.vue`

**Problem:** Both view files exceed 700 lines with template, script, and style all in one file. The template sections contain deeply nested card layouts that could be extracted into components.

**Fix:** Extract the merge group card into a `MergeGroupCard.vue` component:

```
src/components/
  MergeGroupCard.vue       — card for dashboard (used by HomeView)
  BranchDetailCard.vue     — card for merge group details page
```

This would:
- Move ~100 lines of template out of each view
- Move the card-specific CSS with it
- Make the card independently testable and reusable

The views would become orchestrators (data fetching, routing) while components handle presentation.

---

### 9.2 Magic number: `truncateTitle` uses 222 as a cutoff

**Files:** `src/views/HomeView.vue` (lines 392–395)

**Problem:** The number `222` is used without explanation. It's not clear why this specific value was chosen.

**Fix:** Extract it as a named constant with a comment:

```typescript
/** Max characters for MR title display in dashboard cards before truncation. */
const MAX_TITLE_LENGTH = 222

function truncateTitle(title: string): string {
  if (title.length <= MAX_TITLE_LENGTH) return title
  return title.slice(0, MAX_TITLE_LENGTH) + '…'
}
```

(Also use the proper ellipsis character `…` instead of three dots `...`.)

---

### 9.3 `useCurrentUser` composable exposes functions alongside refs inconsistently

**Files:** `src/composables/useCurrentUser.ts`

**Problem:** The `loadCurrentUser` and `clearCurrentUser` functions are defined outside the `useCurrentUser` function at module scope, then returned from the composable. While this works (they close over the module-level refs), it's inconsistent — some composables define functions inside the composable function, others outside.

**Fix (low priority):** This is cosmetic. Consider keeping the current pattern but adding a comment:

```typescript
// Functions are defined at module scope to avoid re-creation on each useCurrentUser() call,
// while still being returned from the composable for a clean API.
```

---

## 10. Vuetify Best Practices

### 10.1 Hardcoded color values in scoped CSS instead of Vuetify theme variables

**Files:** `src/views/HomeView.vue` (lines 788–797, 880–887), `src/views/MergeGroupDetailsView.vue` (lines 790–797, 818–820)

**Problem:** Colors like `#4caf50`, `#1976d2`, `#fb8c00`, `#e8f5e9`, `#e3f2fd`, `#fff3e0` are hardcoded in CSS. These are Vuetify's Material Design colors but are manually duplicated rather than referenced from the theme. If the theme changes, these custom cards won't update.

**Fix:** Use Vuetify's CSS custom properties or utility classes where possible. For the accent bar and status badges, consider using Vuetify's `rgb(var(--v-theme-success))` syntax:

```css
.card-accent.status-ready { background: rgb(var(--v-theme-success)); }
.card-accent.status-open { background: rgb(var(--v-theme-info)); }
.card-accent.status-waiting { background: rgb(var(--v-theme-warning)); }
```

If the exact shades need to differ from the theme, define them as custom theme colors in `main.ts`.

---

### 10.2 Use Vuetify's `v-card` instead of raw `<div>` with manual card CSS

**Files:** `src/views/HomeView.vue` (lines 122–228 card structure), `src/views/MergeGroupDetailsView.vue` (lines 159–269)

**Problem:** Both views implement card layouts using raw `<div>` elements with extensive custom CSS for borders, shadows, hover effects, and border-radius. Vuetify's `v-card` component provides all of this out of the box with proper theming, elevation, and hover support.

**Fix (low priority):** This is a larger refactor. The custom cards have specific design requirements (left accent bar, multi-column grid) that may justify custom CSS. However, consider using `v-card` as the base and overriding only the accent bar:

```html
<v-card
  v-for="group in partition.groups"
  :key="group.id.toString()"
  class="merge-group-card"
  :ripple="false"
  hover
  @click="openMergeGroupDetails(group)"
>
  <div class="card-accent" :class="groupStatusClass(group)" />
  <!-- ... card content ... -->
</v-card>
```

This would give you proper focus management, hover elevation, and keyboard support (`v-card` handles `tabindex` and `Enter`/`Space` key events automatically when `@click` is present).

---

## Summary of Priorities

| Priority | Item | Category |
|----------|------|----------|
| **P0 — Bug** | 1.2 `updateSettings` reverts both toggles incorrectly | Logic Bug |
| **P0 — Bug** | 1.1 `itemStatusLabel` returns "Waiting" during loading | Logic Bug |
| **P0 — Bug** | 1.3 "Open MR" button invisible during loading | Logic Bug |
| **P1 — Reliability** | 1.4 Infinite reload loop on chunk preload failure | Error Handling |
| **P1 — Reliability** | 1.5 Concurrent poll requests can race | Error Handling |
| **P1 — Reliability** | 7.1 503 permanently stops polling with no recovery | Error Handling |
| **P1 — DRY** | 2.1 Duplicated interfaces across views | Maintainability |
| **P1 — DRY** | 2.2 Duplicated polling logic | Maintainability |
| **P1 — DRY** | 2.3 Duplicated status CSS | Maintainability |
| **P2 — DRY** | 2.4 Duplicated status helper functions | Maintainability |
| **P2 — DRY** | 2.5 Inline date formatting functions | Maintainability |
| **P2 — A11y** | 6.1 Cards not keyboard-accessible | Accessibility |
| **P2 — A11y** | 6.2 AppBar title not a link/button | Accessibility |
| **P2 — A11y** | 6.3 Document title never updates | Accessibility |
| **P2 — A11y** | 6.4 Icon buttons lack aria-labels | Accessibility |
| **P2 — UX** | 5.1 `now` refresh cadence vs display granularity mismatch | Performance |
| **P3 — Quality** | 7.2 No catch-all route | Error Handling |
| **P3 — Quality** | 4.1 No runtime validation of API responses | TypeScript |
| **P3 — Quality** | 4.2 `__APP_VERSION__` declared twice | TypeScript |
| **P3 — Quality** | 4.3 Use maps instead of if-chains for status lookups | TypeScript |
| **P3 — Quality** | 9.1 Views too large — extract card components | Organization |
| **P3 — Quality** | 9.2 Magic number in `truncateTitle` | Organization |
| **P3 — Cosmetic** | 10.1 Hardcoded colors vs theme variables | Vuetify |
| **P3 — Cosmetic** | 5.2 Date allocations inside computed | Performance |
