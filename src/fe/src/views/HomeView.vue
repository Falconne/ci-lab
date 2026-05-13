<template>
  <v-container fluid class="px-6">
    <v-row class="mt-4">
      <v-col cols="12">
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
        <WelcomePage v-if="!authenticated && !initialLoading" />

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

        <div v-else class="dashboard-content">
          <div class="dashboard-toolbar mb-6">
            <DashboardFilter
              v-model="filterText"
              :show-open-m-r-button="isMRUrlFilter && filteredMergeGroups.length === 0"
              class="dashboard-filter"
            />
            <div class="view-toggle">
              <v-tooltip text="Grid View" location="top">
                <template #activator="{ props: tipProps }">
                  <v-btn
                    v-bind="tipProps"
                    icon
                    size="small"
                    variant="text"
                    :color="viewMode === 'grid' ? 'primary' : undefined"
                    @click="viewMode = 'grid'"
                  >
                    <v-icon>mdi-table</v-icon>
                  </v-btn>
                </template>
              </v-tooltip>
              <v-tooltip text="Card View" location="top">
                <template #activator="{ props: tipProps }">
                  <v-btn
                    v-bind="tipProps"
                    icon
                    size="small"
                    variant="text"
                    :color="viewMode === 'card' ? 'primary' : undefined"
                    @click="viewMode = 'card'"
                  >
                    <v-icon>mdi-view-comfy</v-icon>
                  </v-btn>
                </template>
              </v-tooltip>
            </div>
          </div>

          <div v-if="filteredMergeGroups.length === 0 && !isMRUrlFilter" class="text-center pa-8">
            <v-icon icon="mdi-filter-off" size="48" color="grey" class="mb-3" />
            <p class="text-body-1 text-grey">No merge groups match &quot;{{ filterText }}&quot;</p>
          </div>

          <div v-else-if="filteredMergeGroups.length === 0 && isMRUrlFilter" class="text-center pa-8">
            <v-icon icon="mdi-magnify" size="48" color="grey" class="mb-3" />
            <p class="text-body-1 text-grey">No tracked merge group found for this MR URL</p>
          </div>

          <!-- Grid view -->
          <MergeGroupGrid
            v-else-if="viewMode === 'grid'"
            :sections="partitionedGroups"
            :now="now"
            @navigate="openMergeGroupDetails"
          />

          <!-- Card view -->
          <div v-else class="dashboard-cards">
            <div
              v-for="partition in partitionedGroups"
              :key="partition.title"
              class="partition-section"
            >
              <div class="partition-header">{{ partition.title }}</div>
              <TransitionGroup name="card-list" tag="div" class="card-container">
                <MergeGroupCard
                  v-for="group in partition.groups"
                  :key="group.id.toString()"
                  :group="group"
                  :now="now"
                  @navigate="openMergeGroupDetails"
                />
              </TransitionGroup>
            </div>
          </div>
        </div>
      </v-col>
    </v-row>
  </v-container>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { fetchBackend, isStartupRequiredError } from '@/composables/useBackendFetch'
import { useCurrentUser } from '@/composables/useCurrentUser'
import { usePolling } from '@/composables/usePolling'
import { useNow } from '@/composables/useNow'
import { useViewMode } from '@/composables/useViewMode'
import type { MergeGroup } from '@/types/mergeGroup'
import { handleTransientError, clearTransientError } from '@/utils/pollHelpers'
import WelcomePage from '@/components/WelcomePage.vue'
import DashboardFilter from '@/components/DashboardFilter.vue'
import MergeGroupCard from '@/components/MergeGroupCard.vue'
import MergeGroupGrid from '@/components/MergeGroupGrid.vue'

interface GroupPartition {
  title: string
  groups: MergeGroup[]
}

const route = useRoute()
const router = useRouter()
const { currentUser, loadCurrentUser } = useCurrentUser()
const { initialPhase, start: startPolling, stop: stopPolling } = usePolling(pollDashboard)

const mergeGroups = ref<MergeGroup[]>([])
const initialLoading = ref(true)
const authenticated = computed(() => currentUser.value !== null)
const errorMessage = ref('')
const filterText = ref('')
const now = useNow()
const viewMode = useViewMode()

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
 * Returns the base MR URL (up to and including the MR number) if the given text
 * looks like a GitLab merge request URL, otherwise null.
 */
function extractMRBaseUrl(text: string): string | null {
  const match = text.match(/^(https?:\/\/.+\/-\/merge_requests\/\d+)/)
  return match ? match[1] : null
}

/**
 * Whether the current filter text looks like a MR URL.
 */
const isMRUrlFilter = computed<boolean>(() => {
  return extractMRBaseUrl(filterText.value.trim()) !== null
})

/**
 * Filters sorted merge groups by branch name or MR URL using the user-entered filter text.
 * When a MR URL is entered, only groups containing a branch with that MR URL are shown.
 */
const filteredMergeGroups = computed<MergeGroup[]>(() => {
  const query = filterText.value.trim()
  if (!query) return sortedMergeGroups.value

  const mrBase = extractMRBaseUrl(query)
  if (mrBase) {
    return sortedMergeGroups.value.filter(group =>
      group.branches.some(b => b.mergeRequestUrl && b.mergeRequestUrl.startsWith(mrBase))
    )
  }

  const lower = query.toLowerCase()
  return sortedMergeGroups.value.filter(group =>
    group.name.toLowerCase().includes(lower) ||
    group.branches.some(b => b.branchName.toLowerCase().includes(lower))
  )
})

function getPartitionKey(group: MergeGroup, todayMidnight: number): string {
  const ts = groupLatestTimestamp(group)
  if (!ts) return 'today'

  const groupDate = new Date(ts)
  groupDate.setHours(0, 0, 0, 0)

  const daysAgo = Math.floor((todayMidnight - groupDate.getTime()) / 86400000)
  if (daysAgo === 0) return 'today'
  if (daysAgo === 1) return 'yesterday'
  if (daysAgo < 7) return 'last7days'
  return 'older'
}

/**
 * Splits the filtered merge groups into sections for display.
 * Auto-merge enabled groups appear first in their own section, followed by
 * time-based partitions. Empty sections are omitted.
 */
const partitionedGroups = computed<GroupPartition[]>(() => {
  const midnight = new Date()
  midnight.setHours(0, 0, 0, 0)
  const todayMidnight = midnight.getTime()

  const autoMergeSection: GroupPartition = { title: 'Auto Merge Enabled', groups: [] }
  const timeSections: GroupPartition[] = [
    { title: 'Today', groups: [] },
    { title: 'Yesterday', groups: [] },
    { title: 'Last 7 Days', groups: [] },
    { title: 'Older', groups: [] },
  ]
  const keyToSection: Record<string, GroupPartition> = {
    today: timeSections[0],
    yesterday: timeSections[1],
    last7days: timeSections[2],
    older: timeSections[3],
  }
  for (const group of filteredMergeGroups.value) {
    if (group.autoMerge) {
      autoMergeSection.groups.push(group)
    } else {
      keyToSection[getPartitionKey(group, todayMidnight)].groups.push(group)
    }
  }
  return [autoMergeSection, ...timeSections].filter(s => s.groups.length > 0)
})

/**
 * Polls the backend for a full dashboard snapshot and reconciles with the displayed list.
 * Groups are updated in place; removed groups are cleaned up.
 */
async function pollDashboard() {
  try {
    const response = await fetchBackend('/api/dashboard/refresh', {
      method: 'POST'
    })

    if (response.status === 401) {
      console.warn('Poll returned 401, stopping polling')
      stopPolling()
      return
    }

    if (handleTransientError(errorMessage, response.status)) return

    if (!response.ok) {
      console.error('Poll failed with status', response.status)
      return
    }

    clearTransientError(errorMessage)

    let data: MergeGroup[]
    try {
      data = await response.json()
    } catch (parseError) {
      console.error('[Mergician] Failed to parse dashboard poll response as JSON:', parseError)
      return
    }

    if (!Array.isArray(data)) {
      console.error('[Mergician] Unexpected dashboard response shape', data)
      return
    }

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
    if (isStartupRequiredError(err)) {
      console.info('[Mergician] Dashboard polling paused while startup is in progress')
      stopPolling()
      return
    }

    console.error('Dashboard poll failed:', err)
  }
}

function openMergeGroupDetails(group: MergeGroup) {
  router.push({
    name: 'merge-group-details',
    params: { mergeGroupId: group.id.toString() },
    query: { title: group.name }
  })
}

onMounted(async () => {
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
    if (isStartupRequiredError(err)) {
      console.info('[Mergician] Dashboard mount paused while startup is in progress')
      return
    }

    console.error('Failed to load dashboard:', err)
    initialLoading.value = false
  }
})
</script>

<style scoped>
/* ---- Dashboard content wrapper ---- */
.dashboard-content {
  width: 100%;
}

/* ---- Dashboard toolbar: filter + view toggle side by side ---- */
.dashboard-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
}

.dashboard-filter {
  flex: 1;
  max-width: 600px;
}

.view-toggle {
  display: flex;
  align-items: center;
  gap: 2px;
  flex-shrink: 0;
}

/* ---- Partition sections (time-based grouping) ---- */
.partition-section {
  margin-bottom: 28px;
}

.partition-section:last-child {
  margin-bottom: 0;
}

.partition-header {
  font-size: 0.75rem;
  font-weight: 600;
  color: rgba(var(--v-theme-on-surface), 0.6);
  text-transform: uppercase;
  letter-spacing: 0.08em;
  margin-bottom: 12px;
  padding-bottom: 6px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.08);
}

/* ---- Card container — auto-fill with fixed 600px cards ---- */
.dashboard-cards {
  position: relative;
}

.card-container {
  display: grid;
  grid-template-columns: repeat(auto-fill, 600px);
  gap: 20px;
  position: relative;
  align-items: start;
}

/* ---- TransitionGroup animations ---- */

/* Enter: fade + slide down */
:deep(.card-list-enter-active) {
  transition: all 0.4s cubic-bezier(0.25, 0.8, 0.25, 1);
}

/* Leave: fade + shrink */
:deep(.card-list-leave-active) {
  transition: all 0.3s cubic-bezier(0.25, 0.8, 0.25, 1);
  position: absolute;
  width: 600px;
}

:deep(.card-list-enter-from) {
  opacity: 0;
  transform: translateY(-16px);
}

:deep(.card-list-leave-to) {
  opacity: 0;
  transform: translateY(8px) scale(0.97);
}

/* FLIP move transition for reordering */
:deep(.card-list-move) {
  transition: transform 0.5s cubic-bezier(0.25, 0.8, 0.25, 1);
}
</style>
