export interface BranchBuildJob {
  name: string
  status: string
  url?: string | null
}

export interface BranchWithActivity {
  branchName: string
  projectId: number
  projectName: string
  projectNameWithNamespace: string
  hasMergeRequest: boolean | null
  approvalsRequired: number | null
  approvalsGiven: number | null
  lastUpdated: string | null
  mergeRequestTitle?: string | null
  mergeRequestUrl?: string | null
  projectUrl?: string | null
  needsRebase?: boolean | null
  buildJobs?: BranchBuildJob[] | null
  id: number
}

export interface MergeGroup {
  id: number
  name: string
  branches: BranchWithActivity[]
  autoMerge: boolean
  autoRebase: boolean
  autoMergeWarning: string | null
}
