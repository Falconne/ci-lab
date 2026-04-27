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
          <span class="branch-name">{{ group.name }}</span>
        </div>
        <div class="card-header-right">
          <span v-if="group.autoMerge" class="auto-merge-badge">
            <v-icon icon="mdi-merge" size="x-small" />
            Auto Merge
          </span>
          <template v-if="isGroupLoaded">
            <v-tooltip
              v-if="groupStatusReasons.length > 0"
              location="top"
            >
              <template #activator="{ props: tipProps }">
                <span v-bind="tipProps" class="card-status-badge" :class="groupStatusClass(group)">
                  <span class="status-dot" />
                  {{ groupStatusLabel(group) }}
                </span>
              </template>
              <span class="tooltip-multiline">{{ groupStatusReasonsText }}</span>
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
          <v-tooltip location="top" :text="item.projectNameWithNamespace">
            <template #activator="{ props: tipProps }">
              <span class="item-project" v-bind="tipProps" :title="item.projectNameWithNamespace">
                {{ item.projectName }}
              </span>
            </template>
          </v-tooltip>

          <span class="item-mr-area">
            <template v-if="item.mrStatus === 0">
              <span class="item-skeleton-inline"><span class="skeleton-shimmer" /></span>
            </template>
            <template v-else>
              <v-tooltip
                v-if="item.mergeRequestTitle && item.mergeRequestTitle.length > MAX_TITLE_LENGTH"
                location="top"
                :text="item.mergeRequestTitle"
              >
                <template #activator="{ props: tipProps }">
                  <span class="item-mr-title" v-bind="tipProps">
                    | {{ truncateTitle(item.mergeRequestTitle as string) }}
                  </span>
                </template>
              </v-tooltip>
              <span v-else-if="item.mergeRequestTitle" class="item-mr-title">
                | {{ item.mergeRequestTitle }}
              </span>
              <span
                v-else-if="item.hasMergeRequest === false"
                class="item-no-mr"
              >
                No Merge Request
              </span>
            </template>
          </span>

          <v-tooltip
            v-if="item.mrStatus !== 0 && itemApprovalsText(item)"
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
          <span v-else class="item-approvals-placeholder" />

          <span class="item-time">
            <span v-if="item.mrStatus === 0" class="skeleton-time"><span class="skeleton-shimmer" /></span>
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
        <span class="card-jobs-label">Build Jobs</span>
        <v-tooltip
          v-for="job in nonSuccessJobs"
          :key="job.name"
          location="top"
          :text="`${job.name} \u2022 ${jobStatusLabel(job.status)}`"
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
  groupStatusLabel,
  groupStatusClass,
  itemApprovalsText,
  getGroupStatusReasons,
  jobStatusIcon,
  jobStatusColor,
  jobStatusLabel,
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

const isGroupLoaded = computed(() =>
  props.group.branches.length > 0 && props.group.branches.every(b => b.mrStatus !== 0)
)

const groupStatusReasons = computed(() => getGroupStatusReasons(props.group))

const groupStatusReasonsText = computed(() => groupStatusReasons.value.join('\n'))

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
  margin: -14px -18px 10px -18px;
  padding: 10px 18px;
  background: #E8EEF8;
  flex-wrap: wrap;
  gap: 8px;
}

.branch-info {
  display: flex;
  align-items: center;
  min-width: 0;
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
  display: grid;
  grid-template-columns: max-content 1fr auto auto;
  gap: 0 12px;
}

.card-item {
  display: contents;
}

.card-item > * {
  padding: 6px 0;
  border-top: 1px solid rgba(var(--v-theme-on-surface), 0.08);
  font-size: 0.85rem;
  min-width: 0;
}

.card-item:first-child > * {
  border-top: none;
}

/* ---- Build jobs summary ---- */
.card-jobs {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px;
  padding-top: 8px;
  border-top: 1px solid rgba(var(--v-theme-on-surface), 0.08);
  margin-top: 4px;
}

.card-jobs-label {
  font-size: 0.72rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: rgba(var(--v-theme-on-surface), 0.5);
  margin-right: 4px;
  white-space: nowrap;
}

.job-chip {
  white-space: nowrap;
}

.item-project {
  font-weight: 500;
  color: rgb(var(--v-theme-on-surface));
  white-space: nowrap;
}

.item-mr-area {
  display: flex;
  align-items: center;
  min-width: 0;
  overflow: hidden;
}

.item-mr-title {
  font-size: 0.85rem;
  color: rgba(var(--v-theme-on-surface), 0.6);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.item-no-mr {
  font-size: 0.85rem;
  color: rgba(var(--v-theme-on-surface), 0.6);
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

.item-approvals-placeholder {
  /* empty placeholder keeps approval column in the grid */
}

.item-time {
  font-size: 0.75rem;
  color: rgba(var(--v-theme-on-surface), 0.6);
  white-space: nowrap;
  text-align: right;
  min-width: 70px;
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

.tooltip-multiline {
  white-space: pre-line;
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
}
</style>
