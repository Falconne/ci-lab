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

        <div v-else-if="sortedMergeGroups.length === 0 && !initialPhase" class="text-center pa-8">
          <v-icon icon="mdi-source-branch" size="64" color="grey" class="mb-4" />
          <p class="text-h6 text-grey">No active branches in the last 14 days</p>
        </div>

        <div v-else-if="sortedMergeGroups.length === 0 && initialPhase" class="text-center pa-8">
          <v-progress-circular indeterminate color="primary" size="48" />
          <p class="mt-4 text-body-1">Loading dashboard...</p>
        </div>

        <div v-else class="dashboard-cards">
          <v-progress-circular
            v-if="initialPhase"
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
              @click="openMergeGroupDetails(group)"
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
                      <span
                        v-else-if="item.hasMergeRequest === false"
                        class="item-no-mr"
                      >
                        | No Merge Request
                      </span>
                    </span>
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

interface BranchRecord {
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
  mergeRequestUrl?: string | null
  projectUrl?: string | null
  buildJobs?: BranchBuildJob[] | null
  branchInProjectId?: number | null
}

interface BranchBuildJob {
  name: string
  status: string
  url?: string | null
}

interface DashboardResponse {
  branches: BranchRecord[]
}

interface MergeGroup {
  groupKey: string
  branchName: string
  items: BranchRecord[]
}

type GroupStatus = 'ready' | 'open' | 'waiting'

const FAST_POLL_INTERVAL_MS = 1000
const NORMAL_POLL_INTERVAL_MS = 5000
const FAST_POLL_DURATION_MS = 5000

const route = useRoute()
const router = useRouter()
const { currentUser, loadCurrentUser } = useCurrentUser()

const activities = ref<BranchRecord[]>([])
const initialLoading = ref(true)
const authenticated = computed(() => currentUser.value !== null)
const initialPhase = ref(false)
const errorMessage = ref('')
const now = ref(Date.now())

let pollIntervalId: ReturnType<typeof setInterval> | null = null
let timeIntervalId: ReturnType<typeof setInterval> | null = null
let fastPollTimeoutId: ReturnType<typeof setTimeout> | null = null

// --- Status logic ---

function getGroupStatus(group: MergeGroup): GroupStatus {
  const statusPriority: GroupStatus[] = ['waiting', 'open', 'ready']
  let worstIndex = 2 // start with 'ready' (best)
  for (const item of group.items) {
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

function itemApprovalsText(item: BranchRecord): string {
  if (!item.hasMergeRequest || item.approvalsGiven == null || item.approvalsRequired == null) return ''
  return `${item.approvalsGiven}/${item.approvalsRequired}`
}

function approvalIconColor(item: BranchRecord): string {
  if (!item.hasMergeRequest || item.approvalsGiven == null || item.approvalsRequired == null) {
    return 'grey'
  }
  if (item.approvalsGiven >= item.approvalsRequired) {
    return 'green'
  }
  return 'grey'
}

function approvalsTooltip(item: BranchRecord): string {
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

/**
 * Groups activities by mergeGroupId (from DB) when available,
 * falling back to branchName for items without a mergeGroupId.
 */
const mergeGroups = computed<MergeGroup[]>(() => {
  const groups = new Map<string, { branchName: string; items: BranchRecord[] }>()
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
 * Sorted merge groups — always ordered by most recently updated first.
 */
const sortedMergeGroups = computed<MergeGroup[]>(() => {
  const groups = mergeGroups.value
  if (groups.length === 0) return []
  return [...groups].sort((a, b) => getGroupLatestTime(b) - getGroupLatestTime(a))
})

/**
 * Formats an ISO datetime string to local date/time for display.
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

function handleActivityEvent(data: BranchRecord) {
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

function findLastIndexOf<T>(arr: T[], predicate: (item: T) => boolean): number {
  for (let i = arr.length - 1; i >= 0; i--) {
    if (predicate(arr[i])) return i
  }
  return -1
}

/**
 * Polls the backend for a full dashboard snapshot and reconciles with the displayed list.
 * New branches are added, existing ones updated, and removed branches are cleaned up.
 */
async function pollDashboard() {
  try {
    const response = await fetch('/api/activity/refresh', {
      method: 'POST'
    })

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

    const data: DashboardResponse = await response.json()

    // Remove items no longer present in the response
    const incomingIds = new Set<number>(
      data.branches
        .filter(b => b.branchInProjectId != null)
        .map(b => b.branchInProjectId!)
    )
    activities.value = activities.value.filter(
      a => a.branchInProjectId == null || incomingIds.has(a.branchInProjectId)
    )

    // Update or add items from the response
    for (const activity of data.branches) {
      handleActivityEvent(activity)
    }
  } catch (err) {
    console.error('Dashboard poll failed:', err)
  }
}

function startPolling() {
  if (pollIntervalId !== null) return

  // Start with fast polling (every 1s for 5 seconds)
  initialPhase.value = true
  pollIntervalId = setInterval(pollDashboard, FAST_POLL_INTERVAL_MS)

  // After 5 seconds, switch to normal polling interval
  fastPollTimeoutId = setTimeout(() => {
    initialPhase.value = false
    if (pollIntervalId !== null) {
      clearInterval(pollIntervalId)
      pollIntervalId = setInterval(pollDashboard, NORMAL_POLL_INTERVAL_MS)
    }
    fastPollTimeoutId = null
  }, FAST_POLL_DURATION_MS)

  // Fire the first poll immediately
  pollDashboard()
}

function stopPolling() {
  if (pollIntervalId !== null) {
    clearInterval(pollIntervalId)
    pollIntervalId = null
  }
  if (fastPollTimeoutId !== null) {
    clearTimeout(fastPollTimeoutId)
    fastPollTimeoutId = null
  }
}

function openMergeGroupDetails(group: MergeGroup) {
  const mergeGroupId = group.items.find(item => item.mergeGroupId != null)?.mergeGroupId
  if (mergeGroupId == null) {
    return
  }

  router.push({
    name: 'merge-group-details',
    params: { mergeGroupId: mergeGroupId.toString() },
    query: { title: group.branchName }
  })
}

onMounted(async () => {
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
    startPolling()
  } catch (err) {
    console.error('Failed to load dashboard:', err)
    initialLoading.value = false
  }
})

onUnmounted(() => {
  if (timeIntervalId) clearInterval(timeIntervalId)
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
  cursor: pointer;
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

.item-no-mr {
  font-size: 0.85rem;
  color: #9e9e9e;
  margin-left: 4px;
  white-space: nowrap;
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
