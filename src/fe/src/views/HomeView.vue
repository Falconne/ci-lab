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

        <div v-else-if="sortedMergeGroups.length === 0 && !streaming" class="text-center pa-8">
          <v-icon icon="mdi-source-branch" size="64" color="grey" class="mb-4" />
          <p class="text-h6 text-grey">No active branches in the last 14 days</p>
        </div>

        <div v-else class="dashboard-cards">
          <v-progress-circular
            v-if="streaming"
            indeterminate
            color="primary"
            size="18"
            width="2"
            class="mb-2 streaming-indicator"
          />

          <TransitionGroup name="card-list" tag="div" class="card-container">
            <div
              v-for="group in sortedMergeGroups"
              :key="group.groupKey"
              class="merge-group-card"
              :data-group-key="group.groupKey"
              @mouseenter="onCardMouseEnter"
              @mouseleave="onCardMouseLeave"
            >
              <div class="card-accent" :class="groupStatusClass(group)" />
              <div class="card-body">
                <div class="card-header">
                                  <div class="branch-info">
                      <v-icon icon="mdi-source-branch" size="small" class="branch-icon" />
                      <span class="branch-name">{{ group.branchName }}</span>
                    </div>
                  <div class="card-header-right">
                    <span class="card-status-badge" :class="groupStatusClass(group)">
                      <span class="status-dot" />
                      {{ groupStatusLabel(group) }}
                    </span>
                    <span class="card-time">{{ groupTimeAgo(group) }}</span>
                  </div>
                </div>
                <div class="card-items">
                  <div
                    v-for="item in group.items"
                    :key="`${group.groupKey}-${item.projectId}`"
                    class="card-item"
                  >
                    <span class="item-main">
                      <v-tooltip location="top" :text="item.projectNameWithNamespace">
                        <template #activator="{ props }">
                          <span class="item-project" v-bind="props" :title="item.projectNameWithNamespace">
                            {{ item.projectName }}
                          </span>
                        </template>
                      </v-tooltip>
                      <span
                        v-if="item.mergeRequestTitle"
                        class="item-mr-title"
                        :title="item.mergeRequestTitle"
                      >
                        | {{ truncateTitle(item.mergeRequestTitle as string) }}
                      </span>
                    </span>
                    <!-- MR status icon: grey when no MR, blue when MR exists -->
                    <v-tooltip
                      location="top"
                      :text="mrTooltip(item)"
                    >
                      <template #activator="{ props }">
                        <span
                          v-bind="props"
                          class="item-mr-icon"
                          :title="mrTooltip(item)"
                        >
                          <v-icon
                            icon="mdi-source-merge"
                            size="small"
                            :color="mrIconColor(item)"
                            :data-mr-color="mrIconColor(item)"
                          />
                        </span>
                      </template>
                    </v-tooltip>
                    <v-tooltip
                      v-if="itemApprovalsText(item)"
                      location="top"
                      :text="approvalsTooltip(item)"
                    >
                      <template #activator="{ props }">
                        <span
                          v-bind="props"
                          class="item-approvals"
                          :title="approvalsTooltip(item)"
                        >
                          <v-icon
                            icon="mdi-thumb-up"
                            size="small"
                            :color="approvalIconColor(item)"
                            :data-approval-color="approvalIconColor(item)"
                            class="approval-icon"
                          />
                          <span class="approval-text">{{ itemApprovalsText(item) }}</span>
                        </span>
                      </template>
                    </v-tooltip>
                    <span class="item-time">
                      <v-tooltip v-if="item.lastUpdated" location="top" :text="formatDateTime(item.lastUpdated)">
                        <template v-slot:activator="{ props }">
                          <span v-bind="props">{{ formatTimeAgo(item.lastUpdated) }}</span>
                        </template>
                      </v-tooltip>
                      <span v-else class="text-grey">—</span>
                    </span>
                  </div>
                </div>
              </div>
            </div>
          </TransitionGroup>
        </div>
      </v-col>
    </v-row>
  </v-container>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useCurrentUser } from '@/composables/useCurrentUser'

interface BranchActivity {
  branchName: string
  projectId: number
  projectName: string
  projectNameWithNamespace: string
  hasMergeRequest: boolean | null
  approvalsRequired: number | null
  approvalsGiven: number | null
  lastUpdated: string | null
  mergeGroupId: number | null
  mergeRequestTitle?: string | null
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

type GroupStatus = 'ready' | 'open' | 'waiting'

const route = useRoute()
const router = useRouter()
const { currentUser, loadCurrentUser } = useCurrentUser()

const activities = ref<BranchActivity[]>([])
const initialLoading = ref(true)
const authenticated = computed(() => currentUser.value !== null)
const streaming = ref(false)
const errorMessage = ref('')
const now = ref(Date.now())

// Hover-pause state for reordering
const isHoveringCard = ref(false)
const lastHoverLeaveTime = ref(0)
const HOVER_COOLDOWN_MS = 2000

let eventSource: EventSource | null = null
let pollIntervalId: ReturnType<typeof setInterval> | null = null
let refreshIntervalId: ReturnType<typeof setInterval> | null = null
let timeIntervalId: ReturnType<typeof setInterval> | null = null
let lastUpdateTime: Date | null = null

// --- Status logic ---

function getGroupStatus(group: MergeGroup): GroupStatus {
  const statusPriority: GroupStatus[] = ['waiting', 'open', 'ready']
  let worstIndex = 2 // start with 'ready' (best)
  for (const item of group.items) {
    // derive status per item: waiting if no MR, ready if approvalsMet, else open
    let s: GroupStatus = 'waiting'
    if (item.hasMergeRequest) {
      if (item.approvalsRequired != null && item.approvalsGiven != null
        && item.approvalsGiven >= item.approvalsRequired) s = 'ready'
      else s = 'open'
    }
    const idx = statusPriority.indexOf(s)
    if (idx < worstIndex) worstIndex = idx
  }
  return statusPriority[worstIndex]
}

function groupStatusLabel(group: MergeGroup): string {
  const s = getGroupStatus(group)
  return s === 'ready' ? 'Ready' : s === 'open' ? 'Open' : 'Waiting'
}

function groupStatusClass(group: MergeGroup): string {
  return `status-${getGroupStatus(group)}`
}

function itemApprovalsText(item: BranchActivity): string {
  if (!item.hasMergeRequest || item.approvalsGiven == null || item.approvalsRequired == null) return ''
  return `${item.approvalsGiven}/${item.approvalsRequired}`
}

function approvalIconColor(item: BranchActivity): string {
  // grey by default; green when sufficient approvals (or zero required)
  if (!item.hasMergeRequest || item.approvalsGiven == null || item.approvalsRequired == null) {
    return 'grey'
  }
  if (item.approvalsGiven >= item.approvalsRequired) {
    return 'green'
  }
  return 'grey'
}

function approvalsTooltip(item: BranchActivity): string {
  if (!item.hasMergeRequest || item.approvalsGiven == null || item.approvalsRequired == null) {
    return ''
  }
  if (item.approvalsRequired === 0) {
    return 'No approval needed'
  }
  if (item.approvalsGiven >= item.approvalsRequired) {
    return 'All required approvals given'
  }
  return `${item.approvalsGiven} of ${item.approvalsRequired} needed approvals given`
}

function mrIconColor(item: BranchActivity): string {
  // blue if there's a merge request, grey otherwise
  if (item.hasMergeRequest) return 'blue'
  return 'grey'
}

function mrTooltip(item: BranchActivity): string {
  return item.hasMergeRequest ? 'MR exists' : 'MR not created'
}

function groupTimeAgo(group: MergeGroup): string {
  let latest: string | null = null
  for (const item of group.items) {
    if (item.lastUpdated && (!latest || item.lastUpdated > latest)) {
      latest = item.lastUpdated
    }
  }
  return latest ? formatTimeAgo(latest) : ''
}

/**
 * Truncates a title to 222 characters, appending "..." when it was longer.
 */
function truncateTitle(title: string): string {
  if (title.length <= 222) return title
  return title.slice(0, 222) + '...'
}

function getGroupLatestTime(group: MergeGroup): number {
  let latest = 0
  for (const item of group.items) {
    if (item.lastUpdated) {
      const t = new Date(item.lastUpdated).getTime()
      if (t > latest) latest = t
    }
  }
  return latest
}

// --- Hover-pause logic ---

function onCardMouseEnter() {
  isHoveringCard.value = true
}

function onCardMouseLeave() {
  isHoveringCard.value = false
  lastHoverLeaveTime.value = Date.now()
}

function canReorder(): boolean {
  if (isHoveringCard.value) return false
  return (Date.now() - lastHoverLeaveTime.value) >= HOVER_COOLDOWN_MS
}

/**
 * Groups activities by mergeGroupId (from DB) when available,
 * falling back to branchName for items without a mergeGroupId.
 */
const mergeGroups = computed<MergeGroup[]>(() => {
  const groups = new Map<string, { branchName: string; items: BranchActivity[] }>()
  for (const item of activities.value) {
    // Use mergeGroupId as the grouping key when available, fallback to branchName
    const groupKey = item.mergeGroupId != null ? `mg:${item.mergeGroupId}` : `bn:${item.branchName}`
    const existing = groups.get(groupKey)
    if (existing) {
      // Skip duplicates: same branch + project already in the group
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
 * Sorted merge groups — most recently updated first.
 * Respects hover-pause: when the user is hovering or recently hovered,
 * returns the previous sort order with new items appended at the bottom.
 */
const lastSortedKeys = ref<string[]>([])

const sortedMergeGroups = computed<MergeGroup[]>(() => {
  const groups = mergeGroups.value
  if (groups.length === 0) return []

  if (canReorder() || lastSortedKeys.value.length === 0) {
    // Sort by most recently updated first
    const sorted = [...groups].sort((a, b) => getGroupLatestTime(b) - getGroupLatestTime(a))
    lastSortedKeys.value = sorted.map(g => g.groupKey)
    return sorted
  }

  // Maintain previous order, append new items at the bottom
  const keyToGroup = new Map(groups.map(g => [g.groupKey, g]))
  const result: MergeGroup[] = []
  const seen = new Set<string>()

  for (const key of lastSortedKeys.value) {
    const g = keyToGroup.get(key)
    if (g) {
      result.push(g)
      seen.add(key)
    }
  }

  // Append any new groups at the bottom
  for (const g of groups) {
    if (!seen.has(g.groupKey)) {
      result.push(g)
    }
  }

  lastSortedKeys.value = result.map(g => g.groupKey)
  return result
})

/**
 * Formats an ISO datetime string to local date/time for display.
 * The backend stores and returns UTC; conversion to local happens here.
 */
function formatDateTime(isoString: string): string {
  if (!isoString) return ''
  return new Date(isoString).toLocaleString()
}

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

function handleActivityEvent(data: BranchActivity) {
  const existingIndex = activities.value.findIndex(
    a => a.branchName === data.branchName && a.projectId === data.projectId
  )

  if (existingIndex >= 0) {
    // Update existing entry in place (e.g. MR/approval data arrived)
    activities.value[existingIndex] = data
  } else {
    // Find the right insertion point: group by mergeGroupId or branchName
    const groupKey = data.mergeGroupId != null ? data.mergeGroupId : null
    let lastGroupIndex = -1

    if (groupKey != null) {
      lastGroupIndex = findLastIndexOf(
        activities.value,
        a => a.mergeGroupId === groupKey
      )
    }

    if (lastGroupIndex < 0) {
      lastGroupIndex = findLastIndexOf(
        activities.value,
        a => a.branchName === data.branchName
      )
    }

    if (lastGroupIndex >= 0) {
      activities.value.splice(lastGroupIndex + 1, 0, data)
    } else {
      activities.value.push(data)
    }
  }
}

function handleBranchDeleted(notification: BranchDeletedNotification) {
  const idx = activities.value.findIndex(
    a => a.branchName === notification.branchName && a.projectId === notification.projectId
  )
  if (idx >= 0) {
    activities.value.splice(idx, 1)
  }
}

function findLastIndexOf<T>(arr: T[], predicate: (item: T) => boolean): number {
  for (let i = arr.length - 1; i >= 0; i--) {
    if (predicate(arr[i])) return i
  }
  return -1
}

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
    startPolling()
  })

  eventSource.onerror = (event) => {
    streaming.value = false
    eventSource?.close()
    eventSource = null
    console.error('SSE stream error:', event)
    startPolling()
  }
}

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

// Refreshes MR and approval status for all currently displayed branches by
// streaming results via SSE as each branch is resolved.
async function refreshExistingBranches() {
  if (activities.value.length === 0) return

  // Deduplicate branch-project pairs
  const seen = new Set<string>()
  const branches: { branchName: string; projectId: number; lastUpdated: string | null; mergeGroupId: number | null }[] = []
  for (const a of activities.value) {
    const key = `${a.branchName}:${a.projectId}`
    if (!seen.has(key)) {
      seen.add(key)
      branches.push({
        branchName: a.branchName,
        projectId: a.projectId,
        lastUpdated: a.lastUpdated,
        mergeGroupId: a.mergeGroupId
      })
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

      // Process complete SSE events (separated by double newline)
      let eventEnd: number
      while ((eventEnd = buffer.indexOf('\n\n')) !== -1) {
        const eventText = buffer.slice(0, eventEnd)
        buffer = buffer.slice(eventEnd + 2)

        if (eventText.startsWith('event: done')) {
          return
        }

        // Handle branch deletion events
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

  // Check from 5 seconds before the last update time to avoid missing activity
  const since = new Date(lastUpdateTime.getTime() - 5000)
  const sinceParam = since.toISOString()

  try {
    const response = await fetch('/api/activity/poll')

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

    // Handle deletions
    if (data.deletedBranches) {
      for (const deleted of data.deletedBranches) {
        handleBranchDeleted(deleted)
      }
    }

    // Handle new/updated activities
    if (data.activities) {
      for (const activity of data.activities) {
        handleActivityEvent(activity)
      }
    }

    lastUpdateTime = new Date()
  } catch (err) {
    console.error('Poll request failed:', err)
  }
}

onMounted(async () => {
  timeIntervalId = setInterval(() => { now.value = Date.now() }, 60000)

  // Check for error in query parameters
  if (route.query.error && route.query.message) {
    errorMessage.value = route.query.message as string
    // Clean up URL by removing error params
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
  eventSource?.close()
  eventSource = null
  stopPolling()
})
</script>

<style scoped>
/* ---- Card container ---- */
.dashboard-cards {
  position: relative;
}

.streaming-indicator {
  display: block;
  margin: 0 auto 8px;
}

.card-container {
  display: flex;
  flex-direction: column;
  /* more vertical space between cards for clarity */
  gap: 20px;
  position: relative;
}

/* ---- Individual card ---- */
.merge-group-card {
  display: flex;
  border-radius: 8px;
  background: #fff;
  /* slightly thicker border on top/right/bottom to give cards more definition */
  border-top: 1.5px solid #e0e0e0;
  border-right: 1.5px solid #e0e0e0;
  border-bottom: 1.5px solid #e0e0e0;
  border-left: none; /* accent bar replaces left border */
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.08), 0 1px 2px rgba(0, 0, 0, 0.06);
  overflow: hidden;
  transition: box-shadow 0.2s ease;
}

.merge-group-card:hover {
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.12), 0 2px 4px rgba(0, 0, 0, 0.08);
}

/* Left accent bar */
.card-accent {
  width: 5px;
  flex-shrink: 0;
}

.card-accent.status-ready { background: #4caf50; }
.card-accent.status-open { background: #1976d2; }
.card-accent.status-waiting { background: #fb8c00; }

.card-body {
  flex: 1;
  padding: 14px 18px;
  min-width: 0;
}

/* ---- Card header ---- */
.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 10px;
  flex-wrap: wrap;
  gap: 8px;
}

.branch-info {
  display: flex;
  align-items: center;
  min-width: 0;
}

.branch-icon {
  color: #5f6368;
  margin-right: 6px;
  flex-shrink: 0;
}

.branch-name {
  font-weight: 600;
  font-size: 0.95rem;
  color: #1a1a2e;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.item-mr-title {
  font-size: 0.85rem;
  color: #5f6368;
  margin-left: 4px;
  flex: 1;
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.item-main {
  display: flex;
  align-items: center;
  flex: 1;
  min-width: 0;
}

.card-header-right {
  display: flex;
  align-items: center;
  gap: 14px;
  flex-shrink: 0;
}

/* ---- Status badges ---- */
.card-status-badge {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 0.78rem;
  font-weight: 600;
  padding: 2px 10px;
  border-radius: 12px;
  line-height: 1.4;
}

.card-status-badge .status-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex-shrink: 0;
}

.status-ready { background: #e8f5e9; color: #2e7d32; }
.status-ready .status-dot { background: #4caf50; }

.status-open { background: #e3f2fd; color: #1565c0; }
.status-open .status-dot { background: #1976d2; }

.status-waiting { background: #fff3e0; color: #e65100; }
.status-waiting .status-dot { background: #fb8c00; }

.card-time {
  font-size: 0.78rem;
  color: #9e9e9e;
  white-space: nowrap;
}

/* ---- Card items (repo rows) ---- */
.card-items {
  display: flex;
  flex-direction: column;
  gap: 0;
}

.card-item {
  display: flex;
  align-items: center;
  padding: 6px 0;
  border-top: 1px solid #f0f0f0;
  font-size: 0.85rem;
  gap: 12px;
}

.item-project {
  font-weight: 500;
  color: #37474f;
  flex-shrink: 0;
  white-space: nowrap;
}

.item-approvals {
  font-size: 0.78rem;
  color: #78909c;
  font-weight: 500;
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.item-mr-icon {
  display: inline-flex;
  align-items: center;
  margin-right: 6px;
}

.approval-icon {
  line-height: 0; /* shrink to icon size */
}

.approval-text {
  white-space: nowrap;
}

.item-time {
  font-size: 0.75rem;
  color: #9e9e9e;
  white-space: nowrap;
  flex-shrink: 0;
  min-width: 70px;
  text-align: right;
}

/* ---- TransitionGroup animations ---- */

/* Enter: fade + slide down */
.card-list-enter-active {
  transition: all 0.4s cubic-bezier(0.25, 0.8, 0.25, 1);
}

/* Leave: fade + shrink */
.card-list-leave-active {
  transition: all 0.3s cubic-bezier(0.25, 0.8, 0.25, 1);
  position: absolute;
  width: 100%;
}

.card-list-enter-from {
  opacity: 0;
  transform: translateY(-16px);
}

.card-list-leave-to {
  opacity: 0;
  transform: translateY(8px) scale(0.97);
}

/* FLIP move transition for reordering */
.card-list-move {
  transition: transform 0.5s cubic-bezier(0.25, 0.8, 0.25, 1);
}

/* ---- Responsive ---- */
@media (max-width: 600px) {
  .card-header {
    flex-direction: column;
    align-items: flex-start;
  }

  .card-header-right {
    width: 100%;
    justify-content: flex-start;
    flex-wrap: wrap;
  }

  .card-item {
    flex-wrap: wrap;
  }

  .item-time {
    width: 100%;
    text-align: left;
    margin-top: 2px;
  }
}
</style>
