<template>
  <div class="mg-grid-container">
    <div
      v-for="section in sections"
      :key="section.title"
      class="grid-section"
    >
      <div v-if="section.title" class="grid-section-header">{{ section.title }}</div>
      <div class="grid-wrapper">
        <table class="mg-grid">
          <thead>
            <tr>
              <th class="col-mg">Branch</th>
              <th v-if="sectionHasAutoMerge(section)" class="col-status">Status</th>
              <th class="col-project">Project</th>
              <th class="col-mr">Merge Request</th>
              <th class="col-approvals">Approvals</th>
              <th class="col-updated">Updated</th>
              <th class="col-jobs">Build Jobs</th>
            </tr>
          </thead>
          <tbody>
            <template v-for="group in section.groups" :key="group.id">
              <tr
                v-for="{ branch, idx } in groupRows(group)"
                :key="`${group.id}-${branch?.projectId ?? 'loading'}`"
                class="grid-row"
                :class="{
                  'grid-row--sub': idx > 0,
                  'grid-row--last-in-group': idx === Math.max(0, group.branches.length - 1),
                }"
                @click="emit('navigate', group)"
              >
                <!-- Branch name: rowspan spanning all branch rows -->
                <td v-if="idx === 0" :rowspan="Math.max(1, group.branches.length)" class="col-mg">
                  <div class="mg-name-cell">
                    <span class="mg-branch-name">{{ group.name }}</span>
                    <a
                      v-if="group.queueId != null && group.queuePosition != null"
                      class="queue-position-badge"
                      @click.stop="navigateToQueue(group)"
                    >
                      <v-icon icon="mdi-playlist-play" size="11" class="mr-1" />
                      {{ group.queuePosition === 1 ? 'Next' : `#${group.queuePosition}` }}
                    </a>
                  </div>
                </td>

                <!-- Status chip: rowspan spanning all branch rows, only in autoMerge sections -->
                <td v-if="idx === 0 && sectionHasAutoMerge(section)" :rowspan="Math.max(1, group.branches.length)" class="col-status">
                  <template v-if="group.autoMerge && isGroupLoaded(group)">
                    <v-tooltip v-if="getGroupStatusReasons(group).length > 0" location="top">
                      <template #activator="{ props: tipProps }">
                        <span v-bind="tipProps" class="card-status-badge" :class="groupStatusClass(group)">
                          <span class="status-dot" />
                          {{ groupStatusLabel(group) }}
                        </span>
                      </template>
                      <span class="tooltip-multiline">{{ getGroupStatusReasons(group).join('\n') }}</span>
                    </v-tooltip>
                    <span v-else class="card-status-badge" :class="groupStatusClass(group)">
                      <span class="status-dot" />
                      {{ groupStatusLabel(group) }}
                    </span>
                  </template>
                  <span v-else-if="group.autoMerge" class="skeleton-badge"><span class="skeleton-shimmer" /></span>
                </td>

                <!-- Project name (per branch) -->
                <td class="col-project">
                  <template v-if="branch">
                    <v-tooltip location="top" :text="branch.projectNameWithNamespace">
                      <template #activator="{ props: tipProps }">
                        <span v-bind="tipProps" class="project-name">{{ branch.projectName }}</span>
                      </template>
                    </v-tooltip>
                  </template>
                  <span v-else class="skeleton-inline"><span class="skeleton-shimmer" /></span>
                </td>

                <!-- MR title (per branch) -->
                <td class="col-mr">
                  <span v-if="!branch || branch.mrStatus === MRStatus.Loading" class="skeleton-inline"><span class="skeleton-shimmer" /></span>
                  <v-tooltip v-else-if="branch.mergeRequestTitle" location="top" :text="branch.mergeRequestTitle">
                    <template #activator="{ props: tipProps }">
                      <span v-bind="tipProps" class="mr-title">{{ branch.mergeRequestTitle }}</span>
                    </template>
                  </v-tooltip>
                  <span v-else-if="branch.hasMergeRequest === false" class="no-mr-text">No MR</span>
                </td>

                <!-- Approvals (per branch) -->
                <td class="col-approvals">
                  <template v-if="branch && branch.mrStatus !== MRStatus.Loading && itemApprovalsText(branch)">
                    <v-tooltip location="top" :text="approvalsTooltip(branch)">
                      <template #activator="{ props: tipProps }">
                        <span v-bind="tipProps" class="approvals-cell">
                          {{ itemApprovalsText(branch) }}
                        </span>
                      </template>
                    </v-tooltip>
                  </template>
                </td>

                <!-- Last updated (per branch) -->
                <td class="col-updated">
                  <span v-if="!branch || branch.mrStatus === MRStatus.Loading" class="skeleton-inline skeleton-inline--sm"><span class="skeleton-shimmer" /></span>
                  <v-tooltip v-else-if="branch.lastUpdated" location="top" :text="formatDateTime(branch.lastUpdated)">
                    <template #activator="{ props: tipProps }">
                      <span v-bind="tipProps" class="updated-text">{{ formatTimeAgo(branch.lastUpdated, now) }}</span>
                    </template>
                  </v-tooltip>
                  <span v-else class="text-grey">—</span>
                </td>

                <!-- Build jobs: rowspan spanning all branch rows -->
                <td v-if="idx === 0" :rowspan="Math.max(1, group.branches.length)" class="col-jobs">
                  <div v-if="deduplicatedJobs(group).length > 0" class="jobs-cell">
                    <v-tooltip
                      v-for="job in nonSuccessJobs(group)"
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
                          <v-icon :icon="jobStatusIcon(job.status)" size="10" class="mr-1" />
                          {{ job.name }}
                        </v-chip>
                      </template>
                    </v-tooltip>
                    <v-chip v-if="successJobCount(group) > 0" size="x-small" color="success" variant="tonal" class="job-chip">
                      <v-icon icon="mdi-check-circle" size="10" class="mr-1" />
                      {{ successJobCount(group) }} ✓
                    </v-chip>
                  </div>
                </td>
              </tr>
            </template>
          </tbody>
        </table>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useRouter } from 'vue-router'
import type { BranchWithActivity, MergeGroup } from '@/types/mergeGroup'
import {
  groupStatusClass,
  groupStatusLabel,
  getGroupStatusReasons,
  itemApprovalsText,
  jobStatusIcon,
  jobStatusColor,
  jobStatusLabel,
  MRStatus,
} from '@/utils/statusHelpers'
import { formatDateTime, formatTimeAgo } from '@/utils/dateFormatting'

export interface GridSection {
  title: string
  groups: MergeGroup[]
}

const props = defineProps<{
  sections: GridSection[]
  now: number
}>()

const emit = defineEmits<{
  navigate: [group: MergeGroup]
}>()

const router = useRouter()

// --- Helpers ---

function isGroupLoaded(group: MergeGroup): boolean {
  return group.branches.length > 0 && group.branches.every(b => b.mrStatus !== MRStatus.Loading)
}

function sectionHasAutoMerge(section: GridSection): boolean {
  return section.groups.some(g => g.autoMerge)
}

/** Returns one row descriptor per branch, or a single null-branch row when branches haven't loaded. */
function groupRows(group: MergeGroup): { branch: BranchWithActivity | null; idx: number }[] {
  if (group.branches.length === 0) return [{ branch: null, idx: 0 }]
  return group.branches.map((branch, idx) => ({ branch, idx }))
}

// --- Build job deduplication ---

const STATUS_PRIORITY = ['failed', 'failure', 'running', 'pending', 'canceled', 'cancelled', 'success']

function jobStatusPriority(status: string): number {
  const idx = STATUS_PRIORITY.indexOf(status.toLowerCase())
  return idx === -1 ? STATUS_PRIORITY.length - 2 : idx
}

function deduplicatedJobs(group: MergeGroup): { name: string; status: string; url?: string | null }[] {
  const jobMap = new Map<string, { name: string; status: string; url?: string | null }>()
  for (const branch of group.branches) {
    for (const job of branch.buildJobs ?? []) {
      const existing = jobMap.get(job.name)
      if (!existing || jobStatusPriority(job.status) < jobStatusPriority(existing.status)) {
        jobMap.set(job.name, job)
      }
    }
  }
  return [...jobMap.values()]
}

function nonSuccessJobs(group: MergeGroup) {
  return deduplicatedJobs(group).filter(j => j.status.toLowerCase() !== 'success')
}

function successJobCount(group: MergeGroup): number {
  return deduplicatedJobs(group).filter(j => j.status.toLowerCase() === 'success').length
}

// --- Approvals ---

function approvalsTooltip(item: BranchWithActivity): string {
  if (!item.hasMergeRequest || item.approvalsGiven == null || item.approvalsRequired == null) return ''
  if (item.approvalsRequired === 0) return 'No approval needed'
  if (item.approvalsGiven >= item.approvalsRequired) return 'All required approvals given'
  return `${item.approvalsGiven} of ${item.approvalsRequired} needed approvals given`
}

function navigateToQueue(group: MergeGroup) {
  if (group.queueId == null) return
  router.push({ name: 'queues', query: { queueId: group.queueId } })
}
</script>

<style scoped>
@import '@/assets/status-badges.css';

/* ---- Section header (matches dashboard partition header) ---- */
.grid-section {
  margin-bottom: 28px;
}

.grid-section:last-child {
  margin-bottom: 0;
}

.grid-section-header {
  font-size: 0.75rem;
  font-weight: 600;
  color: rgba(var(--v-theme-on-surface), 0.6);
  text-transform: uppercase;
  letter-spacing: 0.08em;
  margin-bottom: 10px;
  padding-bottom: 6px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.08);
}

/* ---- Grid table ---- */
.grid-wrapper {
  overflow-x: auto;
}

.mg-grid {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.85rem;
  table-layout: fixed;
}

/* ---- Column widths ---- */
.col-mg     { width: 20%; }
.col-status { width: 90px; }
.col-project { width: 12%; }
.col-mr     { width: auto; }
.col-approvals { width: 80px; }
.col-updated { width: 85px; }
.col-jobs   { width: 22%; }

/* ---- Header row ---- */
thead tr {
  border-bottom: 2px solid rgba(var(--v-theme-on-surface), 0.12);
}

thead th {
  padding: 8px 12px;
  text-align: left;
  font-size: 0.72rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: rgba(var(--v-theme-on-surface), 0.55);
  white-space: nowrap;
}

/* ---- Grid rows ---- */
.grid-row {
  cursor: pointer;
  border-top: 1.5px solid rgba(var(--v-theme-on-surface), 0.1);
  transition: background 0.1s ease;
}

.grid-row:hover {
  background: rgba(var(--v-theme-primary), 0.04);
}

/* Sub-rows (2nd+ branch within an MG) use a lighter border */
.grid-row--sub {
  border-top: 1px solid rgba(var(--v-theme-on-surface), 0.05);
}

/* Extra bottom padding on the last branch row of each MG group */
.grid-row--last-in-group td {
  padding-bottom: 14px;
}

.mg-grid td {
  padding: 8px 12px;
  vertical-align: middle;
  min-width: 0;
  overflow: hidden;
}

/* Common cells (spanning all branch rows) align to top so content starts at the first sub-row */
.col-mg,
.col-status,
.col-jobs {
  vertical-align: middle;
}

/* ---- MG name cell ---- */
.mg-name-cell {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.mg-branch-name {
  font-weight: 400;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  color: rgb(var(--v-theme-on-surface));
}

.queue-position-badge {
  display: inline-flex;
  align-items: center;
  align-self: flex-start;
  font-size: 0.68rem;
  font-weight: 600;
  padding: 1px 6px;
  border-radius: 10px;
  background: rgba(var(--v-theme-primary), 0.12);
  color: rgb(var(--v-theme-primary));
  text-decoration: none;
  white-space: nowrap;
  cursor: pointer;
}

.queue-position-badge:hover {
  background: rgba(var(--v-theme-primary), 0.22);
}

/* ---- Per-branch cells ---- */
.project-name {
  font-weight: 500;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  display: block;
  color: rgb(var(--v-theme-on-surface));
}

.mr-title {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  display: block;
  color: rgba(var(--v-theme-on-surface), 0.7);
}

.no-mr-text {
  font-size: 0.8rem;
  color: rgba(var(--v-theme-on-surface), 0.4);
}

.approvals-cell {
  font-size: 0.78rem;
  color: rgba(var(--v-theme-on-surface), 0.6);
  white-space: nowrap;
}

.updated-text {
  font-size: 0.75rem;
  color: rgba(var(--v-theme-on-surface), 0.6);
  white-space: nowrap;
}

/* ---- Build jobs cell ---- */
.jobs-cell {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  align-items: center;
}

.job-chip {
  white-space: nowrap;
}

/* ---- Skeletons ---- */
.skeleton-inline {
  display: inline-block;
  width: 100px;
  height: 13px;
  border-radius: 4px;
  overflow: hidden;
  vertical-align: middle;
}

.skeleton-inline--sm {
  width: 55px;
}

.tooltip-multiline {
  white-space: pre-line;
}
</style>
