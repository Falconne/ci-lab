import type { BranchWithActivity, MergeGroup } from '@/types/mergeGroup'

export enum MRStatus {
  Loading = 0,
  Blocked = 1,
  Waiting = 2,
  Ready   = 3,
}

/**
 * Display label for a backend-computed MR status code.
 */
export function mrStatusLabel(status: number): string {
  switch (status) {
    case MRStatus.Loading: return 'Loading'
    case MRStatus.Blocked: return 'Blocked'
    case MRStatus.Waiting: return 'Waiting'
    case MRStatus.Ready:   return 'Ready'
    default:               return 'Loading'
  }
}

/**
 * CSS class for a backend-computed MR status code.
 */
export function mrStatusClass(status: number): string {
  switch (status) {
    case MRStatus.Loading: return 'status-loading'
    case MRStatus.Blocked: return 'status-blocked'
    case MRStatus.Waiting: return 'status-waiting'
    case MRStatus.Ready:   return 'status-ready'
    default:               return 'status-loading'
  }
}

/**
 * Vuetify chip color for a backend-computed MR status code.
 */
export function mrStatusChipColor(status: number): string {
  switch (status) {
    case MRStatus.Loading: return 'grey'
    case MRStatus.Blocked: return 'warning'
    case MRStatus.Waiting: return 'info'
    case MRStatus.Ready:   return 'success'
    default:               return 'grey'
  }
}

/**
 * Aggregate MR status for a merge group (worst-branch-wins, lowest status code wins).
 * Returns Loading (0) when there are no branches.
 */
export function getGroupMRStatus(group: MergeGroup): number {
  if (group.branches.length === 0) return MRStatus.Loading
  return Math.min(...group.branches.map(b => b.mrStatus))
}

/**
 * Display label for the overall status of a merge group.
 */
export function groupStatusLabel(group: MergeGroup): string {
  return mrStatusLabel(getGroupMRStatus(group))
}

/**
 * CSS class for the overall status of a merge group.
 */
export function groupStatusClass(group: MergeGroup): string {
  return mrStatusClass(getGroupMRStatus(group))
}

/**
 * Collects status reasons from all non-Ready branches in a group,
 * formatted as "ProjectName: reason".
 */
export function getGroupStatusReasons(group: MergeGroup): string[] {
  const result: string[] = []
  for (const branch of group.branches) {
    if (branch.mrStatus === MRStatus.Loading || branch.mrStatus === MRStatus.Ready) continue
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
    return '—'
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
  running: 'mdi-play-circle',
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

/**
 * Human-readable label for a build job status string.
 */
export function jobStatusLabel(status: string): string {
  switch (status.toLowerCase()) {
    case 'success': return 'Success'
    case 'failed':
    case 'failure': return 'Failed'
    case 'running': return 'Running'
    case 'pending': return 'Pending'
    case 'canceled':
    case 'cancelled': return 'Canceled'
    default: return status
  }
}
