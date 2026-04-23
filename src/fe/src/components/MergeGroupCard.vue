<template>
  <a
    class="merge-group-card status-card"
    :data-merge-group-id="group.id"
    :aria-label="`Merge group ${group.name}`"
    :href="mergeGroupHref"
    @click.prevent="emit('navigate', group)"
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
          <template v-if="isGroupFullyLoaded(group)">
            <v-tooltip
              v-if="groupStatusLabel(group) === 'Waiting'"
              location="top"
              :text="groupWaitingReasonsText"
            >
              <template #activator="{ props: tipProps }">
                <span v-bind="tipProps" class="card-status-badge" :class="groupStatusClass(group)">
                  <span class="status-dot" />
                  {{ groupStatusLabel(group) }}
                </span>
              </template>
            </v-tooltip>
            <span v-else class="card-status-badge" :class="groupStatusClass(group)">
              <span class="status-dot" />
              {{ groupStatusLabel(group) }}
            </span>
          </template>
          <span v-else class="skeleton-badge"><span class="skeleton-shimmer" /></span>
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
              <template #activator="{ props: tipProps }">
                <span class="item-project" v-bind="tipProps" :title="item.projectNameWithNamespace">
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
            <template #activator="{ props: tipProps }">
              <span
                v-bind="tipProps"
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
              <template #activator="{ props: tipProps }">
                <span v-bind="tipProps">{{ formatTimeAgo(item.lastUpdated, now) }}</span>
              </template>
            </v-tooltip>
            <span v-else class="text-grey">—</span>
          </span>
        </div>
      </div>
      <!-- Build jobs summary across all branches -->
      <div v-if="deduplicatedJobs.length > 0" class="card-jobs">
        <v-tooltip
          v-for="job in nonSuccessJobs"
          :key="job.name"
          location="top"
          :text="job.name"
        >
          <template #activator="{ props: tipProps }">
            <v-chip
              v-bind="tipProps"
              size="x-small"
              :color="jobStatusColor(job.status)"
              variant="tonal"
              class="job-chip"
            >
              <v-icon :icon="jobStatusIcon(job.status)" size="12" class="mr-1" />
              {{ job.name }}
            </v-chip>
          </template>
        </v-tooltip>
        <v-chip
          v-if="successJobCount > 0"
          size="x-small"
          color="success"
          variant="tonal"
          class="job-chip"
        >
          <v-icon icon="mdi-check-circle" size="12" class="mr-1" />
          {{ successJobCount }} Successful {{ successJobCount === 1 ? 'Job' : 'Jobs' }}
        </v-chip>
      </div>
    </div>
  </a>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import type { BranchWithActivity, MergeGroup } from '@/types/mergeGroup'
import {
  isBranchLoading,
  isGroupFullyLoaded,
  groupStatusLabel,
  groupStatusClass,
  itemApprovalsText,
  getGroupWaitingReasons,
  jobStatusIcon,
  jobStatusColor,
} from '@/utils/statusHelpers'
import { formatDateTime, formatTimeAgo } from '@/utils/dateFormatting'

const props = defineProps<{
  group: MergeGroup
  now: number
}>()

const emit = defineEmits<{
  navigate: [group: MergeGroup]
}>()

const router = useRouter()

const mergeGroupHref = computed(() => {
  const resolved = router.resolve({
    name: 'merge-group-details',
    params: { mergeGroupId: props.group.id.toString() },
    query: { title: props.group.name }
  })
  return resolved.href
})

const groupWaitingReasonsText = computed(() => {
  const reasons = getGroupWaitingReasons(props.group)
  return reasons.length > 0 ? reasons.join(', ') : 'Waiting'
})

// Status priority for deduplication: lower index = worse
const STATUS_PRIORITY = ['failed', 'failure', 'running', 'pending', 'canceled', 'cancelled', 'success']

function jobStatusPriority(status: string): number {
  const idx = STATUS_PRIORITY.indexOf(status.toLowerCase())
  return idx === -1 ? STATUS_PRIORITY.length - 2 : idx
}

const deduplicatedJobs = computed(() => {
  const jobMap = new Map<string, { name: string; status: string; url?: string | null }>()
  for (const branch of props.group.branches) {
    for (const job of branch.buildJobs ?? []) {
      const existing = jobMap.get(job.name)
      if (!existing || jobStatusPriority(job.status) < jobStatusPriority(existing.status)) {
        jobMap.set(job.name, job)
      }
    }
  }
  return [...jobMap.values()]
})

const nonSuccessJobs = computed(() =>
  deduplicatedJobs.value.filter(j => j.status.toLowerCase() !== 'success')
)

const successJobCount = computed(() =>
  deduplicatedJobs.value.filter(j => j.status.toLowerCase() === 'success').length
)

const MAX_TITLE_LENGTH = 222

function truncateTitle(title: string): string {
  if (title.length <= MAX_TITLE_LENGTH) return title
  return title.slice(0, MAX_TITLE_LENGTH) + '...'
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
</script>

<style scoped>
@import '@/assets/status-badges.css';

.merge-group-card {
  transition: box-shadow 0.2s ease;
  cursor: pointer;
  text-decoration: none;
  color: inherit;
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
  color: rgba(var(--v-theme-on-surface), 0.6);
  margin-right: 6px;
  flex-shrink: 0;
}

.branch-name {
  font-weight: 600;
  font-size: 0.95rem;
  color: rgb(var(--v-theme-on-surface));
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
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

/* ---- Build jobs summary ---- */
.card-jobs {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  padding-top: 8px;
  border-top: 1px solid rgba(var(--v-theme-on-surface), 0.08);
  margin-top: 4px;
}

.job-chip {
  max-width: 160px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-item {
  display: flex;
  align-items: center;
  padding: 6px 0;
  border-top: 1px solid rgba(var(--v-theme-on-surface), 0.08);
  font-size: 0.85rem;
  gap: 12px;
}

.item-main {
  display: flex;
  align-items: center;
  flex: 1;
  min-width: 0;
}

.item-project {
  font-weight: 500;
  color: rgb(var(--v-theme-on-surface));
  flex-shrink: 0;
  white-space: nowrap;
}

.item-mr-title {
  font-size: 0.85rem;
  color: rgba(var(--v-theme-on-surface), 0.6);
  margin-left: 4px;
  flex: 1;
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.item-no-mr {
  font-size: 0.85rem;
  color: rgba(var(--v-theme-on-surface), 0.6);
  margin-left: 4px;
  white-space: nowrap;
}

.item-approvals {
  font-size: 0.78rem;
  color: rgba(var(--v-theme-on-surface), 0.6);
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
  color: rgba(var(--v-theme-on-surface), 0.6);
  white-space: nowrap;
  flex-shrink: 0;
  min-width: 70px;
  text-align: right;
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
