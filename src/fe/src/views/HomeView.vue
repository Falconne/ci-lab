<template>
  <v-container>
    <v-row justify="center" class="mt-4">
      <v-col cols="12" md="10" lg="8">
        <!-- Error alert -->
        <v-alert
          v-if="errorMessage"
          type="error"
          variant="tonal"
          closable
          @click:close="errorMessage = ''"
          class="mb-4"
        >
          {{ errorMessage }}
        </v-alert>

        <!-- Welcome page for unauthenticated users -->
        <div v-if="!authenticated && !initialLoading" class="text-center pa-12">
          <v-icon icon="mdi-source-merge" size="80" color="primary" class="mb-6" />
          <h1 class="text-h3 mb-4">Welcome to Mergician</h1>
          <p class="text-body-1 text-grey-darken-1 mb-8" style="max-width: 500px; margin: 0 auto;">
            Mergician helps you coordinate merge requests across multiple Git repositories.
            Sign in with your GitLab account to get started.
          </p>
          <v-btn
            color="primary"
            size="large"
            href="/api/auth/login"
            prepend-icon="mdi-login"
          >
            Sign in with GitLab
          </v-btn>
        </div>

        <div v-else-if="initialLoading" class="text-center pa-8">
          <v-progress-circular indeterminate color="primary" size="48" />
          <p class="mt-4 text-body-1">Loading dashboard...</p>
        </div>

        <!-- Empty state (stream finished, no branches) -->
        <div v-else-if="orderedGroups.length === 0 && !streaming" class="text-center pa-8">
          <v-icon icon="mdi-source-branch" size="64" color="grey" class="mb-4" />
          <p class="text-h6 text-grey">No active branches in the last 14 days</p>
        </div>

        <!-- Dashboard cards -->
        <div v-else>
          <!-- Streaming indicator -->
          <div class="d-flex align-center mb-3" style="min-height: 28px;">
            <v-progress-circular
              v-if="streaming"
              indeterminate
              color="primary"
              size="16"
              width="2"
              class="mr-2"
            />
            <span v-if="streaming" class="text-caption text-medium-emphasis">Loading activity\u2026</span>
          </div>

          <!-- Card list: TransitionGroup provides enter/leave/move animations.      -->
          <!-- Mouse enter/leave on the wrapper controls reorder-suppression logic.  -->
          <div
            class="card-list-wrapper"
            @mouseenter="onCardsMouseEnter"
            @mouseleave="onCardsMouseLeave"
          >
            <TransitionGroup name="card" tag="div" class="card-list">
              <div
                v-for="group in orderedGroups"
                :key="group.groupKey"
                class="merge-group-card"
                :class="`card-status-${groupStatus(group)}`"
                data-testid="merge-group-card"
              >
                <!-- Card header: branch icon + name, group status chip, time ago -->
                <div class="card-header">
                  <v-icon icon="mdi-source-branch" size="16" class="card-branch-icon mr-1" />
                  <span class="card-branch-name" :data-branch="group.branchName">
                    {{ group.branchName }}
                  </span>
                  <v-spacer />
                  <StatusChip :status="groupStatus(group)" class="mr-3" />
                  <span class="card-time text-caption text-medium-emphasis">
                    {{ groupTimeAgo(group) }}
                  </span>
                </div>

                <!-- Per-repo rows, separated by a subtle divider -->
                <template v-if="group.items.length > 0">
                  <v-divider class="card-divider" />
                  <div class="card-repos">
                    <div
                      v-for="item in group.items"
                      :key="`${item.projectId}-${item.branchName}`"
                      class="repo-row"
                    >
                      <span class="repo-name text-body-2">{{ item.projectName }}</span>
                      <div class="d-flex align-center ga-2">
                        <!-- Loading indicator while MR/approval data resolves -->
                        <v-progress-circular
                          v-if="item.hasMergeRequest === null"
                          indeterminate
                          color="grey"
                          size="14"
                          width="2"
                          class="mr-1"
                        />
                        <template v-else>
                          <StatusChip :status="itemStatus(item)" size="x-small" />
                          <span
                            v-if="item.hasMergeRequest && item.approvalsRequired != null && item.approvalsGiven != null"
                            class="approvals-text text-caption"
                            :class="item.approvalsGiven >= item.approvalsRequired ? 'text-success' : 'text-medium-emphasis'"
                          >
                            {{ item.approvalsGiven }}/{{ item.approvalsRequired }}
                          </span>
                        </template>
                      </div>
                    </div>
                  </div>
                </template>
              </div>
            </TransitionGroup>
          </div>
        </div>
      </v-col>
    </v-row>
  </v-container>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, defineComponent, h } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useCurrentUser } from '@/composables/useCurrentUser'
import { usePageTitle } from '@/composables/usePageTitle'

// ─── Inline StatusChip component ─────────────────────────────────────────────

type Status = 'ready' | 'open' | 'waiting'

const StatusChip = defineComponent({
  props: {
    status: { type: String as () => Status, required: true },
    size: { type: String, default: 'small' }
  },
  setup(props) {
    const config: Record<Status, { label: string }> = {
      ready:   { label: 'Ready'   },
      open:    { label: 'Open'    },
      waiting: { label: 'Waiting' }
    }
    return () => {
      const { label } = config[props.status]
      return h('span', { class: `status-chip status-chip--${props.status} status-chip--${props.size}` }, [
        h('span', { class: `status-dot status-dot--${props.status}` }),
        h('span', { class: 'status-label' }, label)
      ])
    }
  }
})

// ─── Types ────────────────────────────────────────────────────────────────────

interface BranchActivity {
  branchName: string
  projectId: number
  projectName: string
  hasMergeRequest: boolean | null
  approvalsRequired: number | null
  approvalsGiven: number | null
  lastUpdated: string | null
  mergeGroupId: number | null
}

interface BranchDeletedNotification {
  branchName: string
  projectId: number
  mergeGroupId: number | null
}

interface ActivityPollResponse {
  activities: BranchActivity[]
  deletedBranches: BranchDeletedNotification[]
}

interface MergeGroup {
  groupKey: string
  branchName: string
  items: BranchActivity[]
}

// ─── Composables & state ──────────────────────────────────────────────────────

const route = useRoute()
const router = useRouter()
const { currentUser, loadCurrentUser } = useCurrentUser()
const { setPageTitle } = usePageTitle()

const activities = ref<BranchActivity[]>([])
const initialLoading = ref(true)
const authenticated = computed(() => currentUser.value !== null)
const streaming = ref(false)
const errorMessage = ref('')
const now = ref(Date.now())

// Controls the display order of merge group cards for animation purposes.
// Each entry is a groupKey string. Managed reactively so TransitionGroup can
// animate reordering, additions, and removals separately from data updates.
const displayOrder = ref<string[]>([])

// Mouse-hover state: prevents reorder while the user is examining a card.
const isHoveringCards = ref(false)
let hoverReorderTimer: ReturnType<typeof setTimeout> | null = null

let eventSource: EventSource | null = null
let pollIntervalId: ReturnType<typeof setInterval> | null = null
let refreshIntervalId: ReturnType<typeof setInterval> | null = null
let timeIntervalId: ReturnType<typeof setInterval> | null = null
let lastUpdateTime: Date | null = null

// ─── Merge groups ────────────────────────────────────────────────────────────

/**
 * Groups activities by mergeGroupId (from DB) when available,
 * falling back to branchName for items without a mergeGroupId.
 */
const mergeGroups = computed<MergeGroup[]>(() => {
  const groups = new Map<string, { branchName: string; items: BranchActivity[] }>()
  for (const item of activities.value) {
    const groupKey = item.mergeGroupId != null ? `mg:${item.mergeGroupId}` : `bn:${item.branchName}`
    const existing = groups.get(groupKey)
    if (existing) {
      if (!existing.items.some(e => e.projectId === item.projectId && e.branchName === item.branchName)) {
        existing.items.push(item)
      }
    } else {
      groups.set(groupKey, { branchName: item.branchName, items: [item] })
    }
  }
  return Array.from(groups.entries()).map(([groupKey, { branchName, items }]) => ({
    groupKey,
    branchName,
    items
  }))
})

/**
 * Cards ordered according to displayOrder, which is managed separately from
 * the raw data so TransitionGroup can animate position changes.
 */
const orderedGroups = computed<MergeGroup[]>(() => {
  const groupMap = new Map(mergeGroups.value.map(g => [g.groupKey, g]))
  return displayOrder.value
    .filter(k => groupMap.has(k))
    .map(k => groupMap.get(k)!)
})

// ─── Status logic ────────────────────────────────────────────────────────────

function itemStatus(item: BranchActivity): Status {
  if (item.hasMergeRequest === null) return 'waiting' // still resolving
  if (!item.hasMergeRequest) return 'waiting'         // no MR exists
  // Has an MR – check approval counts
  if (item.approvalsRequired != null && item.approvalsGiven != null) {
    return item.approvalsGiven >= item.approvalsRequired ? 'ready' : 'open'
  }
  // Has MR but approval data is still loading
  return 'open'
}

/**
 * Group status is the "least ready" status across all contained items:
 * waiting > open > ready
 */
function groupStatus(group: MergeGroup): Status {
  const statuses = group.items.map(itemStatus)
  if (statuses.includes('waiting')) return 'waiting'
  if (statuses.includes('open')) return 'open'
  return 'ready'
}

// ─── Display order management ────────────────────────────────────────────────

/** Returns the most recent lastUpdated timestamp for any item in a group. */
function groupMostRecentTime(group: MergeGroup): number {
  let max = 0
  for (const item of group.items) {
    if (item.lastUpdated) {
      const t = new Date(item.lastUpdated).getTime()
      if (t > max) max = t
    }
  }
  return max
}

/**
 * Synchronises displayOrder with the current mergeGroups after a data update:
 * - Drops deleted groups
 * - Adds newly discovered groups at the top (or bottom while hovering)
 * Existing groups keep their current position so the list is stable during
 * active browsing; a separate reorderByRecency() call does the actual sort.
 */
function syncDisplayOrder() {
  const currentKeys = new Set(mergeGroups.value.map(g => g.groupKey))
  const kept = displayOrder.value.filter(k => currentKeys.has(k))
  const keptSet = new Set(kept)
  const incoming = mergeGroups.value.map(g => g.groupKey).filter(k => !keptSet.has(k))

  if (incoming.length === 0) {
    displayOrder.value = kept
    return
  }

  if (isHoveringCards.value) {
    // While the user is hovering, new cards appear at the bottom so existing
    // cards do not shift unexpectedly.
    displayOrder.value = [...kept, ...incoming]
  } else {
    displayOrder.value = [...incoming, ...kept]
  }
}

/**
 * Sorts the display order by most-recently-updated first.
 * Called 2 seconds after the mouse fully leaves the card area.
 */
function reorderByRecency() {
  const groupMap = new Map(mergeGroups.value.map(g => [g.groupKey, g]))
  displayOrder.value = [...displayOrder.value].sort((a, b) => {
    const ta = groupMap.has(a) ? groupMostRecentTime(groupMap.get(a)!) : 0
    const tb = groupMap.has(b) ? groupMostRecentTime(groupMap.get(b)!) : 0
    return tb - ta
  })
}

// ─── Mouse hover handlers ─────────────────────────────────────────────────────

function onCardsMouseEnter() {
  isHoveringCards.value = true
  if (hoverReorderTimer !== null) {
    clearTimeout(hoverReorderTimer)
    hoverReorderTimer = null
  }
}

function onCardsMouseLeave() {
  isHoveringCards.value = false
  // Reorder 2 seconds after the mouse completely leaves the card area
  hoverReorderTimer = setTimeout(() => {
    hoverReorderTimer = null
    reorderByRecency()
  }, 2000)
}

// ─── Time formatting ─────────────────────────────────────────────────────────

function formatTimeAgo(isoString: string): string {
  if (!isoString) return ''
  const date = new Date(isoString)
  const diffMs = now.value - date.getTime()
  const diffSec = Math.floor(diffMs / 1000)

  if (diffSec < 60) return diffSec === 1 ? '1 second ago' : `${diffSec} seconds ago`
  const diffMin = Math.floor(diffSec / 60)
  if (diffMin < 60) return diffMin === 1 ? '1 minute ago' : `${diffMin} minutes ago`
  const diffHour = Math.floor(diffMin / 60)
  if (diffHour < 24) return diffHour === 1 ? '1 hour ago' : `${diffHour} hours ago`
  const diffDay = Math.floor(diffHour / 24)
  return diffDay === 1 ? '1 day ago' : `${diffDay} days ago`
}

function groupTimeAgo(group: MergeGroup): string {
  const t = groupMostRecentTime(group)
  return t > 0 ? formatTimeAgo(new Date(t).toISOString()) : ''
}

// ─── Activity event handlers ─────────────────────────────────────────────────

function handleActivityEvent(data: BranchActivity) {
  const existingIndex = activities.value.findIndex(
    a => a.branchName === data.branchName && a.projectId === data.projectId
  )

  if (existingIndex >= 0) {
    activities.value[existingIndex] = data
  } else {
    const groupKey = data.mergeGroupId != null ? data.mergeGroupId : null
    let lastGroupIndex = -1

    if (groupKey != null) {
      lastGroupIndex = findLastIndexOf(activities.value, a => a.mergeGroupId === groupKey)
    }

    if (lastGroupIndex < 0) {
      lastGroupIndex = findLastIndexOf(activities.value, a => a.branchName === data.branchName)
    }

    if (lastGroupIndex >= 0) {
      activities.value.splice(lastGroupIndex + 1, 0, data)
    } else {
      activities.value.push(data)
    }
  }

  syncDisplayOrder()
}

function handleBranchDeleted(notification: BranchDeletedNotification) {
  const idx = activities.value.findIndex(
    a => a.branchName === notification.branchName && a.projectId === notification.projectId
  )
  if (idx >= 0) {
    activities.value.splice(idx, 1)
  }
  syncDisplayOrder()
}

function findLastIndexOf<T>(arr: T[], predicate: (item: T) => boolean): number {
  for (let i = arr.length - 1; i >= 0; i--) {
    if (predicate(arr[i])) return i
  }
  return -1
}

// ─── SSE streaming ───────────────────────────────────────────────────────────

function startStreaming() {
  streaming.value = true
  eventSource = new EventSource('/api/activity/stream')

  eventSource.onmessage = (event) => {
    try {
      const data: BranchActivity = JSON.parse(event.data)
      handleActivityEvent(data)
    } catch (err) {
      console.error('Failed to parse SSE data:', err)
    }
  }

  eventSource.addEventListener('done', () => {
    streaming.value = false
    eventSource?.close()
    eventSource = null
    syncDisplayOrder()
    // Initial sort by recency once all streaming data has arrived
    reorderByRecency()
    startPolling()
  })

  eventSource.onerror = (event) => {
    streaming.value = false
    eventSource?.close()
    eventSource = null
    console.error('SSE stream error:', event)
    syncDisplayOrder()
    startPolling()
  }
}

// ─── Polling ─────────────────────────────────────────────────────────────────

function startPolling() {
  if (pollIntervalId !== null || refreshIntervalId !== null) {
    return
  }

  lastUpdateTime = new Date()
  pollIntervalId = setInterval(pollForActivity, 5000)
  refreshIntervalId = setInterval(refreshExistingBranches, 15000)
}

function stopPolling() {
  if (pollIntervalId !== null) {
    clearInterval(pollIntervalId)
    pollIntervalId = null
  }
  if (refreshIntervalId !== null) {
    clearInterval(refreshIntervalId)
    refreshIntervalId = null
  }
}

async function refreshExistingBranches() {
  if (activities.value.length === 0) return

  const seen = new Set<string>()
  const branches: { branchName: string; projectId: number; lastUpdated: string | null; mergeGroupId: number | null }[] = []
  for (const a of activities.value) {
    const key = `${a.branchName}:${a.projectId}`
    if (!seen.has(key)) {
      seen.add(key)
      branches.push({ branchName: a.branchName, projectId: a.projectId, lastUpdated: a.lastUpdated, mergeGroupId: a.mergeGroupId })
    }
  }

  try {
    const response = await fetch('/api/activity/refresh', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(branches)
    })

    if (response.status === 401) {
      console.warn('Refresh returned 401, stopping polling')
      stopPolling()
      return
    }

    if (response.status === 503) {
      errorMessage.value = 'Database is unavailable. Please try again later.'
      stopPolling()
      return
    }

    if (!response.ok) {
      console.error('Refresh failed with status', response.status)
      return
    }

    const reader = response.body?.getReader()
    if (!reader) return

    const decoder = new TextDecoder()
    let buffer = ''

    while (true) {
      const { done, value } = await reader.read()
      if (done) break

      buffer += decoder.decode(value, { stream: true })

      let eventEnd: number
      while ((eventEnd = buffer.indexOf('\n\n')) !== -1) {
        const eventText = buffer.slice(0, eventEnd)
        buffer = buffer.slice(eventEnd + 2)

        if (eventText.startsWith('event: done')) return

        if (eventText.startsWith('event: deleted')) {
          const dataLine = eventText.split('\n').find(l => l.startsWith('data: '))
          if (dataLine) {
            try {
              const notification: BranchDeletedNotification = JSON.parse(dataLine.slice(6))
              handleBranchDeleted(notification)
            } catch (err) {
              console.error('Failed to parse deleted SSE data:', err)
            }
          }
          continue
        }

        if (eventText.startsWith('data: ')) {
          try {
            const data: BranchActivity = JSON.parse(eventText.slice(6))
            handleActivityEvent(data)
          } catch (err) {
            console.error('Failed to parse refresh SSE data:', err)
          }
        }
      }
    }
  } catch (err) {
    console.error('Refresh request failed:', err)
  }
}

async function pollForActivity() {
  if (!lastUpdateTime) return

  const since = new Date(lastUpdateTime.getTime() - 5000)
  const sinceParam = since.toISOString()

  try {
    const response = await fetch(`/api/activity/poll?since=${encodeURIComponent(sinceParam)}`)

    if (response.status === 401) {
      console.warn('Poll returned 401, stopping polling')
      stopPolling()
      return
    }

    if (response.status === 503) {
      errorMessage.value = 'Database is unavailable. Please try again later.'
      stopPolling()
      return
    }

    if (!response.ok) {
      console.error('Poll failed with status', response.status)
      return
    }

    const data: ActivityPollResponse = await response.json()

    if (data.deletedBranches) {
      for (const deleted of data.deletedBranches) {
        handleBranchDeleted(deleted)
      }
    }

    if (data.activities) {
      for (const activity of data.activities) {
        handleActivityEvent(activity)
      }
    }

    // After incorporating poll data, re-sync and reorder if the user is not hovering
    syncDisplayOrder()
    if (!isHoveringCards.value) {
      reorderByRecency()
    }

    lastUpdateTime = new Date()
  } catch (err) {
    console.error('Poll request failed:', err)
  }
}

// ─── Lifecycle ───────────────────────────────────────────────────────────────

onMounted(async () => {
  setPageTitle('Dashboard')
  timeIntervalId = setInterval(() => { now.value = Date.now() }, 60000)

  if (route.query.error && route.query.message) {
    errorMessage.value = route.query.message as string
    router.replace({ query: {} })
  }

  try {
    await loadCurrentUser()
    if (!currentUser.value) {
      initialLoading.value = false
      return
    }

    initialLoading.value = false
    startStreaming()
  } catch (err) {
    console.error('Failed to load dashboard:', err)
    initialLoading.value = false
  }
})

onUnmounted(() => {
  if (timeIntervalId) clearInterval(timeIntervalId)
  if (hoverReorderTimer !== null) clearTimeout(hoverReorderTimer)
  eventSource?.close()
  eventSource = null
  stopPolling()
})
</script>

<style scoped>
/* ── Card list layout ───────────────────────────────────────────────────────── */

.card-list-wrapper {
  /* Provides a containing block for absolutely-positioned leaving elements     */
  position: relative;
}

.card-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
  position: relative;
}

/* ── TransitionGroup animations ─────────────────────────────────────────────── */

/* Entering: slides down from slightly above and fades in */
.card-enter-active {
  transition: opacity 0.35s ease, transform 0.35s ease;
}
.card-enter-from {
  opacity: 0;
  transform: translateY(-12px);
}

/* Leaving: fades out while sliding up slightly, removed from layout flow so   */
/* that sibling cards FLIP-animate to their new positions cleanly.              */
.card-leave-active {
  transition: opacity 0.22s ease, transform 0.22s ease;
  position: absolute;
  width: 100%;
  pointer-events: none;
}
.card-leave-to {
  opacity: 0;
  transform: translateY(-8px);
}

/* Reorder FLIP: cards glide smoothly to their new positions */
.card-move {
  transition: transform 0.42s cubic-bezier(0.25, 0.8, 0.25, 1);
}

/* ── Individual card ─────────────────────────────────────────────────────────── */

.merge-group-card {
  background: rgb(var(--v-theme-surface));
  border-radius: 6px;
  border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  border-left-width: 4px;
  border-left-color: transparent;
  overflow: hidden;
  transition: box-shadow 0.2s ease;
}

.merge-group-card:hover {
  box-shadow: 0 2px 12px rgba(0, 0, 0, 0.12);
}

.card-status-ready   { border-left-color: rgb(var(--v-theme-success)); }
.card-status-open    { border-left-color: rgb(var(--v-theme-info)); }
.card-status-waiting { border-left-color: rgb(var(--v-theme-warning)); }

/* ── Card header ─────────────────────────────────────────────────────────────── */

.card-header {
  display: flex;
  align-items: center;
  padding: 10px 14px;
  gap: 4px;
}

.card-branch-icon {
  opacity: 0.55;
  flex-shrink: 0;
}

.card-branch-name {
  font-weight: 600;
  font-size: 0.875rem;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 55%;
}

.card-time {
  white-space: nowrap;
  flex-shrink: 0;
}

/* ── Card repo rows ──────────────────────────────────────────────────────────── */

.card-divider {
  opacity: 0.08;
}

.card-repos {
  padding: 6px 14px 10px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.repo-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.repo-name {
  opacity: 0.72;
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.approvals-text {
  font-variant-numeric: tabular-nums;
  min-width: 28px;
  text-align: right;
}

/* ── Status chip (inline component, styled via :deep) ───────────────────────── */

:deep(.status-chip) {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  border-radius: 999px;
  padding: 2px 8px;
  font-size: 0.75rem;
  font-weight: 500;
  line-height: 1.5;
  white-space: nowrap;
}

:deep(.status-chip--x-small) {
  font-size: 0.7rem;
  padding: 1px 6px;
}

:deep(.status-chip--ready) {
  background: rgba(var(--v-theme-success), 0.12);
  color: rgb(var(--v-theme-success));
}
:deep(.status-chip--open) {
  background: rgba(var(--v-theme-info), 0.12);
  color: rgb(var(--v-theme-info));
}
:deep(.status-chip--waiting) {
  background: rgba(var(--v-theme-warning), 0.12);
  color: rgb(var(--v-theme-warning));
}

:deep(.status-dot) {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex-shrink: 0;
}
:deep(.status-dot--ready)   { background: rgb(var(--v-theme-success)); }
:deep(.status-dot--open)    { background: rgb(var(--v-theme-info)); }
:deep(.status-dot--waiting) { background: rgb(var(--v-theme-warning)); }
</style>
