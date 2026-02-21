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

        <div v-else-if="displayedMergeGroups.length === 0 && !streaming" class="text-center pa-8">
          <v-icon icon="mdi-source-branch" size="64" color="grey" class="mb-4" />
          <p class="text-h6 text-grey">No active branches in the last 14 days</p>
        </div>

        <div v-else>
          <div class="d-flex align-center mb-3" style="min-height: 24px;">
            <v-progress-circular
              v-if="streaming"
              indeterminate
              color="primary"
              size="18"
              width="2"
            />
          </div>

          <TransitionGroup name="dashboard-cards" tag="div" class="dashboard-card-list">
            <v-card
              v-for="group in displayedMergeGroups"
              :key="group.groupKey"
              class="merge-group-card"
              variant="outlined"
              :data-branch-name="group.branchName"
              @mouseenter="onCardMouseEnter"
              @mouseleave="onCardMouseLeave"
            >
              <v-card-item class="py-3">
                <template #prepend>
                  <v-icon icon="mdi-source-branch" size="small" class="mr-2" />
                </template>
                <v-card-title class="text-body-1 font-weight-medium merge-group-branch">
                  {{ group.branchName }}
                </v-card-title>
                <template #append>
                  <v-chip
                    class="group-status-chip"
                    :color="getGroupStatus(group).color"
                    size="small"
                    variant="tonal"
                  >
                    {{ getGroupStatus(group).label }}
                  </v-chip>
                </template>
              </v-card-item>

              <v-divider />

              <v-card-text class="py-2">
                <div
                  v-for="(item, idx) in group.items"
                  :key="`${group.groupKey}-${item.projectId}`"
                  class="repo-row py-2"
                  :data-repo-name="item.projectName"
                >
                  <v-row align="center" no-gutters>
                    <v-col cols="12" md="5" class="pr-md-3">
                      <div class="text-body-2 font-weight-medium">
                        {{ item.projectName }}
                      </div>
                    </v-col>

                    <v-col cols="12" sm="4" md="2" class="mt-2 mt-sm-0">
                      <v-progress-circular
                        v-if="item.hasMergeRequest === null"
                        indeterminate
                        color="grey"
                        size="16"
                        width="2"
                      />
                      <v-chip
                        v-else
                        class="repo-status-chip"
                        :color="getItemStatus(item).color"
                        size="small"
                        variant="tonal"
                      >
                        {{ getItemStatus(item).label }}
                      </v-chip>
                    </v-col>

                    <v-col cols="6" sm="4" md="2" class="mt-2 mt-sm-0">
                      <v-chip
                        class="repo-approvals-chip"
                        size="small"
                        variant="outlined"
                        :color="item.hasMergeRequest ? (getItemStatus(item).label === 'Ready' ? 'success' : 'info') : 'warning'"
                      >
                        {{ formatApprovals(item) }}
                      </v-chip>
                    </v-col>

                    <v-col cols="6" sm="4" md="3" class="text-end mt-2 mt-sm-0">
                      <v-tooltip v-if="item.lastUpdated" location="top" :text="formatDateTime(item.lastUpdated)">
                        <template v-slot:activator="{ props }">
                          <span v-bind="props" class="repo-last-updated">{{ formatTimeAgo(item.lastUpdated) }}</span>
                        </template>
                      </v-tooltip>
                      <span v-else class="text-grey repo-last-updated">—</span>
                    </v-col>
                  </v-row>

                  <v-divider v-if="idx < group.items.length - 1" class="mt-2" />
                </div>
              </v-card-text>
            </v-card>
          </TransitionGroup>
        </div>
      </v-col>
    </v-row>
  </v-container>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
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

const statusDefinitions = {
  ready: {
    label: 'Ready',
    color: 'success',
    priority: 1
  },
  open: {
    label: 'Open',
    color: 'info',
    priority: 2
  },
  waiting: {
    label: 'Waiting',
    color: 'warning',
    priority: 3
  }
} as const

type DashboardStatus = typeof statusDefinitions[keyof typeof statusDefinitions]

const route = useRoute()
const router = useRouter()
const { currentUser, loadCurrentUser } = useCurrentUser()

const activities = ref<BranchActivity[]>([])
const initialLoading = ref(true)
const authenticated = computed(() => currentUser.value !== null)
const streaming = ref(false)
const errorMessage = ref('')
const now = ref(Date.now())
const cardOrder = ref<string[]>([])
const reorderLocked = ref(false)

let eventSource: EventSource | null = null
let pollIntervalId: ReturnType<typeof setInterval> | null = null
let refreshIntervalId: ReturnType<typeof setInterval> | null = null
let timeIntervalId: ReturnType<typeof setInterval> | null = null
let reorderUnlockTimeoutId: ReturnType<typeof setTimeout> | null = null
let lastUpdateTime: Date | null = null

/**
 * Groups activities by mergeGroupId (from DB) when available,
 * falling back to branchName for items without a mergeGroupId.
 */
const rawMergeGroups = computed<MergeGroup[]>(() => {
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

const displayedMergeGroups = computed<MergeGroup[]>(() => {
  const mergeGroupByKey = new Map(rawMergeGroups.value.map(group => [group.groupKey, group]))
  const orderedGroups = cardOrder.value
    .map(groupKey => mergeGroupByKey.get(groupKey))
    .filter((group): group is MergeGroup => group !== undefined)

  if (orderedGroups.length === rawMergeGroups.value.length) {
    return orderedGroups
  }

  const orderedKeys = new Set(orderedGroups.map(group => group.groupKey))
  const missingGroups = rawMergeGroups.value.filter(group => !orderedKeys.has(group.groupKey))
  return [...orderedGroups, ...missingGroups]
})

watch(rawMergeGroups, groups => {
  syncCardOrder(groups)
}, { immediate: true })

function getItemStatus(item: BranchActivity): DashboardStatus {
  if (!item.hasMergeRequest) {
    return statusDefinitions.waiting
  }

  const approvalsRequired = item.approvalsRequired ?? 0
  const approvalsGiven = item.approvalsGiven ?? 0

  if (approvalsGiven >= approvalsRequired) {
    return statusDefinitions.ready
  }

  return statusDefinitions.open
}

function getGroupStatus(group: MergeGroup): DashboardStatus {
  let leastReady: DashboardStatus = statusDefinitions.ready

  for (const item of group.items) {
    const status = getItemStatus(item)
    if (status.priority > leastReady.priority) {
      leastReady = status
    }
  }

  return leastReady
}

function formatApprovals(item: BranchActivity): string {
  if (!item.hasMergeRequest) {
    return '—'
  }

  const approvalsRequired = item.approvalsRequired ?? 0
  const approvalsGiven = item.approvalsGiven ?? 0
  return `${approvalsGiven}/${approvalsRequired}`
}

function getGroupLatestUpdatedMs(group: MergeGroup): number {
  return group.items.reduce((latest, item) => {
    if (!item.lastUpdated) {
      return latest
    }

    const itemTime = Date.parse(item.lastUpdated)
    if (Number.isNaN(itemTime)) {
      return latest
    }

    return Math.max(latest, itemTime)
  }, 0)
}

function getSortedGroupKeys(groups: MergeGroup[]): string[] {
  return [...groups]
    .sort((a, b) => {
      const latestDiff = getGroupLatestUpdatedMs(b) - getGroupLatestUpdatedMs(a)
      if (latestDiff !== 0) {
        return latestDiff
      }

      return a.branchName.localeCompare(b.branchName)
    })
    .map(group => group.groupKey)
}

function syncCardOrder(groups: MergeGroup[]) {
  const incomingKeys = groups.map(group => group.groupKey)
  const incomingKeySet = new Set(incomingKeys)
  const persistedKeys = cardOrder.value.filter(groupKey => incomingKeySet.has(groupKey))
  const persistedKeySet = new Set(persistedKeys)
  const newKeys = incomingKeys.filter(groupKey => !persistedKeySet.has(groupKey))

  if (reorderLocked.value) {
    cardOrder.value = [...persistedKeys, ...newKeys]
    return
  }

  const sortedKeys = getSortedGroupKeys(groups)
  if (newKeys.length === 0) {
    cardOrder.value = sortedKeys
    return
  }

  const newKeySet = new Set(newKeys)
  const sortedNewKeys = sortedKeys.filter(groupKey => newKeySet.has(groupKey))
  const sortedExistingKeys = sortedKeys.filter(groupKey => !newKeySet.has(groupKey))
  cardOrder.value = [...sortedNewKeys, ...sortedExistingKeys]
}

function onCardMouseEnter() {
  if (reorderUnlockTimeoutId !== null) {
    clearTimeout(reorderUnlockTimeoutId)
    reorderUnlockTimeoutId = null
  }

  if (!reorderLocked.value) {
    console.info('[Mergician] Dashboard card reorder locked while cursor is over cards')
  }

  reorderLocked.value = true
}

function onCardMouseLeave() {
  if (reorderUnlockTimeoutId !== null) {
    clearTimeout(reorderUnlockTimeoutId)
  }

  reorderUnlockTimeoutId = setTimeout(() => {
    reorderLocked.value = false
    console.info('[Mergician] Dashboard card reorder unlocked after hover idle threshold')
    syncCardOrder(rawMergeGroups.value)
    reorderUnlockTimeoutId = null
  }, 2000)
}

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
  if (reorderUnlockTimeoutId) clearTimeout(reorderUnlockTimeoutId)
  eventSource?.close()
  eventSource = null
  stopPolling()
})
</script>

<style scoped>
.dashboard-card-list {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.merge-group-card {
  border-radius: 8px;
}

.merge-group-branch {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.repo-last-updated {
  white-space: nowrap;
}

.dashboard-cards-move {
  transition: transform 240ms cubic-bezier(0.2, 0.8, 0.2, 1);
}

.dashboard-cards-enter-active,
.dashboard-cards-leave-active {
  transition: opacity 180ms ease, transform 180ms ease;
}

.dashboard-cards-enter-from,
.dashboard-cards-leave-to {
  opacity: 0;
  transform: translateY(-8px);
}
</style>
