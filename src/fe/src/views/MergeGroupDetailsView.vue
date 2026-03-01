<template>
  <v-container>
    <v-row class="mt-4">
      <!-- Back button: shown as left column on wide screens -->
      <v-col cols="auto" class="d-none d-lg-flex align-start" style="padding-top: 6px;">
        <v-btn variant="text" prepend-icon="mdi-arrow-left" @click="goBack">
          Back to Dashboard
        </v-btn>
      </v-col>

      <!-- Main content column -->
      <v-col cols="12" md="10" lg="9" class="mx-auto mx-lg-0">
        <!-- Back button: shown above content on narrow screens -->
        <div class="d-flex align-center mb-4 d-lg-none">
          <v-btn variant="text" prepend-icon="mdi-arrow-left" @click="goBack">
            Back to Dashboard
          </v-btn>
        </div>

        <v-alert
          v-if="errorMessage"
          type="error"
          variant="tonal"
          closable
          class="mb-4"
          @click:close="errorMessage = ''"
        >
          {{ errorMessage }}
        </v-alert>

        <div v-if="initialLoading" class="text-center pa-8">
          <p class="text-body-1 text-grey">Loading merge group details...</p>
        </div>

        <template v-else>
          <!-- Summary: merge group name + overall status -->
          <div class="merge-group-header mb-5">
            <div class="d-flex align-center flex-wrap ga-3">
              <v-icon icon="mdi-source-merge" size="small" color="primary" />
              <span class="text-h6 font-weight-bold">{{ mergeGroupName }}</span>
              <span v-if="isFullyLoaded" class="card-status-badge" :class="overallStatusClass">
                <span class="status-dot" />
                {{ overallStatusLabel }}
              </span>
              <span v-else class="skeleton-badge"><span class="skeleton-shimmer" /></span>
            </div>
          </div>

          <div v-if="activities.length === 0 && !initialPhase" class="text-center pa-8">
            <v-icon icon="mdi-source-branch" size="64" color="grey" class="mb-4" />
            <p class="text-h6 text-grey">No branches in this merge group</p>
          </div>

          <!-- Branch cards -->
          <div v-else class="repo-card-list">
            <div
              v-for="item in activities"
              :key="`${item.branchName}-${item.projectId}-details`"
              class="branch-card mb-4"
            >
              <div class="card-accent" :class="itemStatusClass(item)" />
              <div class="card-body">
                <!-- Card header: title + status chip -->
                <div class="card-header">
                  <div class="branch-card-title">
                    <v-icon icon="mdi-source-repository" size="small" class="title-icon" />
                    <a
                      v-if="item.projectUrl"
                      class="branch-title-link"
                      :href="branchUrl(item)"
                      target="_blank"
                      rel="noopener noreferrer"
                    >
                      {{ item.projectName }} | {{ item.branchName }}
                    </a>
                    <span v-else class="branch-title-text">{{ item.projectName }} | {{ item.branchName }}</span>
                  </div>
                  <v-chip v-if="item.hasMergeRequest !== null" size="small" :color="itemStatusColor(item)" variant="tonal" class="flex-shrink-0">
                    {{ itemStatusLabel(item) }}
                  </v-chip>
                  <span v-else class="skeleton-chip"><span class="skeleton-shimmer" /></span>
                </div>

                <!-- Detail rows -->
                <div class="detail-row">
                  <span class="detail-label">Approvals:</span>
                  <span class="detail-value">
                    <span v-if="item.hasMergeRequest === null" class="skeleton-detail"><span class="skeleton-shimmer" /></span>
                    <template v-else>{{ itemApprovalsText(item) }}</template>
                  </span>
                </div>

                <div class="detail-row">
                  <span class="detail-label">Merge Request:</span>
                  <span class="detail-value">
                    <a
                      v-if="item.mergeRequestTitle && item.mergeRequestUrl"
                      :href="item.mergeRequestUrl"
                      target="_blank"
                      rel="noopener noreferrer"
                      class="detail-link"
                    >
                      {{ item.mergeRequestTitle }}
                    </a>
                    <span v-else-if="item.mergeRequestTitle">{{ item.mergeRequestTitle }}</span>
                    <span v-else-if="item.hasMergeRequest === false" class="text-medium-emphasis">
                      No Merge Request
                      <a
                        v-if="item.projectUrl"
                        :href="createMrUrl(item)"
                        target="_blank"
                        rel="noopener noreferrer"
                        class="detail-link ml-1"
                      >
                        — Create
                      </a>
                    </span>
                    <span v-else class="skeleton-detail"><span class="skeleton-shimmer" /></span>
                  </span>
                </div>

                <div class="detail-row align-start">
                  <span class="detail-label">External Jobs:</span>
                  <span class="detail-value">
                    <div v-if="item.buildJobs && item.buildJobs.length > 0" class="jobs-list">
                      <v-chip
                        v-for="job in item.buildJobs"
                        :key="`${item.projectId}-${job.name}-${job.status}`"
                        size="small"
                        :color="jobStatusColor(job.status)"
                        variant="outlined"
                        :prepend-icon="jobStatusIcon(job.status)"
                      >
                        <a
                          v-if="job.url"
                          :href="job.url"
                          target="_blank"
                          rel="noopener noreferrer"
                          class="job-link"
                        >
                          {{ job.name }}
                        </a>
                        <span v-else>{{ job.name }}</span>
                      </v-chip>
                    </div>
                    <span v-else-if="item.hasMergeRequest != null" class="text-medium-emphasis">No external jobs on latest pipeline</span>
                    <span v-else class="skeleton-detail"><span class="skeleton-shimmer" /></span>
                  </span>
                </div>
              </div>
            </div>
          </div>
        </template>
      </v-col>
    </v-row>
  </v-container>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, ref, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAppLoading } from '@/composables/useAppLoading'

interface BranchBuildJob {
  name: string
  status: string
  url?: string | null
}

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

interface MergeGroup {
  id: number
  name: string
  branches: BranchWithActivity[]
}

const FAST_POLL_INTERVAL_MS = 1000
const NORMAL_POLL_INTERVAL_MS = 5000
const FAST_POLL_DURATION_MS = 5000

const route = useRoute()
const router = useRouter()
const { setAppLoading } = useAppLoading()

const activities = ref<BranchWithActivity[]>([])
const mergeGroupName = ref('')
const initialLoading = ref(true)
const initialPhase = ref(false)
const errorMessage = ref('')

let pollIntervalId: ReturnType<typeof setInterval> | null = null
let fastPollTimeoutId: ReturnType<typeof setTimeout> | null = null

const isFullyLoaded = computed<boolean>(() => {
  return activities.value.length > 0 && activities.value.every(b => b.hasMergeRequest !== null)
})

const overallStatusLabel = computed<string>(() => {
  const statuses = activities.value.map(item => itemStatusLabel(item))
  if (statuses.some(s => s === 'Waiting')) return 'Waiting'
  if (statuses.length > 0 && statuses.every(s => s === 'Ready')) return 'Ready'
  if (statuses.length === 0) return 'Loading'
  return 'Open'
})

const overallStatusClass = computed<string>(() => {
  if (overallStatusLabel.value === 'Ready') return 'status-ready'
  if (overallStatusLabel.value === 'Open') return 'status-open'
  return 'status-waiting'
})

function itemStatusClass(item: BranchWithActivity): string {
  const label = itemStatusLabel(item)
  if (label === 'Ready') return 'status-ready'
  if (label === 'Open') return 'status-open'
  return 'status-waiting'
}

function branchUrl(item: BranchWithActivity): string {
  if (!item.projectUrl) return ''
  return `${item.projectUrl}/-/tree/${encodeURIComponent(item.branchName)}?ref_type=heads`
}

function createMrUrl(item: BranchWithActivity): string {
  if (!item.projectUrl) return ''
  return `${item.projectUrl}/-/merge_requests/new?merge_request[source_branch]=${encodeURIComponent(item.branchName)}`
}

function itemStatusLabel(item: BranchWithActivity): string {
  if (!item.hasMergeRequest) {
    return 'Waiting'
  }

  if (item.approvalsRequired != null && item.approvalsGiven != null) {
    return item.approvalsGiven >= item.approvalsRequired ? 'Ready' : 'Open'
  }

  return 'Open'
}

function itemStatusColor(item: BranchWithActivity): string {
  const status = itemStatusLabel(item)
  if (status === 'Ready') return 'success'
  if (status === 'Open') return 'info'
  return 'warning'
}

function itemApprovalsText(item: BranchWithActivity): string {
  if (!item.hasMergeRequest || item.approvalsGiven == null || item.approvalsRequired == null) {
    return 'Not available'
  }

  if (item.approvalsRequired === 0) {
    return `${item.approvalsGiven}/${item.approvalsRequired} (No approval needed)`
  }

  return `${item.approvalsGiven}/${item.approvalsRequired}`
}

function jobStatusIcon(status: string): string {
  const normalized = status.toLowerCase()
  if (normalized === 'success') return 'mdi-check-circle'
  if (normalized === 'failed' || normalized === 'failure') return 'mdi-close-circle'
  if (normalized === 'running') return 'mdi-progress-clock'
  if (normalized === 'pending') return 'mdi-timer-sand'
  if (normalized === 'canceled' || normalized === 'cancelled') return 'mdi-cancel'
  return 'mdi-help-circle'
}

function jobStatusColor(status: string): string {
  const normalized = status.toLowerCase()
  if (normalized === 'success') return 'success'
  if (normalized === 'failed' || normalized === 'failure') return 'error'
  if (normalized === 'running') return 'info'
  if (normalized === 'pending') return 'warning'
  if (normalized === 'canceled' || normalized === 'cancelled') return 'secondary'
  return 'default'
}

function goBack() {
  router.push('/')
}

// --- Incremental data management ---

function handleActivityEvent(data: BranchWithActivity) {
  const existingIndex = activities.value.findIndex(
    a => a.branchName === data.branchName && a.projectId === data.projectId
  )

  if (existingIndex >= 0) {
    activities.value[existingIndex] = data
  } else {
    activities.value.push(data)
  }
}

function getMergeGroupId(): string {
  return route.params.mergeGroupId as string
}

/**
 * Polls the backend for a full merge group snapshot and reconciles with the displayed list.
 * Existing branches are updated, new ones added, and removed branches are cleaned up.
 */
async function pollMergeGroup() {
  const mergeGroupId = getMergeGroupId()
  if (!mergeGroupId) return

  try {
    const response = await fetch(`/api/merge-groups/${mergeGroupId}/refresh`, {
      method: 'POST'
    })

    if (response.status === 401) {
      console.warn('Poll returned 401, stopping polling')
      stopPolling()
      return
    }

    if (response.status === 404) {
      errorMessage.value = 'Merge group not found.'
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

    const data: MergeGroup = await response.json()

    // Update merge group name if changed
    if (data.name && data.name !== mergeGroupName.value) {
      mergeGroupName.value = data.name
      updateRouteTitle(data.name)
    }

    // Remove items no longer present in the response
    const incomingIds = new Set<number>(data.branches.map(b => b.id))
    activities.value = activities.value.filter(a => incomingIds.has(a.id))

    // Update or add items from the response
    for (const activity of data.branches) {
      handleActivityEvent(activity)
    }
  } catch (err) {
    console.error('Merge group poll failed:', err)
  }
}

// --- Polling lifecycle ---

function startPolling() {
  if (pollIntervalId !== null) return

  // Start with fast polling (every 1s for 5 seconds)
  initialPhase.value = true
  setAppLoading(true)
  pollIntervalId = setInterval(pollMergeGroup, FAST_POLL_INTERVAL_MS)

  // After 5 seconds, switch to normal polling interval
  fastPollTimeoutId = setTimeout(() => {
    initialPhase.value = false
    setAppLoading(false)
    if (pollIntervalId !== null) {
      clearInterval(pollIntervalId)
      pollIntervalId = setInterval(pollMergeGroup, NORMAL_POLL_INTERVAL_MS)
    }
    fastPollTimeoutId = null
  }, FAST_POLL_DURATION_MS)

  // Fire the first poll immediately
  pollMergeGroup()
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

function updateRouteTitle(name: string) {
  const mergeGroupId = getMergeGroupId()
  if (route.query.title !== name) {
    router.replace({
      name: 'merge-group-details',
      params: { mergeGroupId },
      query: { title: name }
    })
  }
}

onMounted(async () => {
  const mergeGroupId = getMergeGroupId()
  if (!mergeGroupId) {
    errorMessage.value = 'Merge group id is missing.'
    initialLoading.value = false
    return
  }

  // Use the title from the route query as initial name if available
  if (route.query.title) {
    mergeGroupName.value = route.query.title as string
  }

  initialLoading.value = false
  startPolling()
})

onUnmounted(() => {
  stopPolling()
})
</script>

<style scoped>
/* ---- Merge group header ---- */
.merge-group-header {
  padding: 2px 0 6px;
}

/* ---- Status badges (shared with home page style) ---- */
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

/* ---- Branch card — mirrors home page merge-group-card style ---- */
.branch-card {
  display: flex;
  border-radius: 8px;
  background: #fff;
  border-top: 1.5px solid #e0e0e0;
  border-right: 1.5px solid #e0e0e0;
  border-bottom: 1.5px solid #e0e0e0;
  border-left: none;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.08), 0 1px 2px rgba(0, 0, 0, 0.06);
  overflow: hidden;
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
  margin-bottom: 12px;
  flex-wrap: wrap;
  gap: 8px;
}

.branch-card-title {
  display: flex;
  align-items: center;
  min-width: 0;
  gap: 6px;
}

.title-icon {
  color: #5f6368;
  flex-shrink: 0;
}

.branch-title-link,
.branch-title-text {
  font-weight: 600;
  font-size: 0.95rem;
  color: #1a1a2e;
}

.branch-title-link {
  text-decoration: underline;
  text-underline-offset: 2px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* ---- Detail rows ---- */
.detail-row {
  display: flex;
  align-items: baseline;
  gap: 10px;
  font-size: 0.85rem;
  margin-bottom: 8px;
  flex-wrap: wrap;
  word-break: break-word;
}

.detail-row.align-start {
  align-items: flex-start;
}

.detail-label {
  font-weight: 600;
  color: #37474f;
  white-space: nowrap;
  flex-shrink: 0;
}

.detail-value {
  color: #37474f;
  flex: 1;
  min-width: 0;
}

.detail-link,
.job-link {
  color: inherit;
  text-decoration: underline;
  text-underline-offset: 2px;
}

/* ---- Jobs list ---- */
.jobs-list {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 4px;
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

/* Skeleton for status badge */
.skeleton-badge {
  display: inline-block;
  width: 60px;
  height: 20px;
  border-radius: 12px;
  overflow: hidden;
}

/* Skeleton for status chip on branch cards */
.skeleton-chip {
  display: inline-block;
  width: 56px;
  height: 24px;
  border-radius: 12px;
  overflow: hidden;
  flex-shrink: 0;
}

/* Skeleton for detail value rows (approvals, MR, build jobs) */
.skeleton-detail {
  display: inline-block;
  width: 120px;
  height: 14px;
  border-radius: 4px;
  overflow: hidden;
  vertical-align: middle;
}

/* ---- Responsive ---- */
@media (max-width: 600px) {
  .card-header {
    flex-direction: column;
    align-items: flex-start;
  }
}
</style>
