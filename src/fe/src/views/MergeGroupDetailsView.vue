<template>
  <v-container>
    <v-row class="mt-4">
      <!-- Back button: shown as left column on wide screens -->
      <v-col cols="auto" class="d-none d-lg-flex align-start" style="padding-top: 6px;">
        <v-btn variant="text" prepend-icon="mdi-arrow-left" :to="'/'">
          Back to Dashboard
        </v-btn>
      </v-col>

      <!-- Main content column -->
      <v-col cols="12" md="10" lg="9" class="mx-auto mx-lg-0">
        <!-- Back button: shown above content on narrow screens -->
        <div class="d-flex align-center mb-4 d-lg-none">
          <v-btn variant="text" prepend-icon="mdi-arrow-left" :to="'/'">
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
              <v-tooltip
                :text="isSubscribed ? 'Remove this merge group from my dashboard' : 'Track this merge group in my dashboard'"
                location="bottom"
              >
                <template #activator="{ props: tooltipProps }">
                  <v-btn
                    v-if="subscriptionLoaded"
                    v-bind="tooltipProps"
                    variant="flat"
                    :color="isSubscribed ? 'brown' : 'primary'"
                    size="small"
                    :prepend-icon="isSubscribed ? 'mdi-minus' : 'mdi-plus'"
                    :loading="subscriptionUpdating"
                    :class="['subscription-btn', isSubscribed ? 'subscription-brown' : '']"
                    @click="toggleSubscription"
                  >
                    {{ isSubscribed ? 'Untrack' : 'Track' }}
                  </v-btn>
                </template>
              </v-tooltip>
            </div>

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
                    <v-tooltip text="Copy branch name" location="top">
                      <template #activator="{ props: tooltipProps }">
                        <v-btn
                          v-bind="tooltipProps"
                          icon
                          size="x-small"
                          variant="text"
                          color="grey"
                          class="copy-branch-btn"
                          aria-label="Copy branch name"
                          @click.stop="copyBranchName(item.branchName)"
                        >
                          <v-icon size="16">mdi-content-copy</v-icon>
                        </v-btn>
                      </template>
                    </v-tooltip>
                  </div>
                  <template v-if="item.mrStatus !== 0">
                    <v-tooltip
                      v-if="item.mrStatus !== 3 && item.mrStatusReasons?.length"
                      location="top"
                      :text="item.mrStatusReasons!.join(', ')"
                    >
                      <template #activator="{ props: tipProps }">
                        <v-chip v-bind="tipProps" size="small" :color="mrStatusChipColor(item.mrStatus)" variant="tonal" class="flex-shrink-0">
                          {{ mrStatusLabel(item.mrStatus) }}
                        </v-chip>
                      </template>
                    </v-tooltip>
                    <v-chip v-else size="small" :color="mrStatusChipColor(item.mrStatus)" variant="tonal" class="flex-shrink-0">
                      {{ mrStatusLabel(item.mrStatus) }}
                    </v-chip>
                  </template>
                  <span v-else class="skeleton-chip"><span class="skeleton-shimmer" /></span>
                </div>

                <!-- Detail rows -->
                <div class="detail-row">
                  <span class="detail-label">Approvals:</span>
                  <span class="detail-value">
                    <span v-if="item.mrStatus === 0" class="skeleton-detail"><span class="skeleton-shimmer" /></span>
                    <template v-else>{{ itemApprovalsTextDetailed(item) }}</template>
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
                        :href="createMergeRequestUrl(item)"
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
                  <span class="detail-label">Build Jobs:</span>
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
                    <span v-else-if="item.mrStatus !== 0" class="text-medium-emphasis">No jobs on latest pipeline</span>
                    <span v-else class="skeleton-detail"><span class="skeleton-shimmer" /></span>
                  </span>
                </div>
              </div>
            </div>
          </div>

          <!-- Add Merge Request button -->
          <div class="mt-4">
            <v-tooltip text="Manually add a merge request to this group" location="top">
              <template #activator="{ props: tooltipProps }">
                <v-btn
                  v-bind="tooltipProps"
                  color="primary"
                  variant="flat"
                  size="small"
                  prepend-icon="mdi-plus"
                  class="text-none"
                  @click="showAddMergeRequestDialog = true"
                >
                  Add Existing Merge Request...
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
  itemApprovalsTextDetailed, jobStatusIcon, jobStatusColor,
  groupStatusLabel, groupStatusClass,
} from '@/utils/statusHelpers'
import { handleTransientError, clearTransientError } from '@/utils/pollHelpers'

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
const isSubscribed = ref(false)
const subscriptionLoaded = ref(false)
const subscriptionUpdating = ref(false)
const showAddMergeRequestDialog = ref(false)
const addMergeRequestUrl = ref('')
const addMergeRequestError = ref('')
const addMergeRequestLoading = ref(false)

const isFullyLoaded = computed<boolean>(() => {
  return activities.value.length > 0 && activities.value.every(b => b.mrStatus !== 0)
})

// --- Merge permissions ---

type MergePermissionState = 'checking' | 'can-merge' | 'blocked' | 'check-failed'
const mergePermissionState = ref<MergePermissionState>('checking')
const permissionBlockedProjects = ref<string[]>([])

const autoMergeDisabled = computed<boolean>(() => {
  if (settingsUpdating.value) return true
  // Only block turning ON; always allow turning OFF to avoid trapping an enabled toggle
  if (autoMerge.value) return false
  return mergePermissionState.value === 'checking' || mergePermissionState.value === 'blocked'
})

const autoMergeTooltip = computed<string>(() => {
  if (!autoMerge.value) {
    if (mergePermissionState.value === 'checking') return 'Checking permissions...'
    if (mergePermissionState.value === 'blocked') {
      return `Cannot enable Auto Merge: missing merge permission in: ${permissionBlockedProjects.value.join(', ')}`
    }
  }
  return 'Merge all branches together, only when all are ready'
})

async function checkMergePermissions() {
  const mergeGroupId = getMergeGroupId()
  if (!mergeGroupId) return

  mergePermissionState.value = 'checking'
  try {
    const response = await fetchBackend(`/api/merge-groups/${mergeGroupId}/merge-permissions`)
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
  groupStatusLabel({ branches: activities.value } as MergeGroup)
)

const overallStatusClass = computed<string>(() =>
  groupStatusClass({ branches: activities.value } as MergeGroup)
)

function branchUrl(item: BranchWithActivity): string {
  if (!item.projectUrl) return ''
  return `${item.projectUrl}/-/tree/${encodeURIComponent(item.branchName)}?ref_type=heads`
}

function createMergeRequestUrl(item: BranchWithActivity): string {
  if (!item.projectUrl) return ''
  return `${item.projectUrl}/-/merge_requests/new?merge_request[source_branch]=${encodeURIComponent(item.branchName)}`
}

async function copyBranchName(branchName: string) {
  try {
    await navigator.clipboard.writeText(branchName)
  } catch (err) {
    console.warn('[Mergician] Failed to copy branch name to clipboard:', err)
  }
}

// --- Subscription management ---

async function loadSubscription() {
  const mergeGroupId = getMergeGroupId()
  if (!mergeGroupId) return

  try {
    const response = await fetchBackend(`/api/merge-groups/${mergeGroupId}/subscription`)
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
  const mergeGroupId = getMergeGroupId()
  if (!mergeGroupId) return

  subscriptionUpdating.value = true
  const method = isSubscribed.value ? 'DELETE' : 'PUT'

  try {
    const response = await fetchBackend(`/api/merge-groups/${mergeGroupId}/subscription`, { method })
    if (response.ok) {
      const data = await response.json() as { isSubscribed?: boolean }
      isSubscribed.value = data.isSubscribed === true
    } else {
      console.error('Failed to update subscription, status', response.status)
      errorMessage.value = 'Failed to update subscription.'
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
  const mergeGroupId = getMergeGroupId()
  if (!mergeGroupId || !addMergeRequestUrl.value.trim()) return

  addMergeRequestLoading.value = true
  addMergeRequestError.value = ''

  try {
    const response = await fetchBackend(`/api/merge-groups/${mergeGroupId}/add-by-merge-request`, {
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
      const data = await response.json().catch(() => null)
      addMergeRequestError.value = data?.error || `Request failed with status ${response.status}`
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
  const mergeGroupId = getMergeGroupId()
  if (!mergeGroupId) return

  const prevAutoMerge = autoMerge.value
  const prevAutoRebase = autoRebase.value

  settingsUpdating.value = true
  try {
    const response = await fetchBackend(`/api/merge-groups/${mergeGroupId}/settings`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ autoMerge: newAutoMerge, autoRebase: newAutoRebase })
    })

    if (!response.ok) {
      console.error('Failed to update settings, status', response.status)
      errorMessage.value = 'Failed to update auto merge settings.'
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
  const mergeGroupId = getMergeGroupId()
  if (!mergeGroupId) return

  autoMergeWarning.value = null

  try {
    await fetchBackend(`/api/merge-groups/${mergeGroupId}/settings/clear-warning`, {
      method: 'POST'
    })
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('Failed to clear warning:', err)
  }
}

function getMergeGroupId(): string {
  const id = route.params.mergeGroupId
  return Array.isArray(id) ? id[0] : (id ?? '')
}

/**
 * Polls the backend for a full merge group snapshot and reconciles with the displayed list.
 * Existing branches are updated, new ones added, and removed branches are cleaned up.
 */
async function pollMergeGroup() {
  const mergeGroupId = getMergeGroupId()
  if (!mergeGroupId) return

  try {
    const response = await fetchBackend(`/api/merge-groups/${mergeGroupId}/refresh`, {
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
    if (!settingsUpdating.value) {
      autoMerge.value = data.autoMerge
      autoRebase.value = data.autoRebase
      autoMergeWarning.value = data.autoMergeWarning
    }

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

/* ---- Merge group header ---- */
.merge-group-header {
  padding: 2px 0 6px;
}

.auto-merge-controls {
  display: flex;
  gap: 24px;
  flex-wrap: wrap;
}

.subscription-btn {
  text-transform: none;
}

.subscription-brown {
  background-color: #8B4513 !important; /* saddle brown */
  color: white !important;
}

/* ---- Branch card — uses .status-card base, no extra modifiers needed ---- */

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

.copy-branch-btn {
  flex-shrink: 0;
  opacity: 0.5;
  transition: opacity 0.15s;
}

.branch-card:hover .copy-branch-btn,
.copy-branch-btn:hover {
  opacity: 1;
}

.title-icon {
  color: rgba(var(--v-theme-on-surface), 0.6);
  flex-shrink: 0;
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
  color: rgb(var(--v-theme-on-surface));
  white-space: nowrap;
  flex-shrink: 0;
}

.detail-value {
  color: rgb(var(--v-theme-on-surface));
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
