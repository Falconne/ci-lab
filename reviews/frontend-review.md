# Frontend Code Review — Round 2

This review covers the Mergician Vue.js frontend after the Round 1 refactoring.
Recommendations are grouped by category and prioritised:

- **P0** — Bug or correctness issue
- **P1** — Architectural / maintainability improvement
- **P2** — Code quality / DRY improvement
- **P3** — Minor improvement

---

## 1. Architecture / Component Design

### 1.1 Replace `role="link"` with actual `<a>` elements on merge group cards (P1)

**Files:** `HomeView.vue`

The merge group cards use `<div role="link" tabindex="0" @click @keydown.enter>` to navigate. This is a reimplementation of what `<a>` already provides natively. Using a real `<a>` element (or Vue Router's `<router-link>`) gives:

- Native right-click → "Copy link address" / "Open in new tab"
- Native middle-click opens in a new tab (users expect this)
- Screen readers announce it correctly without ARIA workarounds
- The `href` is already computed by `mergeGroupHref()`

The entire card can be wrapped in a `<router-link>` rendered as a `<div>` via its `custom` slot, or simpler: use an `<a :href="mergeGroupHref(group)">` as the card root element, with `@click.prevent="openMergeGroupDetails(group)"` for SPA navigation. This preserves the click behaviour while enabling all native link affordances.

### 1.2 Extract a `useNow()` composable for reactive timestamp (P2)

**Files:** `HomeView.vue`

The `now` ref, its `setInterval`, and `onUnmounted` cleanup form a self-contained concern. Extracting into a `useNow(intervalMs)` composable:

```ts
// composables/useNow.ts
import { ref, onMounted, onUnmounted } from 'vue'

export function useNow(intervalMs = 10_000) {
  const now = ref(Date.now())
  let timer: ReturnType<typeof setInterval> | null = null

  onMounted(() => { timer = setInterval(() => { now.value = Date.now() }, intervalMs) })
  onUnmounted(() => { if (timer) clearInterval(timer) })

  return now
}
```

HomeView then becomes `const now = useNow()` — one line instead of five, reusable if other views need relative times.

### 1.3 Simplify MergeGroupDetailsView data reconciliation (P2)

**Files:** `MergeGroupDetailsView.vue`

The `handleActivityEvent()` function uses `findIndex` to update or add items one-by-one. But `pollMergeGroup` already filters out removed items and then calls `handleActivityEvent` for every incoming branch. Since the poll always returns the full list, this incremental reconciliation is unnecessary complexity. Replace with a direct array reassignment:

```ts
// In pollMergeGroup, after the filter step:
activities.value = data.branches
```

If field-level identity preservation matters for transitions, use:

```ts
activities.value = data.branches.map(incoming => {
  const existing = activities.value.find(a => a.id === incoming.id)
  return existing ? Object.assign(existing, incoming) : incoming
})
```

Both approaches are simpler than the current two-pass filter-then-loop-and-findIndex pattern.

### 1.4 Consolidate group-level status logic (P1)

**Files:** `MergeGroupDetailsView.vue`, `statusHelpers.ts`

The detail view computes `overallStatusLabel` and `overallStatusClass` with custom logic that reimplements what `getGroupStatus` / `groupStatusLabel` / `groupStatusClass` already do in `statusHelpers.ts`. The implementations differ slightly — the detail view maps through `itemStatusLabel` while `getGroupStatus` uses a priority-based loop, and the detail view has a `'Loading'` case that `getGroupStatus` doesn't.

Either:
- Extend `getGroupStatus` in `statusHelpers.ts` to handle the loading case (when no branches exist), then use it in both views, or
- Accept the MergeGroup type in a new shared function that returns `ItemStatus` for the group.

This eliminates the duplicated status aggregation logic.

---

## 2. Type Safety

### 2.1 Remove trivial wrapper functions in MergeGroupDetailsView (P2)

**Files:** `MergeGroupDetailsView.vue`

Several functions exist only to forward to an imported utility with no added logic:

```ts
function itemStatusClass(item) { return statusCssClass(itemStatusLabel(item)) }
function itemStatusColor(item) { return statusChipColor(itemStatusLabel(item)) }
function itemApprovalsText(item) { return itemApprovalsTextDetailed(item) }
```

These can be replaced with either:
- Direct calls in the template: `:class="statusCssClass(itemStatusLabel(item))"` and `:color="statusChipColor(itemStatusLabel(item))"`, or
- A single composable/helper that returns all three derived values for a branch (status label, CSS class, chip color) to avoid repeated `itemStatusLabel` calls.

`itemApprovalsText` is a pure alias for `itemApprovalsTextDetailed` — just import and use `itemApprovalsTextDetailed` directly.

### 2.2 Type the route `params.mergeGroupId` access (P3)

**Files:** `MergeGroupDetailsView.vue`

`getMergeGroupId()` casts `route.params.mergeGroupId as string` without validation. If the route somehow has an array param (Vue Router allows this), this silently becomes `"value1,value2"`. A safer pattern:

```ts
function getMergeGroupId(): string {
  const id = route.params.mergeGroupId
  return Array.isArray(id) ? id[0] : (id ?? '')
}
```

---

## 3. CSS & Theming

### 3.1 Extract shared card CSS into the common stylesheet (P1)

**Files:** `HomeView.vue`, `MergeGroupDetailsView.vue`, `status-badges.css`

The `.merge-group-card` (HomeView) and `.branch-card` (MergeGroupDetailsView) share nearly identical styles:

```css
/* Both have: */
display: flex;
border-radius: 8px;
background: #fff;
border-top: 1.5px solid #e0e0e0;
border-right: 1.5px solid #e0e0e0;
border-bottom: 1.5px solid #e0e0e0;
border-left: none;
box-shadow: 0 1px 3px rgba(0, 0, 0, 0.08), 0 1px 2px rgba(0, 0, 0, 0.06);
overflow: hidden;
```

Extract a `.status-card` base class into `status-badges.css` (or rename to something like `shared-cards.css`). Then each view just adds view-specific modifiers (e.g. `cursor: pointer` for home cards, hover shadow for home cards).

### 3.2 Replace hard-coded hex colours with Vuetify theme references (P2)

**Files:** `HomeView.vue`, `MergeGroupDetailsView.vue`

Despite the Round 1 migration of status colours to Vuetify CSS custom properties, many hard-coded hex colours remain throughout both views for non-status elements:

- Text colours: `#37474f`, `#1a1a2e`, `#5f6368`, `#9e9e9e`
- Background/borders: `#f0f0f0`, `#e0e0e0`, `#fff`, `#e8eaf6`, `#3949ab`
- Card shadows: hardcoded rgba values

These should use Vuetify's text and surface colour classes where possible (e.g. `text-medium-emphasis`, `text-high-emphasis`, `bg-surface`) or CSS custom properties from the Vuetify theme (e.g. `rgb(var(--v-theme-on-surface))`). This ensures the UI automatically adapts if a dark theme is ever added and keeps the design language consistent.

### 3.3 CSS shorthand for borders (P3)

**Files:** `HomeView.vue`, `MergeGroupDetailsView.vue`

Both views spell out three separate border declarations instead of using shorthand:

```css
/* Current: */
border-top: 1.5px solid #e0e0e0;
border-right: 1.5px solid #e0e0e0;
border-bottom: 1.5px solid #e0e0e0;
border-left: none;

/* Simpler: */
border: 1.5px solid #e0e0e0;
border-left: none;
```

---

## 4. Error Handling & Resilience

### 4.1 Add a global Vue error handler (P1)

**Files:** `main.ts`

The app has no `app.config.errorHandler`. Any unhandled error in a lifecycle hook, event handler, or watcher silently fails in production (Vue swallows it after logging to console). Adding a global handler enables centralised error tracking and prevents silent failures:

```ts
app.config.errorHandler = (err, instance, info) => {
  console.error(`[Mergician] Unhandled error in ${info}:`, err)
}
```

This is especially important because the polling composable catches errors but template event handlers (e.g. `@click="openMrAsGroup"`) could throw unexpectedly.

### 4.2 Guard JSON parsing in poll functions (P2)

**Files:** `HomeView.vue`, `MergeGroupDetailsView.vue`

Both poll functions call `response.json()` on the success path without a try/catch. If the backend returns malformed JSON (e.g. during a deploy, or a proxy error returns HTML), this throws a `SyntaxError` that propagates to the outer catch block and logs as a generic "poll failed" message, obscuring the real issue.

Wrap the JSON parse:

```ts
let data: MergeGroup
try {
  data = await response.json()
} catch (parseError) {
  console.error('[Mergician] Failed to parse poll response as JSON:', parseError)
  return
}
```

### 4.3 Fix `document.title` race between `afterEach` guard and `updateRouteTitle` (P1)

**Files:** `MergeGroupDetailsView.vue`, `router/index.ts`

When `pollMergeGroup` updates the merge group name, `updateRouteTitle()` sets `document.title` to `"Branch Name — Mergician"` and then calls `router.replace()` to update the query param. This triggers the router's `afterEach` guard, which overwrites `document.title` with just `"Merge Group — Mergician"` (from `route.meta.title`).

The `afterEach` guard already handles the detail view title by reading `route.query.title`, so the `document.title = ...` line in `updateRouteTitle()` is unnecessary — it will be set correctly by the `afterEach` guard after `router.replace()` completes. Remove the direct `document.title` assignment from `updateRouteTitle()`, or have the `afterEach` guard check for `query.title` on the details route.

Verify by checking the current `afterEach` implementation — if it only reads `route.meta.title`, then the fix is to enhance the `afterEach` guard to also check `to.query.title` for the detail route, which is the single source of truth for the page title.

---

## 5. Performance

### 5.1 Avoid re-creating the `incomingIds` Set on every poll when data hasn't changed (P3)

**Files:** `HomeView.vue`, `MergeGroupDetailsView.vue`

Both poll functions unconditionally create a `new Set()`, filter the existing array, and update all items — even when the response is identical to the current state. For most polls, nothing has changed.

Consider a quick equality check before applying updates:

```ts
const dataJson = JSON.stringify(data)
if (dataJson === lastPollJson) return
lastPollJson = dataJson
```

This trades a string comparison for avoiding DOM updates on every 5-second tick. With ~50 merge groups × ~5 branches each, the DOM diffing saved is meaningful.

---

## 6. Composable Design

### 6.1 Document `usePolling`'s setup-time constraint (P2)

**Files:** `usePolling.ts`

`usePolling` calls `onUnmounted(stop)` which only works if the composable is invoked during component `setup()`. If someone calls it inside a callback or conditional block, the cleanup hook silently fails to register. Add a JSDoc comment:

```ts
/**
 * Must be called during component setup (not inside callbacks or conditionals)
 * so that the onUnmounted cleanup hook is properly registered.
 */
export function usePolling(...)
```

### 6.2 `usePolling` doesn't fire an immediate poll reliably (P2)

**Files:** `usePolling.ts`

`start()` calls `guardedPoll()` immediately for the first poll, but also starts a `setInterval` with `fastIntervalMs`. This means the second poll fires `fastIntervalMs` after `start()`, not after the first poll completes. If the first poll takes longer than `fastIntervalMs` (e.g. slow backend), the guard prevents overlap but the timing slips — subsequent polls fire at `fastIntervalMs` intervals from the `start()` call, not from completion.

Consider using recursive `setTimeout` instead of `setInterval` to ensure each poll starts after the previous one completes:

```ts
async function schedulePoll(delayMs: number) {
  await new Promise(resolve => setTimeout(resolve, delayMs))
  await guardedPoll()
  if (pollIntervalId !== null) schedulePoll(currentInterval)
}
```

This prevents poll drift and timer accumulation under load.

---

## 7. DRY / Consolidation

### 7.1 Extract the 503 "database unavailable" handling into a shared pattern (P2)

**Files:** `HomeView.vue`, `MergeGroupDetailsView.vue`

Both poll functions have identical 503 handling:

```ts
if (response.status === 503) {
  errorMessage.value = 'Database is temporarily unavailable. Retrying...'
  return
}
// ... later ...
if (errorMessage.value === 'Database is temporarily unavailable. Retrying...') {
  errorMessage.value = ''
}
```

The string `'Database is temporarily unavailable. Retrying...'` is duplicated as both a setter and an equality check. At minimum, extract it as a constant. Better: extract a helper that manages transient error state:

```ts
const TRANSIENT_DB_MESSAGE = 'Database is temporarily unavailable. Retrying...'

function handleTransientError(errorRef: Ref<string>, status: number): boolean {
  if (status === 503) {
    errorRef.value = TRANSIENT_DB_MESSAGE
    return true
  }
  return false
}

function clearTransientError(errorRef: Ref<string>) {
  if (errorRef.value === TRANSIENT_DB_MESSAGE) errorRef.value = ''
}
```

### 7.2 Consolidate `goBack` navigation (P3)

**Files:** `MergeGroupDetailsView.vue`

The `goBack()` function just calls `router.push('/')`. The two "Back to Dashboard" buttons could use `<router-link to="/">` directly inside a `<v-btn>` (using `v-btn`'s `:to` prop), eliminating the function entirely:

```html
<v-btn variant="text" prepend-icon="mdi-arrow-left" :to="'/'">
  Back to Dashboard
</v-btn>
```

Vuetify's `v-btn` supports the `to` prop natively when vue-router is installed.

---

## 8. Code Organisation

### 8.1 Consider splitting HomeView into smaller components (P2)

**Files:** `HomeView.vue`

At ~860 lines, HomeView is the largest file in the frontend. The template alone is ~240 lines. Candidates for extraction into child components:

- **`WelcomePage.vue`** — The unauthenticated welcome section (~15 lines of template, self-contained)
- **`MergeGroupCard.vue`** — The card rendered inside the `TransitionGroup` (~100 lines of template, receives a `MergeGroup` prop). This would encapsulate the card layout, approval icons, time-ago display, and skeleton states. It would also make the `TransitionGroup` key simpler.
- **`DashboardFilter.vue`** — The filter text field + "Open MR as Merge Group" button + related logic (~40 lines of template, substantial script logic for clipboard, MR URL parsing)

Each of these has clear boundaries and could be independently tested or modified without touching the rest of HomeView.

---

## 9. Minor Issues

### 9.1 `onUnmounted` in HomeView only needed for time ticker (P3)

**Files:** `HomeView.vue`

`HomeView.vue` imports `onUnmounted` solely for the time interval cleanup. If recommendation 1.2 (`useNow` composable) is implemented, this import becomes unnecessary — the composable handles its own cleanup. Note this as a follow-up cleanup after 1.2.

### 9.2 `randomUUID` import in `vite.config.ts` is unused (P3)

**Files:** `vite.config.ts`

The `randomUUID` import from `node:crypto` is imported but never used. Remove it.
