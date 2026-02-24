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

        <div v-if="loading" class="text-center pa-8">
          <v-progress-circular indeterminate color="primary" size="44" />
          <p class="mt-4 text-body-1">Loading merge group details...</p>
        </div>

        <template v-else>
          <!-- Summary: merge group name + overall status -->
          <div class="merge-group-header mb-5">
            <div class="d-flex align-center flex-wrap ga-3">
              <v-icon icon="mdi-source-merge" size="small" color="primary" />
              <span class="text-h6 font-weight-bold">{{ details.mergeGroupName }}</span>
              <span class="card-status-badge" :class="overallStatusClass">
                <span class="status-dot" />
                {{ overallStatusLabel }}
              </span>
            </div>
          </div>

          <!-- Branch cards -->
          <div class="repo-card-list">
            <div
              v-for="item in details.activities"
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
                  <v-chip size="small" :color="itemStatusColor(item)" variant="tonal" class="flex-shrink-0">
                    {{ itemStatusLabel(item) }}
                  </v-chip>
                </div>

                <!-- Detail rows -->
                <div class="detail-row">
                  <span class="detail-label">Approvals:</span>
                  <span class="detail-value">{{ itemApprovalsText(item) }}</span>
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
                    <span v-else class="text-medium-emphasis">No Merge Request</span>
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
                    <span v-else class="text-medium-emphasis">No external jobs on latest pipeline</span>
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
import { onMounted, ref, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'

interface BranchBuildJob {
  name: string
  status: string
  url?: string | null
}

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
  mergeRequestUrl?: string | null
  projectUrl?: string | null
  buildJobs?: BranchBuildJob[] | null
}

interface MergeGroupDetailsResponse {
  mergeGroupId: number
  mergeGroupName: string
  activities: BranchActivity[]
}

const route = useRoute()
const router = useRouter()

const loading = ref(true)
const errorMessage = ref('')
const details = ref<MergeGroupDetailsResponse>({
  mergeGroupId: 0,
  mergeGroupName: '',
  activities: []
})

const overallStatusLabel = computed<string>(() => {
  const statuses = details.value.activities.map(item => itemStatusLabel(item))
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

function itemStatusClass(item: BranchActivity): string {
  const label = itemStatusLabel(item)
  if (label === 'Ready') return 'status-ready'
  if (label === 'Open') return 'status-open'
  return 'status-waiting'
}

function branchUrl(item: BranchActivity): string {
  if (!item.projectUrl) return ''
  return `${item.projectUrl}/-/tree/${encodeURIComponent(item.branchName)}?ref_type=heads`
}

function createMrUrl(item: BranchActivity): string {
  if (!item.projectUrl) return ''
  return `${item.projectUrl}/-/merge_requests/new?merge_request[source_branch]=${encodeURIComponent(item.branchName)}`
}

function itemStatusLabel(item: BranchActivity): string {
  if (!item.hasMergeRequest) {
    return 'Waiting'
  }

  if (item.approvalsRequired != null && item.approvalsGiven != null) {
    return item.approvalsGiven >= item.approvalsRequired ? 'Ready' : 'Open'
  }

  return 'Open'
}

function itemStatusColor(item: BranchActivity): string {
  const status = itemStatusLabel(item)
  if (status === 'Ready') return 'success'
  if (status === 'Open') return 'info'
  return 'warning'
}

function itemApprovalsText(item: BranchActivity): string {
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

async function loadDetails() {
  loading.value = true
  errorMessage.value = ''

  const mergeGroupId = route.params.mergeGroupId as string
  if (!mergeGroupId) {
    errorMessage.value = 'Merge group id is missing.'
    loading.value = false
    return
  }

  try {
    const response = await fetch(`/api/merge-groups/${mergeGroupId}`)

    if (response.status === 401) {
      errorMessage.value = 'You are not authenticated. Please sign in again.'
      loading.value = false
      return
    }

    if (response.status === 404) {
      errorMessage.value = 'Merge group not found.'
      loading.value = false
      return
    }

    if (!response.ok) {
      errorMessage.value = 'Failed to load merge group details.'
      loading.value = false
      return
    }

    const data: MergeGroupDetailsResponse = await response.json()
    details.value = data

    if (route.query.title !== data.mergeGroupName) {
      router.replace({
        name: 'merge-group-details',
        params: { mergeGroupId },
        query: { title: data.mergeGroupName }
      })
    }
  } catch (err) {
    console.error('Failed to load merge group details:', err)
    errorMessage.value = 'Failed to load merge group details.'
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  await loadDetails()
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

/* ---- Responsive ---- */
@media (max-width: 600px) {
  .card-header {
    flex-direction: column;
    align-items: flex-start;
  }
}
</style>
