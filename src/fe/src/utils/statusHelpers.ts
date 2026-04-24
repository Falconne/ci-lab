import type { BranchWithActivity, MergeGroup } from '@/types/mergeGroup'

// Status codes: 0=Loading, 1=Blocked, 2=Waiting, 3=Ready
const STATUS_LOADING = 0
const STATUS_READY = 3

/**
 * Display label for a backend-computed MR status code.
 */
export function mrStatusLabel(status: number): string {
  switch (status) {
    case 0: return 'Loading'
    case 1: return 'Blocked'
    case 2: return 'Waiting'
    case 3: return 'Ready'
    default: return 'Loading'
  }
}

/**
 * CSS class for a backend-computed MR status code.
 */
export function mrStatusClass(status: number): string {
  switch (status) {
    case 0: return 'status-loading'
    case 1: return 'status-blocked'
    case 2: return 'status-waiting'
    case 3: return 'status-ready'
    default: return 'status-loading'
  }
}

/**
 * Vuetify chip color for a backend-computed MR status code.
 */
export function mrStatusChipColor(status: number): string {
  switch (status) {
    case 0: return 'grey'
    case 1: return 'error'
    case 2: return 'info'
    case 3: return 'success'
    default: return 'grey'
  }
}

/**
 * Aggregate MR status for a merge group (worst-branch-wins, lowest status code wins).
 * Returns Loading (0) when there are no branches.
 */
export function getGroupMrStatus(group: MergeGroup): number {
  if (group.branches.length === 0) return STATUS_LOADING
  return Math.min(...group.branches.map(b => b.mrStatus))
}

/**
 * Display label for the overall status of a merge group.
 */
export function groupStatusLabel(group: MergeGroup): string {
  return mrStatusLabel(getGroupMrStatus(group))
}

/**
 * CSS class for the overall status of a merge group.
 */
export function groupStatusClass(group: MergeGroup): string {
  return mrStatusClass(getGroupMrStatus(group))
}

/**
 * Collects status reasons from all non-Ready branches in a group,
 * formatted as "ProjectName: reason".
 */
export function getGroupStatusReasons(group: MergeGroup): string[] {
  const result: string[] = []
  for (const branch of group.branches) {
    if (branch.mrStatus === STATUS_LOADING || branch.mrStatus === STATUS_READY) continue
    if (branch.mrStatusReasons && branch.mrStatusReasons.length > 0) {
      for (const reason of branch.mrStatusReasons) {
        result.push(`${branch.projectName}: ${reason}`)
      }
    }
  }
  return result
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
