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

        <div v-else>
          <!-- Text filter for searching merge groups by branch name or MR URL -->
          <div class="filter-row mb-4">
            <v-text-field
              v-model="filterText"
              prepend-inner-icon="mdi-magnify"
              label="Filter by branch name or MR URL"
              variant="outlined"
              density="compact"
              clearable
              hide-details
            >
              <template #append-inner>
                <v-tooltip text="Paste from clipboard" location="top">
                  <template #activator="{ props }">
                    <v-btn
                      v-bind="props"
                      icon
                      size="x-small"
                      variant="text"
                      color="grey"
                      class="paste-btn"
                      aria-label="Paste from clipboard"
                      @click="pasteFromClipboard"
                    >
                      <v-icon size="18">mdi-content-paste</v-icon>
                    </v-btn>
                  </template>
                </v-tooltip>
              </template>
            </v-text-field>

            <v-btn
              v-if="isMrUrlFilter && filteredMergeGroups.length === 0"
              color="primary"
              variant="flat"
              size="small"
              prepend-icon="mdi-open-in-app"
              class="ml-2 text-none open-mr-btn"
              :loading="openMrLoading"
              :disabled="openMrLoading"
              @click="openMrAsGroup"
            >
              Open MR as Merge Group
            </v-btn>
          </div>

          <v-alert
            v-if="openMrError"
            type="error"
            variant="tonal"
            closable
            class="mb-4"
            @click:close="openMrError = ''"
          >
            {{ openMrError }}
          </v-alert>

          <div v-if="filteredMergeGroups.length === 0 && !isMrUrlFilter" class="text-center pa-8">
            <v-icon icon="mdi-filter-off" size="48" color="grey" class="mb-3" />
            <p class="text-body-1 text-grey">No merge groups match &quot;{{ filterText }}&quot;</p>
          </div>

          <div v-else-if="filteredMergeGroups.length === 0 && isMrUrlFilter" class="text-center pa-8">
            <v-icon icon="mdi-magnify" size="48" color="grey" class="mb-3" />
            <p class="text-body-1 text-grey">No tracked merge group found for this MR URL</p>
          </div>

          <div v-else class="dashboard-cards">
            <div
              v-for="partition in partitionedGroups"
              :key="partition.title"
              class="partition-section"
            >
              <div class="partition-header">{{ partition.title }}</div>
              <TransitionGroup name="card-list" tag="div" class="card-container">
                <div
                  v-for="group in partition.groups"
                  :key="group.id.toString()"
                  class="merge-group-card"
                  :data-merge-group-id="group.id"
                  role="link"
                  tabindex="0"
                  :aria-label="`Merge group ${group.name}`"
                  @click="openMergeGroupDetails(group)"
                  @keydown.enter="openMergeGroupDetails(group)"
                >
                  <div class="card-accent" :class="groupStatusClass(group)" />
                  <div class="card-body">
                    <div class="card-header">
                      <div class="branch-info">
                        <v-icon icon="mdi-source-branch" size="small" class="branch-icon" />
                        <span class="branch-name">{{ group.name }}</span>
                      </div>
                      <div class="card-header-right">
                        <span v-if="group.autoMerge" class="auto-merge-badge">
                          <v-icon icon="mdi-merge" size="x-small" />
                          Auto Merge
                        </span>
                        <span v-if="isGroupFullyLoaded(group)" class="card-status-badge" :class="groupStatusClass(group)">
                          <span class="status-dot" />
                          {{ groupStatusLabel(group) }}
                        </span>
                        <span v-else class="skeleton-badge"><span class="skeleton-shimmer" /></span>
                        <v-btn
                          icon
                          size="x-small"
                          variant="text"
                          color="grey"
                          :href="mergeGroupHref(group)"
                          target="_blank"
                          rel="noopener noreferrer"
                          title="Open in new tab"
                          aria-label="Open in new tab"
                          @click.stop
                        >
                          <v-icon icon="mdi-open-in-new" size="24" />
                        </v-btn>
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
                              <span v-bind="props">{{ formatTimeAgo(item.lastUpdated, now) }}</span>
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
import type { BranchWithActivity, MergeGroup } from '@/types/mergeGroup'
import {
  isBranchLoading, isGroupFullyLoaded,
  getGroupStatus, groupStatusLabel, groupStatusClass,
  itemApprovalsText,
} from '@/utils/statusHelpers'
import { handleTransientError, clearTransientError } from '@/utils/pollHelpers'
import { formatDateTime, formatTimeAgo } from '@/utils/dateFormatting'

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
const openMrLoading = ref(false)
const openMrError = ref('')
const now = useNow()

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

const MAX_TITLE_LENGTH = 222

function truncateTitle(title: string): string {
  if (title.length <= MAX_TITLE_LENGTH) return title
  return title.slice(0, MAX_TITLE_LENGTH) + '...'
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
function extractMrBaseUrl(text: string): string | null {
  const match = text.match(/^(https?:\/\/.+\/-\/merge_requests\/\d+)/)
  return match ? match[1] : null
}

/**
 * Whether the current filter text looks like a MR URL.
 */
const isMrUrlFilter = computed<boolean>(() => {
  return extractMrBaseUrl(filterText.value.trim()) !== null
})

/**
 * Filters sorted merge groups by branch name or MR URL using the user-entered filter text.
 * When a MR URL is entered, only groups containing a branch with that MR URL are shown.
 */
const filteredMergeGroups = computed<MergeGroup[]>(() => {
  const query = filterText.value.trim()
  if (!query) return sortedMergeGroups.value

  const mrBase = extractMrBaseUrl(query)
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
 * Splits the filtered merge groups into time-based partitions for display.
 * Empty partitions are omitted.
 */
const partitionedGroups = computed<GroupPartition[]>(() => {
  const midnight = new Date()
  midnight.setHours(0, 0, 0, 0)
  const todayMidnight = midnight.getTime()

  const sections: GroupPartition[] = [
    { title: 'Today', groups: [] },
    { title: 'Yesterday', groups: [] },
    { title: 'Last 7 Days', groups: [] },
    { title: 'Older', groups: [] },
  ]
  const keyToSection: Record<string, GroupPartition> = {
    today: sections[0],
    yesterday: sections[1],
    last7days: sections[2],
    older: sections[3],
  }
  for (const group of filteredMergeGroups.value) {
    keyToSection[getPartitionKey(group, todayMidnight)].groups.push(group)
  }
  return sections.filter(s => s.groups.length > 0)
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

function mergeGroupHref(group: MergeGroup): string {
  const resolved = router.resolve({
    name: 'merge-group-details',
    params: { mergeGroupId: group.id.toString() },
    query: { title: group.name }
  })
  return resolved.href
}

// --- Filter box helpers ---

async function pasteFromClipboard() {
  try {
    const text = await navigator.clipboard.readText()
    filterText.value = text
  } catch (err) {
    console.warn('[Mergician] Failed to read from clipboard:', err)
    openMrError.value = 'Could not read from clipboard. Please paste manually.'
  }
}

async function openMrAsGroup() {
  const url = filterText.value.trim()
  if (!url) return

  openMrLoading.value = true
  openMrError.value = ''

  try {
    const response = await fetchBackend('/api/merge-groups/find-by-merge-request', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mergeRequestUrl: url })
    })

    if (response.ok) {
      const data = await response.json() as { mergeGroupId?: number }
      if (!data.mergeGroupId) {
        openMrError.value = 'Unexpected response: missing mergeGroupId'
        return
      }
      filterText.value = ''
      router.push(`/merge-group/${data.mergeGroupId}`)
    } else {
      const data = await response.json().catch(() => null)
      openMrError.value = data?.error || `Request failed with status ${response.status}`
    }
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('[Mergician] Open MR as merge group failed:', err)
    openMrError.value = 'Failed to find merge request. Please try again.'
  } finally {
    openMrLoading.value = false
  }
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
@import '@/assets/status-badges.css';

/* ---- Filter row ---- */
.filter-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.filter-row .v-text-field {
  flex: 1;
}

.open-mr-btn {
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
  color: #9e9e9e;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  margin-bottom: 12px;
  padding-bottom: 6px;
  border-bottom: 1px solid #f0f0f0;
}

/* ---- Card container — multi-column grid ---- */
.dashboard-cards {
  position: relative;
}

.card-container {
  display: grid;
  grid-template-columns: 1fr;
  gap: 20px;
  position: relative;
}

@media (min-width: 600px) {
  .card-container {
    grid-template-columns: repeat(2, 1fr);
  }
}

@media (min-width: 900px) {
  .card-container {
    grid-template-columns: repeat(3, 1fr);
  }
}

@media (min-width: 1280px) {
  .card-container {
    grid-template-columns: repeat(4, 1fr);
  }
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

.auto-merge-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border-radius: 12px;
  font-size: 0.75rem;
  font-weight: 500;
  background: #e8eaf6;
  color: #3949ab;
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
