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
          <p class="text-body-1 text-grey">Loading...</p>
        </div>

        <div v-else-if="sortedMergeGroups.length === 0 && !initialPhase" class="text-center pa-8">
          <v-icon icon="mdi-source-branch" size="64" color="grey" class="mb-4" />
          <p class="text-h6 text-grey">No active branches in the last 14 days</p>
        </div>

        <div v-else-if="sortedMergeGroups.length === 0 && initialPhase" class="text-center pa-8">
          <p class="text-body-1 text-grey">Loading dashboard...</p>
        </div>

        <div v-else class="dashboard-cards">
          <TransitionGroup name="card-list" tag="div" class="card-container">
            <div
              v-for="group in sortedMergeGroups"
              :key="group.id.toString()"
              class="merge-group-card"
              :data-merge-group-id="group.id"
              @click="openMergeGroupDetails(group)"
            >
              <div class="card-accent" :class="groupStatusClass(group)" />
              <div class="card-body">
                <div class="card-header">
                                  <div class="branch-info">
                      <v-icon icon="mdi-source-branch" size="small" class="branch-icon" />
                      <span class="branch-name">{{ group.name }}</span>
                    </div>
                  <div class="card-header-right">
                    <span v-if="isGroupFullyLoaded(group)" class="card-status-badge" :class="groupStatusClass(group)">
                      <span class="status-dot" />
                      {{ groupStatusLabel(group) }}
                    </span>
                    <span v-else class="skeleton-badge"><span class="skeleton-shimmer" /></span>
                    <span class="card-time">{{ groupTimeAgo(group) }}</span>
                  </div>
                </div>
                <div class="card-items">
                  <div
                    v-for="item in group.branches"
                    :key="`${group.id}-${item.projectId}`"
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
                      <template v-if="isBranchLoading(item)">
                        <span class="item-skeleton-inline"><span class="skeleton-shimmer" /></span>
                      </template>
                      <template v-else>
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
                      </template>
                    </span>
                    <v-tooltip
                      v-if="!isBranchLoading(item) && itemApprovalsText(item)"
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
                      <span v-if="isBranchLoading(item)" class="skeleton-time"><span class="skeleton-shimmer" /></span>
                      <v-tooltip v-else-if="item.lastUpdated" location="top" :text="formatDateTime(item.lastUpdated)">
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
import { useAppLoading } from '@/composables/useAppLoading'

interface BranchWithActivity {
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

interface BranchBuildJob {
  name: string
  status: string
  url?: string | null
}

interface MergeGroup {
  id: number
  name: string
  branches: BranchWithActivity[]
}

type GroupStatus = 'ready' | 'open' | 'waiting'

const FAST_POLL_INTERVAL_MS = 1000
const NORMAL_POLL_INTERVAL_MS = 5000
const FAST_POLL_DURATION_MS = 5000

const route = useRoute()
const router = useRouter()
const { currentUser, loadCurrentUser } = useCurrentUser()
const { setAppLoading } = useAppLoading()

const mergeGroups = ref<MergeGroup[]>([])
const initialLoading = ref(true)
const authenticated = computed(() => currentUser.value !== null)
const initialPhase = ref(false)
const errorMessage = ref('')
const now = ref(Date.now())

let pollIntervalId: ReturnType<typeof setInterval> | null = null
let timeIntervalId: ReturnType<typeof setInterval> | null = null
let fastPollTimeoutId: ReturnType<typeof setTimeout> | null = null

// --- Status logic ---

/**
 * Whether a branch's detail data (MR status, approvals, build jobs) has not yet been fetched.
 */
function isBranchLoading(item: BranchWithActivity): boolean {
  return item.hasMergeRequest === null
}

/**
 * Whether all branches in a group have had their details resolved.
 * The group's overall status badge should only be shown when this is true.
 */
function isGroupFullyLoaded(group: MergeGroup): boolean {
  return group.branches.length > 0 && group.branches.every(b => b.hasMergeRequest !== null)
}

function getGroupStatus(group: MergeGroup): GroupStatus {
  const statusPriority: GroupStatus[] = ['waiting', 'open', 'ready']
  let worstIndex = 2 // start with 'ready' (best)
  for (const item of group.branches) {
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

function itemApprovalsText(item: BranchWithActivity): string {
  if (!item.hasMergeRequest || item.approvalsGiven == null || item.approvalsRequired == null) return ''
  return `${item.approvalsGiven}/${item.approvalsRequired}`
}

function approvalIconColor(item: BranchWithActivity): string {
  if (!item.hasMergeRequest || item.approvalsGiven == null || item.approvalsRequired == null) {
    return 'grey'
  }
  if (item.approvalsGiven >= item.approvalsRequired) {
    return 'green'
  }
  return 'grey'
}

function approvalsTooltip(item: BranchWithActivity): string {
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

/**
 * Returns the latest lastUpdated timestamp across all branches in the group, or null.
 */
function groupLatestTimestamp(group: MergeGroup): string | null {
  let latest: string | null = null
  for (const b of group.branches) {
    if (b.lastUpdated && (!latest || b.lastUpdated > latest)) {
      latest = b.lastUpdated
    }
  }
  return latest
}

function groupTimeAgo(group: MergeGroup): string {
  const ts = groupLatestTimestamp(group)
  return ts ? formatTimeAgo(ts) : ''
}

/**
 * Truncates a title to 222 characters, appending "..." when it was longer.
 */
function truncateTitle(title: string): string {
  if (title.length <= 222) return title
  return title.slice(0, 222) + '...'
}

/**
 * Sorted merge groups — always ordered by most recently updated first.
 */
const sortedMergeGroups = computed<MergeGroup[]>(() => {
  if (mergeGroups.value.length === 0) return []
  return [...mergeGroups.value].sort((a, b) => {
    const aTs = groupLatestTimestamp(a)
    const bTs = groupLatestTimestamp(b)
    // Groups with no timestamp (newly discovered, not yet refreshed) sort to the top
    if (!aTs && !bTs) return 0
    if (!aTs) return -1
    if (!bTs) return 1
    return new Date(bTs).getTime() - new Date(aTs).getTime()
  })
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

/**
 * Polls the backend for a full dashboard snapshot and reconciles with the displayed list.
 * Groups are updated in place; removed groups are cleaned up.
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

    const data: MergeGroup[] = await response.json()

    const incomingIds = new Set<number>(data.map(g => g.id))

    // Remove groups no longer present in the response
    mergeGroups.value = mergeGroups.value.filter(g => incomingIds.has(g.id))

    // Update existing groups or add new ones
    for (const group of data) {
      const existingIndex = mergeGroups.value.findIndex(g => g.id === group.id)
      if (existingIndex >= 0) {
        mergeGroups.value[existingIndex] = group
      } else {
        mergeGroups.value.push(group)
      }
    }
  } catch (err) {
    console.error('Dashboard poll failed:', err)
  }
}

function startPolling() {
  if (pollIntervalId !== null) return

  // Start with fast polling (every 1s for 5 seconds)
  initialPhase.value = true
  setAppLoading(true)
  pollIntervalId = setInterval(pollDashboard, FAST_POLL_INTERVAL_MS)

  // After 5 seconds, switch to normal polling interval
  fastPollTimeoutId = setTimeout(() => {
    initialPhase.value = false
    setAppLoading(false)
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
  setAppLoading(false)
}

function openMergeGroupDetails(group: MergeGroup) {
  router.push({
    name: 'merge-group-details',
    params: { mergeGroupId: group.id.toString() },
    query: { title: group.name }
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

/* ---- Skeleton loading shimmer ---- */
@keyframes shimmer {
  0% { background-position: -200px 0; }
  100% { background-position: 200px 0; }
}

.skeleton-shimmer {
  display: block;
  width: 100%;
  height: 100%;
  border-radius: inherit;
  background: linear-gradient(90deg, #e0e0e0 25%, #f5f5f5 50%, #e0e0e0 75%);
  background-size: 400px 100%;
  animation: shimmer 1.5s ease-in-out infinite;
}

/* Skeleton for status badge — replaces the real badge while data is loading */
.skeleton-badge {
  display: inline-block;
  width: 60px;
  height: 20px;
  border-radius: 12px;
  overflow: hidden;
}

/* Skeleton for MR title area — inline with project name */
.item-skeleton-inline {
  display: inline-block;
  width: 100px;
  height: 14px;
  border-radius: 4px;
  overflow: hidden;
  margin-left: 6px;
  vertical-align: middle;
}

/* Skeleton for the time column */
.skeleton-time {
  display: inline-block;
  width: 60px;
  height: 12px;
  border-radius: 4px;
  overflow: hidden;
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
