import type { BranchWithActivity, BranchBuildJob } from '@/types/mergeGroup'

const STATUS_PRIORITY = ['failed', 'failure', 'running', 'pending', 'canceled', 'cancelled', 'success']

function jobStatusPriority(status: string): number {
  const idx = STATUS_PRIORITY.indexOf(status.toLowerCase())
  return idx === -1 ? STATUS_PRIORITY.length - 2 : idx
}

/**
 * Deduplicates build jobs across all branches in a group by job name,
 * keeping the entry with the highest-priority (worst) status.
 */
export function deduplicateJobs(branches: BranchWithActivity[]): BranchBuildJob[] {
  const jobMap = new Map<string, BranchBuildJob>()
  for (const branch of branches) {
    for (const job of branch.buildJobs ?? []) {
      const existing = jobMap.get(job.name)
      if (!existing || jobStatusPriority(job.status) < jobStatusPriority(existing.status)) {
        jobMap.set(job.name, job)
      }
    }
  }
  return [...jobMap.values()]
}
