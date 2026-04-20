import type { BranchWithActivity, MergeGroup } from '@/types/mergeGroup'

export type ItemStatus = 'Loading' | 'Waiting' | 'Open' | 'Ready'

/**
 * Whether a branch's detail data (MR status, approvals, build jobs) has not yet been fetched.
 */
export function isBranchLoading(item: BranchWithActivity): boolean {
  return item.hasMergeRequest === null
}

/**
 * Whether all branches in a group have had their details resolved.
 */
export function isGroupFullyLoaded(group: MergeGroup): boolean {
  return group.branches.length > 0 && group.branches.every(b => b.hasMergeRequest !== null)
}

/**
 * Determine the display status label for a single branch.
 */
export function itemStatusLabel(item: BranchWithActivity): ItemStatus {
  if (item.hasMergeRequest === null) return 'Loading'
  if (item.hasMergeRequest === false) return 'Waiting'

  if (item.approvalsRequired != null && item.approvalsGiven != null
    && item.approvalsGiven >= item.approvalsRequired) {
    return 'Ready'
  }

  return 'Open'
}

/**
 * CSS class for a status label (e.g. 'status-ready').
 */
export function statusCssClass(label: ItemStatus): string {
  return `status-${label.toLowerCase()}`
}

/**
 * Vuetify color name for a branch status chip.
 */
export function statusChipColor(label: ItemStatus): string {
  const colors: Record<ItemStatus, string> = {
    Ready: 'success',
    Open: 'info',
    Loading: 'grey',
    Waiting: 'warning',
  }
  return colors[label]
}

/**
 * Approval text for a single branch (e.g. "2/3").
 */
export function itemApprovalsText(item: BranchWithActivity): string {
  if (!item.hasMergeRequest || item.approvalsGiven == null || item.approvalsRequired == null) {
    return ''
  }
  return `${item.approvalsGiven}/${item.approvalsRequired}`
}

/**
 * Extended approval text used in the details view (includes "Not available" / "No approval needed").
 */
export function itemApprovalsTextDetailed(item: BranchWithActivity): string {
  if (!item.hasMergeRequest || item.approvalsGiven == null || item.approvalsRequired == null) {
    return 'Not available'
  }
  if (item.approvalsRequired === 0) {
    return `${item.approvalsGiven}/${item.approvalsRequired} (No approval needed)`
  }
  return `${item.approvalsGiven}/${item.approvalsRequired}`
}

// --- Group-level helpers ---

type GroupStatus = 'loading' | 'ready' | 'open' | 'waiting'

/**
 * Aggregate status for a merge group (worst-branch-wins).
 * Returns 'loading' when there are no branches yet.
 */
export function getGroupStatus(group: MergeGroup): GroupStatus {
  if (group.branches.length === 0) return 'loading'
  const statusPriority: GroupStatus[] = ['waiting', 'open', 'ready']
  let worstIndex = 2 // start with 'ready' (best)
  for (const item of group.branches) {
    const label = itemStatusLabel(item)
    let s: GroupStatus = 'waiting'
    if (label === 'Ready') s = 'ready'
    else if (label === 'Open') s = 'open'
    const idx = statusPriority.indexOf(s)
    if (idx < worstIndex) worstIndex = idx
  }
  return statusPriority[worstIndex]
}

const groupStatusLabels: Record<GroupStatus, string> = {
  loading: 'Loading',
  ready: 'Ready',
  open: 'Open',
  waiting: 'Waiting',
}

export function groupStatusLabel(group: MergeGroup): string {
  return groupStatusLabels[getGroupStatus(group)]
}

export function groupStatusClass(group: MergeGroup): string {
  return `status-${getGroupStatus(group)}`
}

// --- Job-level helpers (details view) ---

const jobStatusIcons: Record<string, string> = {
  success: 'mdi-check-circle',
  failed: 'mdi-close-circle',
  failure: 'mdi-close-circle',
  running: 'mdi-progress-clock',
  pending: 'mdi-timer-sand',
  canceled: 'mdi-cancel',
  cancelled: 'mdi-cancel',
}

export function jobStatusIcon(status: string): string {
  return jobStatusIcons[status.toLowerCase()] ?? 'mdi-help-circle'
}

const jobStatusColors: Record<string, string> = {
  success: 'success',
  failed: 'error',
  failure: 'error',
  running: 'info',
  pending: 'warning',
  canceled: 'secondary',
  cancelled: 'secondary',
}

export function jobStatusColor(status: string): string {
  return jobStatusColors[status.toLowerCase()] ?? 'default'
}
