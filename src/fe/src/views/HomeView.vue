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

        <div v-else-if="mergeGroups.length === 0 && !streaming" class="text-center pa-8">
          <v-icon icon="mdi-source-branch" size="64" color="grey" class="mb-4" />
          <p class="text-h6 text-grey">No active branches in the last 14 days</p>
        </div>

        <div v-else>
          <h2 class="text-h5 mb-4">
            Dashboard
            <v-progress-circular
              v-if="streaming"
              indeterminate
              color="primary"
              size="18"
              width="2"
              class="ml-2"
            />
          </h2>
          <v-table class="dashboard-table" density="comfortable">
            <thead>
              <tr>
                <th>Branch</th>
                <th>Repository</th>
                <th class="text-center">MR</th>
                <th class="text-center">Approvals</th>
                <th class="text-end">Last Updated</th>
              </tr>
            </thead>
            <tbody>
              <template v-for="group in mergeGroups" :key="group.groupKey">
                <tr v-for="(item, idx) in group.items" :key="`${group.groupKey}-${item.projectId}`">
                  <!-- Branch name cell: only show on first row of group, span all rows -->
                  <td
                    v-if="idx === 0"
                    :rowspan="group.items.length"
                    class="branch-name-cell font-weight-medium"
                  >
                    <v-icon icon="mdi-source-branch" size="small" class="mr-1" />
                    {{ group.branchName }}
                  </td>

                  <!-- Repository name -->
                  <td>{{ item.projectName }}</td>

                  <!-- MR status -->
                  <td class="text-center">
                    <v-progress-circular
                      v-if="item.hasMergeRequest === null"
                      indeterminate
                      color="grey"
                      size="16"
                      width="2"
                    />
                    <v-icon
                      v-else-if="item.hasMergeRequest"
                      icon="mdi-check-circle"
                      color="primary"
                      size="small"
                    />
                    <v-icon
                      v-else
                      icon="mdi-minus-circle-outline"
                      color="grey"
                      size="small"
                    />
                  </td>

                  <!-- Approval status -->
                  <td class="text-center">
                    <v-progress-circular
                      v-if="item.hasMergeRequest === null"
                      indeterminate
                      color="grey"
                      size="16"
                      width="2"
                    />
                    <template v-else-if="item.hasMergeRequest && item.approvalsRequired != null && item.approvalsGiven != null">
                      <v-chip
                        :color="item.approvalsGiven >= item.approvalsRequired ? 'success' : 'warning'"
                        size="small"
                        variant="tonal"
                      >
                        <v-icon
                          v-if="item.approvalsGiven >= item.approvalsRequired"
                          icon="mdi-check"
                          size="x-small"
                          class="mr-1"
                        />
                        {{ item.approvalsGiven }}/{{ item.approvalsRequired }}
                      </v-chip>
                    </template>
                    <span v-else class="text-grey">—</span>
                  </td>

                  <!-- Last Updated -->
                  <td class="text-end">
                    <v-tooltip v-if="item.lastUpdated" location="top" :text="formatDateTime(item.lastUpdated)">
                      <template v-slot:activator="{ props }">
                        <span v-bind="props">{{ formatTimeAgo(item.lastUpdated) }}</span>
                      </template>
                    </v-tooltip>
                    <span v-else class="text-grey">—</span>
                  </td>
                </tr>
              </template>
            </tbody>
          </v-table>
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

const route = useRoute()
const router = useRouter()
const { currentUser, loadCurrentUser } = useCurrentUser()

const activities = ref<BranchActivity[]>([])
const initialLoading = ref(true)
const authenticated = computed(() => currentUser.value !== null)
const streaming = ref(false)
const errorMessage = ref('')
const now = ref(Date.now())

let eventSource: EventSource | null = null
let pollIntervalId: ReturnType<typeof setInterval> | null = null
let refreshIntervalId: ReturnType<typeof setInterval> | null = null
let timeIntervalId: ReturnType<typeof setInterval> | null = null
let lastUpdateTime: Date | null = null

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
.dashboard-table {
  border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  border-radius: 4px;
}

.branch-name-cell {
  vertical-align: top;
  border-right: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}
</style>
