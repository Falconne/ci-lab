<template>
  <v-container fluid>
    <v-row class="mt-4">
      <!-- Main content column: full width -->
      <v-col cols="12">
        <!-- Top bar: back link + status chip + track/untrack button -->
        <div class="page-top-bar mb-4">
          <v-btn variant="text" size="small" prepend-icon="mdi-arrow-left" :to="'/'">
            Back to Dashboard
          </v-btn>
          <div v-if="!initialLoading && !mergeGroupGone" class="page-top-bar-right">
            <v-tooltip
              v-if="isFullyLoaded && overallStatusReasons.length > 0"
              location="bottom"
            >
              <template #activator="{ props: tipProps }">
                <span v-bind="tipProps" class="card-status-badge" :class="overallStatusClass">
                  <span class="status-dot" />
                  {{ overallStatusLabel }}
                </span>
              </template>
              <span class="tooltip-multiline">{{ overallStatusReasonsText }}</span>
            </v-tooltip>
            <span v-else-if="isFullyLoaded" class="card-status-badge" :class="overallStatusClass">
              <span class="status-dot" />
              {{ overallStatusLabel }}
            </span>
            <span v-else class="skeleton-badge"><span class="skeleton-shimmer" /></span>
            <v-tooltip
              :text="isSubscribed ? 'Remove this merge group from my dashboard' : 'Track this merge group in my dashboard'"
              location="bottom"
            >
              <template #activator="{ props: tooltipProps }">
                <v-btn
                  v-if="subscriptionLoaded"
                  v-bind="tooltipProps"
                  variant="flat"
                  :color="isSubscribed ? 'surface-variant' : 'primary'"
                  size="small"
                  :prepend-icon="isSubscribed ? 'mdi-minus' : 'mdi-plus'"
                  :loading="subscriptionUpdating"
                  :class="['subscription-btn']"
                  @click="toggleSubscription"
                >
                  {{ isSubscribed ? 'Untrack' : 'Track' }}
                </v-btn>
              </template>
            </v-tooltip>
          </div>
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

        <v-alert
          v-if="mergeGroupGone"
          type="success"
          variant="tonal"
          class="mb-4"
        >
          Merge group has been merged or removed.
        </v-alert>

        <div v-if="initialLoading" class="text-center pa-8">
          <p class="text-body-1 text-grey">Loading merge group details...</p>
        </div>

        <template v-else-if="!mergeGroupGone">
          <!-- Summary: merge group name + auto merge controls -->
          <div class="merge-group-header mb-5">
            <div v-if="singleMrTitle" class="header-title-block">
              <span class="header-mr-title">{{ singleMrTitle }}</span>
              <span class="header-mr-subtitle">{{ mergeGroupName }}</span>
            </div>
            <span v-else class="header-title font-weight-bold">{{ mergeGroupName }}</span>

            <!-- Auto Merge / Auto Rebase toggles -->
            <div class="auto-merge-controls mt-3">
              <v-tooltip :text="autoMergeTooltip" location="bottom">
                <template #activator="{ props: tooltipProps }">
                  <span v-bind="tooltipProps" class="d-inline-flex">
                    <v-switch
                      v-model="autoMerge"
                      label="Auto Merge"
                      color="primary"
                      density="compact"
                      hide-details
                      :disabled="autoMergeDisabled"
                      @update:model-value="onAutoMergeToggle"
                    />
                  </span>
                </template>
              </v-tooltip>
              <v-tooltip text="Keep rebasing out of date branches" location="bottom">
                <template #activator="{ props: tooltipProps }">
                  <span v-bind="tooltipProps" class="d-inline-flex">
                    <v-switch
                      v-model="autoRebase"
                      label="Auto Rebase"
                      color="primary"
                      density="compact"
                      hide-details
                      :disabled="settingsUpdating || !autoMerge"
                      @update:model-value="onAutoRebaseToggle"
                    />
                  </span>
                </template>
              </v-tooltip>
            </div>

            <!-- Queue position indicator -->
            <div v-if="queueId != null && queuePosition != null" class="queue-position-info mt-3">
              <v-icon icon="mdi-playlist-play" size="16" class="mr-1" />
              Queue position:
              <router-link :to="{ name: 'queues', query: { queueId } }" class="queue-position-link">
                {{ queuePosition === 1 ? 'Next in queue' : `#${queuePosition} in queue` }}
              </router-link>
            </div>
          </div>

          <!-- Add Merge Request dialog -->
          <v-dialog v-model="showAddMergeRequestDialog" max-width="520" persistent>
            <v-card>
              <v-card-title>Add Merge Request</v-card-title>
              <v-card-text>
                <p class="text-body-2 mb-3">
                  Enter the URL of a GitLab merge request to add its branch to this merge group.
                </p>
                <v-text-field
                  v-model="addMergeRequestUrl"
                  label="Merge Request URL"
                  placeholder="https://gitlab.example.com/group/project/-/merge_requests/123"
                  variant="outlined"
                  density="compact"
                  :error-messages="addMergeRequestError"
                  :disabled="addMergeRequestLoading"
                  autofocus
                  @keyup.enter="submitAddMergeRequest"
                />
              </v-card-text>
              <v-card-actions>
                <v-spacer />
                <v-btn variant="text" :disabled="addMergeRequestLoading" @click="closeAddMergeRequestDialog">Cancel</v-btn>
                <v-btn color="primary" :loading="addMergeRequestLoading" :disabled="!addMergeRequestUrl.trim()" @click="submitAddMergeRequest">Add</v-btn>
              </v-card-actions>
            </v-card>
          </v-dialog>
          <v-alert
            v-if="autoMergeWarning"
            type="warning"
            variant="tonal"
            closable
            class="mb-4"
            @click:close="dismissWarning"
          >
            {{ autoMergeWarning }}
          </v-alert>

          <div v-if="activities.length === 0 && !initialPhase" class="text-center pa-8">
            <v-icon icon="mdi-source-branch" size="64" color="grey" class="mb-4" />
            <p class="text-h6 text-grey">No branches in this merge group</p>
          </div>

          <!-- Branch cards -->
          <div v-else class="repo-card-list">
            <div
              v-for="item in activities"
              :key="`${item.branchName}-${item.projectId}-details`"
              class="branch-card status-card mb-4"
            >
              <div class="card-accent" :class="mrStatusClass(item.mrStatus)" />
              <div class="card-body">
                <!-- Card header: title + status chip -->
                <div class="card-header">
                  <BranchCardTitle :item="item" />
                  <template v-if="item.mrStatus !== MRStatus.Loading">
                    <v-tooltip
                      v-if="item.mrStatus !== MRStatus.Ready && item.mrStatusReasons?.length"
                      location="top"
                    >
                      <template #activator="{ props: tipProps }">
                        <v-chip v-bind="tipProps" size="small" :color="mrStatusChipColor(item.mrStatus)" variant="tonal" class="flex-shrink-0">
                          {{ mrStatusLabel(item.mrStatus) }}
                        </v-chip>
                      </template>
                      <span class="tooltip-multiline">{{ item.mrStatusReasons!.join('\n') }}</span>
                    </v-tooltip>
                    <v-chip v-else size="small" :color="mrStatusChipColor(item.mrStatus)" variant="tonal" class="flex-shrink-0">
                      {{ mrStatusLabel(item.mrStatus) }}
                    </v-chip>
                  </template>
                  <span v-else class="skeleton-chip"><span class="skeleton-shimmer" /></span>
                </div>

                <!-- Detail rows -->
                <div class="detail-grid">
                  <!-- MR skeleton row: only shown while MR existence is still unknown -->
                  <div v-if="!item.mergeRequestTitle && item.hasMergeRequest !== false" class="detail-row">
                    <span class="detail-label">Merge Request</span>
                    <span class="detail-value">
                      <span class="skeleton-detail"><span class="skeleton-shimmer" /></span>
                    </span>
                  </div>

                  <div class="detail-row">
                    <span class="detail-label">Approvals</span>
                    <span class="detail-value">
                      <span v-if="item.mrStatus === MRStatus.Loading" class="skeleton-detail"><span class="skeleton-shimmer" /></span>
                      <template v-else>{{ itemApprovalsTextDetailed(item) }}</template>
                    </span>
                  </div>

                  <div v-if="item.lastCommitMessage" class="detail-row">
                    <span class="detail-label">Commit</span>
                    <span class="detail-value">{{ item.lastCommitMessage }}</span>
                  </div>
                </div>

                <!-- Create MR row: outside grid so no label appears to the left -->
                <template v-if="!item.mergeRequestTitle && item.hasMergeRequest === false">
                  <div class="create-mr-row">
                    <v-btn
                      v-if="item.projectUrl"
                      color="primary"
                      variant="flat"
                      size="small"
                      prepend-icon="mdi-plus"
                      :href="createMergeRequestUrl(item)"
                      target="_blank"
                      rel="noopener noreferrer"
                      class="text-none"
                    >
                      Create Merge Request
                    </v-btn>
                    <span v-else class="text-medium-emphasis">No Merge Request</span>
                  </div>
                </template>

                <div class="build-jobs-section">
                  <span class="build-jobs-subtitle">Build Jobs:</span>
                  <div v-if="item.buildJobs && item.buildJobs.length > 0" class="jobs-list">
                    <v-tooltip
                      v-for="job in item.buildJobs"
                      :key="`${item.projectId}-${job.name}-${job.status}`"
                      location="top"
                      :text="jobStatusLabel(job.status)"
                    >
                      <template #activator="{ props: tipProps }">
                        <v-chip
                          v-bind="tipProps"
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
                      </template>
                    </v-tooltip>
                  </div>
                  <span v-else-if="item.mrStatus !== MRStatus.Loading" class="text-medium-emphasis">No jobs on latest pipeline</span>
                  <span v-else class="skeleton-detail"><span class="skeleton-shimmer" /></span>
                </div>
              </div>
            </div>
          </div>

          <!-- Add Merge Request button -->
          <div class="mt-4">
            <v-tooltip text="Add a merge request from a different branch to this group" location="top">
              <template #activator="{ props: tooltipProps }">
                <v-btn
                  v-bind="tooltipProps"
                  color="primary"
                  variant="tonal"
                  size="small"
                  prepend-icon="mdi-plus"
                  class="text-none"
                  @click="showAddMergeRequestDialog = true"
                >
                  Add Another MR to Group...
                </v-btn>
              </template>
            </v-tooltip>
          </div>
        </template>
      </v-col>
    </v-row>
  </v-container>
</template>

<script setup lang="ts">
import { onMounted, ref, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { fetchBackend, isStartupRequiredError } from '@/composables/useBackendFetch'
import { usePolling } from '@/composables/usePolling'
import type { BranchWithActivity, MergeGroup } from '@/types/mergeGroup'
import {
  mrStatusLabel, mrStatusClass, mrStatusChipColor,
  itemApprovalsTextDetailed, jobStatusIcon, jobStatusColor, jobStatusLabel,
  groupStatusLabel, groupStatusClass, getGroupStatusReasons,
  MRStatus,
} from '@/utils/statusHelpers'
import { handleTransientError, clearTransientError } from '@/utils/pollHelpers'
import { extractBackendError } from '@/utils/errorHelpers'
import BranchCardTitle from '@/components/BranchCardTitle.vue'

const route = useRoute()
const router = useRouter()
const { initialPhase, start: startPolling, stop: stopPolling } = usePolling(pollMergeGroup)

const activities = ref<BranchWithActivity[]>([])
const mergeGroupName = ref('')
const initialLoading = ref(true)
const errorMessage = ref('')
const mergeGroupGone = ref(false)
const autoMerge = ref(false)
const autoRebase = ref(false)
const autoMergeWarning = ref<string | null>(null)
const settingsUpdating = ref(false)
let settingsUpdateSeq = 0
const queueId = ref<number | null>(null)
const queuePosition = ref<number | null>(null)
const isSubscribed = ref(false)
const subscriptionLoaded = ref(false)
const subscriptionUpdating = ref(false)
const showAddMergeRequestDialog = ref(false)
const addMergeRequestUrl = ref('')
const addMergeRequestError = ref('')
const addMergeRequestLoading = ref(false)

const isFullyLoaded = computed<boolean>(() => {
  return activities.value.length > 0 && activities.value.every(b => b.mrStatus !== MRStatus.Loading)
})

const singleMrTitle = computed<string | null>(() => {
  if (activities.value.length !== 1) return null
  const branch = activities.value[0]
  if (branch.mrStatus === MRStatus.Loading) return null
  return branch.mergeRequestTitle ?? null
})

const allBranchesHaveMr = computed<boolean>(() => {
  const loaded = activities.value.filter(b => b.mrStatus !== MRStatus.Loading)
  return loaded.length > 0 && loaded.every(b => b.hasMergeRequest === true)
})

// --- Merge permissions ---

type MergePermissionState = 'checking' | 'can-merge' | 'blocked' | 'check-failed'
const mergePermissionState = ref<MergePermissionState>('checking')
const permissionBlockedProjects = ref<string[]>([])

const autoMergeDisabled = computed<boolean>(() => {
  if (settingsUpdating.value) return true
  // Only block turning ON; always allow turning OFF to avoid trapping an enabled toggle
  if (autoMerge.value) return false
  if (!allBranchesHaveMr.value) return true
  return mergePermissionState.value === 'checking' || mergePermissionState.value === 'blocked'
})

const autoMergeTooltip = computed<string>(() => {
  if (!autoMerge.value) {
    if (!allBranchesHaveMr.value) return 'All branches must have a Merge Request to enable Auto Merge'
    if (mergePermissionState.value === 'checking') return 'Checking permissions...'
    if (mergePermissionState.value === 'blocked') {
      return `Cannot enable Auto Merge: missing merge permission in: ${permissionBlockedProjects.value.join(', ')}`
    }
  }
  return 'Merge all branches together, only when all are ready'
})

async function checkMergePermissions() {
  if (!mergeGroupId.value) return

  mergePermissionState.value = 'checking'
  try {
    const response = await fetchBackend(`/api/merge-groups/${mergeGroupId.value}/merge-permissions`)
    if (!response.ok) {
      console.error('Failed to check merge permissions, status', response.status)
      mergePermissionState.value = 'check-failed'
      return
    }
    const data = await response.json() as { canMerge: boolean; checkFailed: boolean; blockedProjects: string[] }
    if (data.checkFailed) {
      mergePermissionState.value = 'check-failed'
    } else if (data.canMerge) {
      mergePermissionState.value = 'can-merge'
      permissionBlockedProjects.value = []
    } else {
      mergePermissionState.value = 'blocked'
      permissionBlockedProjects.value = data.blockedProjects ?? []
    }
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('Failed to check merge permissions:', err)
    mergePermissionState.value = 'check-failed'
  }
}


const overallStatusLabel = computed<string>(() =>
  groupStatusLabel(activities.value)
)

const overallStatusClass = computed<string>(() =>
  groupStatusClass(activities.value)
)

const overallStatusReasons = computed<string[]>(() =>
  getGroupStatusReasons(activities.value)
)

const overallStatusReasonsText = computed<string>(() =>
  overallStatusReasons.value.join('\n')
)

function createMergeRequestUrl(item: BranchWithActivity): string {
  if (!item.projectUrl) return ''
  return `${item.projectUrl}/-/merge_requests/new?merge_request[source_branch]=${encodeURIComponent(item.branchName)}`
}

// --- Subscription management ---

async function loadSubscription() {
  if (!mergeGroupId.value) return

  try {
    const response = await fetchBackend(`/api/merge-groups/${mergeGroupId.value}/subscription`)
    if (response.ok) {
      const data = await response.json() as { isSubscribed?: boolean }
      isSubscribed.value = data.isSubscribed === true
      subscriptionLoaded.value = true
    }
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('Failed to load subscription state:', err)
  }
}

async function toggleSubscription() {
  if (!mergeGroupId.value) return

  subscriptionUpdating.value = true
  const method = isSubscribed.value ? 'DELETE' : 'PUT'

  try {
    const response = await fetchBackend(`/api/merge-groups/${mergeGroupId.value}/subscription`, { method })
    if (response.ok) {
      const data = await response.json() as { isSubscribed?: boolean }
      isSubscribed.value = data.isSubscribed === true
    } else {
      errorMessage.value = await extractBackendError(response, 'Failed to update subscription')
    }
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('Failed to update subscription:', err)
    errorMessage.value = 'Failed to update subscription.'
  } finally {
    subscriptionUpdating.value = false
  }
}

// --- Add Merge Request dialog ---

function closeAddMergeRequestDialog() {
  showAddMergeRequestDialog.value = false
  addMergeRequestUrl.value = ''
  addMergeRequestError.value = ''
}

async function submitAddMergeRequest() {
  if (!mergeGroupId.value || !addMergeRequestUrl.value.trim()) return

  addMergeRequestLoading.value = true
  addMergeRequestError.value = ''

  try {
    const response = await fetchBackend(`/api/merge-groups/${mergeGroupId.value}/add-by-merge-request`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mergeRequestUrl: addMergeRequestUrl.value.trim() })
    })

    if (response.ok) {
      closeAddMergeRequestDialog()
      await pollMergeGroup()
      checkMergePermissions().catch(err => {
        console.error('[Mergician] Failed to re-check merge permissions after adding MR:', err)
      })
    } else {
      addMergeRequestError.value = await extractBackendError(response, 'Failed to add merge request')
    }
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('Failed to add merge request:', err)
    addMergeRequestError.value = 'Failed to add merge request. Please try again.'
  } finally {
    addMergeRequestLoading.value = false
  }
}

// --- Auto merge settings ---

async function updateSettings(newAutoMerge: boolean, newAutoRebase: boolean) {
  if (!mergeGroupId.value) return

  const prevAutoMerge = autoMerge.value
  const prevAutoRebase = autoRebase.value

  settingsUpdating.value = true
  const seq = ++settingsUpdateSeq
  try {
    const response = await fetchBackend(`/api/merge-groups/${mergeGroupId.value}/settings`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ autoMerge: newAutoMerge, autoRebase: newAutoRebase })
    })

    if (!response.ok) {
      errorMessage.value = await extractBackendError(response, 'Failed to update auto merge settings')
      autoMerge.value = prevAutoMerge
      autoRebase.value = prevAutoRebase
      return
    }

    const data: MergeGroup = await response.json()
    autoMerge.value = data.autoMerge
    autoRebase.value = data.autoRebase
    autoMergeWarning.value = data.autoMergeWarning
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('Failed to update settings:', err)
    errorMessage.value = 'Failed to update auto merge settings.'
    autoMerge.value = prevAutoMerge
    autoRebase.value = prevAutoRebase
  } finally {
    settingsUpdating.value = false
  }
}

function onAutoMergeToggle(value: boolean | null) {
  const enabled = value === true
  if (enabled) {
    // Enabling auto merge also enables auto rebase
    updateSettings(true, true)
  } else {
    // Disabling auto merge also disables auto rebase (rebase requires merge)
    updateSettings(false, false)
  }
}

function onAutoRebaseToggle(value: boolean | null) {
  const enabled = value === true
  if (!enabled && autoMerge.value) {
    // Cannot disable auto rebase while auto merge is on; auto merge requires auto rebase
    // Disable auto merge too
    updateSettings(false, false)
  } else {
    updateSettings(autoMerge.value, enabled)
  }
}

async function dismissWarning() {
  if (!mergeGroupId.value) return

  autoMergeWarning.value = null

  try {
    await fetchBackend(`/api/merge-groups/${mergeGroupId.value}/settings/clear-warning`, {
      method: 'POST'
    })
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('Failed to clear warning:', err)
  }
}

const mergeGroupId = computed<string>(() => {
  const id = route.params.mergeGroupId
  return Array.isArray(id) ? id[0] : (id ?? '')
})

/**
 * Polls the backend for a full merge group snapshot and reconciles with the displayed list.
 * Existing branches are updated, new ones added, and removed branches are cleaned up.
 */
async function pollMergeGroup() {
  if (!mergeGroupId.value) return

  const seq = settingsUpdateSeq
  try {
    const response = await fetchBackend(`/api/merge-groups/${mergeGroupId.value}/refresh`, {
      method: 'POST'
    })

    if (response.status === 401) {
      console.warn('Poll returned 401, stopping polling')
      stopPolling()
      return
    }

    if (response.status === 404) {
      mergeGroupGone.value = true
      initialLoading.value = false
      stopPolling()
      return
    }

    if (handleTransientError(errorMessage, response.status)) return

    if (!response.ok) {
      console.error('Poll failed with status', response.status)
      return
    }

    clearTransientError(errorMessage)

    let data: MergeGroup
    try {
      data = await response.json()
    } catch (parseError) {
      console.error('[Mergician] Failed to parse merge group poll response as JSON:', parseError)
      return
    }

    // Update merge group name if changed
    if (data.name && data.name !== mergeGroupName.value) {
      mergeGroupName.value = data.name
      updateRouteTitle(data.name)
    }

    // Sync auto merge settings from backend (only if not currently updating)
    if (!settingsUpdating.value && settingsUpdateSeq === seq) {
      autoMerge.value = data.autoMerge
      autoRebase.value = data.autoRebase
      autoMergeWarning.value = data.autoMergeWarning
    }

    queueId.value = data.queueId ?? null
    queuePosition.value = data.queuePosition ?? null

    // Replace activities with the full response (poll always returns the complete list)
    activities.value = data.branches
  } catch (err) {
    if (isStartupRequiredError(err)) {
      console.info('[Mergician] Merge group polling paused while startup is in progress')
      stopPolling()
      return
    }

    console.error('Merge group poll failed:', err)
  }
}

function updateRouteTitle(name: string) {
  if (route.query.title !== name) {
    router.replace({
      name: 'merge-group-details',
      params: { mergeGroupId: mergeGroupId.value },
      query: { title: name }
    })
  }
}

onMounted(async () => {
  if (!mergeGroupId.value) {
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
  loadSubscription().catch(err => {
    console.error('[Mergician] Failed to load subscription:', err)
  })
  checkMergePermissions().catch(err => {
    console.error('[Mergician] Failed to check merge permissions:', err)
  })
})
</script>

<style scoped>
@import '@/assets/status-badges.css';

/* ---- Page top bar: back link + right-aligned status + track button ---- */
.page-top-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.page-top-bar-right {
  display: flex;
  align-items: center;
  gap: 10px;
}

/* ---- Merge group header ---- */
.merge-group-header {
  padding: 2px 0 6px;
}

.header-title {
  font-size: 1rem;
  color: rgb(var(--v-theme-on-surface));
}

.header-title-block {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  min-width: 0;
}

.header-mr-title {
  font-size: 1rem;
  font-weight: 700;
}

.header-mr-subtitle {
  font-size: 0.8rem;
  color: rgba(var(--v-theme-on-surface), 0.55);
  white-space: nowrap;
}

.auto-merge-controls {
  display: flex;
  gap: 24px;
  flex-wrap: wrap;
}

.queue-position-info {
  display: flex;
  align-items: center;
  font-size: 0.85rem;
  color: rgba(var(--v-theme-on-surface), 0.7);
}

.queue-position-link {
  font-weight: 600;
  color: rgb(var(--v-theme-primary));
  text-decoration: none;
  margin-left: 4px;
}

.queue-position-link:hover {
  text-decoration: underline;
}

.subscription-btn {
  text-transform: none;
}

/* ---- Branch card — uses .status-card base, no extra modifiers needed ---- */

/* ---- Card header ---- */
.card-header {
  display: flex;
  align-items: center;
  margin: -14px -18px 12px -18px;
  padding: 10px 18px;
  background: #E8EEF8;
  flex-wrap: wrap;
  gap: 8px;
}

.branch-card-title {
  display: flex;
  align-items: center;
  min-width: 0;
  gap: 6px;
}

.copy-branch-btn {
  flex-shrink: 0;
  opacity: 0.5;
  transition: opacity 0.15s;
}

.branch-card:hover .copy-branch-btn,
.copy-branch-btn:hover {
  opacity: 1;
}

.branch-title-link,
.branch-title-text {
  font-weight: 600;
  font-size: 0.95rem;
  color: rgb(var(--v-theme-on-surface));
}

.branch-title-link {
  text-decoration: underline;
  text-underline-offset: 2px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.branch-card-title--with-mr {
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
}

.mr-title-row {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
}

.mr-external-link-btn {
  display: inline-flex;
  align-items: center;
  color: inherit;
  text-decoration: none;
  flex-shrink: 0;
  opacity: 0.6;
  transition: opacity 0.15s;
}

.mr-external-link-btn:hover {
  opacity: 1;
}

.mr-external-link-icon {
  flex-shrink: 0;
}

.mr-title-link,
.mr-title-text {
  font-weight: 600;
  font-size: 0.95rem;
  color: rgb(var(--v-theme-on-surface));
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.mr-title-link {
  text-decoration: underline;
  text-underline-offset: 2px;
  min-width: 0;
}

.branch-subtitle-row {
  display: flex;
  align-items: center;
  gap: 2px;
}

.branch-subtitle-link,
.branch-subtitle-text {
  font-size: 0.78rem;
  font-weight: 400;
  color: rgba(var(--v-theme-on-surface), 0.55);
}

.branch-subtitle-link {
  text-decoration: underline;
  text-underline-offset: 2px;
}

/* ---- Detail rows ---- */
.detail-grid {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 6px 12px;
  font-size: 0.85rem;
  align-items: baseline;
  word-break: break-word;
  margin-bottom: 8px;
}

.detail-grid .detail-row {
  display: contents;
}

.detail-label {
  font-weight: 600;
  color: rgb(var(--v-theme-on-surface));
  white-space: nowrap;
}

.detail-value {
  color: rgb(var(--v-theme-on-surface));
  min-width: 0;
}

.detail-link,
.job-link {
  color: inherit;
  text-decoration: underline;
  text-underline-offset: 2px;
}

.tooltip-multiline {
  white-space: pre-line;
}

.create-mr-row {
  margin-top: 8px;
}

/* ---- Jobs list ---- */
.build-jobs-section {
  margin-top: 12px;
  padding-top: 10px;
  border-top: 1px solid rgba(var(--v-theme-on-surface), 0.1);
}

.build-jobs-subtitle {
  display: block;
  font-size: 0.72rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.07em;
  color: rgba(var(--v-theme-on-surface), 0.5);
  margin-bottom: 8px;
}

.jobs-list {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 4px;
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
