<template>
  <v-container>
    <v-row justify="center" class="mt-4">
      <v-col cols="12" md="10" lg="9">
        <div class="d-flex align-center mb-4">
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
          <v-card variant="tonal" class="mb-4">
            <v-card-title class="text-h6">Project Summary</v-card-title>
            <v-card-text>
              <div class="summary-list">
                <div
                  v-for="item in details.activities"
                  :key="`${item.branchName}-${item.projectId}`"
                  class="summary-row"
                >
                  <div class="summary-repo">
                    <v-icon icon="mdi-source-repository" size="small" class="mr-2" />
                    <a
                      v-if="item.projectUrl"
                      class="summary-link"
                      :href="item.projectUrl"
                      target="_blank"
                      rel="noopener noreferrer"
                    >
                      {{ item.projectName }}
                    </a>
                    <span v-else>{{ item.projectName }}</span>
                  </div>
                  <div class="summary-labels">
                    <span class="text-caption text-medium-emphasis">Status:</span>
                    <v-chip size="small" :color="itemStatusColor(item)" variant="tonal">
                      {{ itemStatusLabel(item) }}
                    </v-chip>
                    <span class="text-caption text-medium-emphasis ml-2">Approvals:</span>
                    <span class="text-body-2">{{ itemApprovalsText(item) }}</span>
                  </div>
                </div>
              </div>
            </v-card-text>
          </v-card>

          <div class="repo-card-list">
            <v-card
              v-for="item in details.activities"
              :key="`${item.branchName}-${item.projectId}-details`"
              class="mb-4"
              variant="outlined"
            >
              <v-card-title class="d-flex align-center justify-space-between flex-wrap ga-2">
                <div class="d-flex align-center ga-2">
                  <v-icon icon="mdi-source-repository" size="small" />
                  <a
                    v-if="item.projectUrl"
                    class="repo-link"
                    :href="item.projectUrl"
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    {{ item.projectName }}
                  </a>
                  <span v-else>{{ item.projectName }}</span>
                </div>
                <v-chip size="small" :color="itemStatusColor(item)" variant="tonal">
                  {{ itemStatusLabel(item) }}
                </v-chip>
              </v-card-title>

              <v-card-text>
                <div class="mb-2 text-body-2">
                  <strong>Approvals:</strong>
                  {{ itemApprovalsText(item) }}
                </div>

                <div class="mb-3 text-body-2 mr-block">
                  <strong>Merge Request:</strong>
                  <a
                    v-if="item.mergeRequestTitle && item.mergeRequestUrl"
                    :href="item.mergeRequestUrl"
                    target="_blank"
                    rel="noopener noreferrer"
                    class="mr-link"
                  >
                    {{ item.mergeRequestTitle }}
                  </a>
                  <span v-else-if="item.mergeRequestTitle">{{ item.mergeRequestTitle }}</span>
                  <span v-else class="text-medium-emphasis">No Merge Request</span>
                </div>

                <div class="text-body-2">
                  <strong>External Jobs:</strong>
                  <div v-if="item.buildJobs && item.buildJobs.length > 0" class="jobs-list mt-2">
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
                  <div v-else class="text-medium-emphasis mt-2">No external jobs on latest pipeline</div>
                </div>
              </v-card-text>
            </v-card>
          </div>
        </template>
      </v-col>
    </v-row>
  </v-container>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
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
    const response = await fetch(`/api/activity/merge-groups/${mergeGroupId}`)

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
.summary-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.summary-row {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.summary-repo {
  display: flex;
  align-items: center;
}

.summary-labels {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
}

.summary-link,
.repo-link,
.mr-link,
.job-link {
  color: inherit;
  text-decoration: underline;
  text-underline-offset: 2px;
}

.mr-block {
  word-break: break-word;
}

.jobs-list {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
</style>
