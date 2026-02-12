<template>
  <v-container>
    <v-row justify="center" class="mt-4">
      <v-col cols="12" md="10" lg="8">
        <!-- Welcome page for unauthenticated users -->
        <div v-if="!authenticated && !loading" class="text-center pa-12">
          <v-icon icon="mdi-source-merge" size="80" color="primary" class="mb-6" />
          <h1 class="text-h3 mb-4">Welcome to Mergician</h1>
          <p class="text-body-1 text-grey-darken-1 mb-8" style="max-width: 500px; margin: 0 auto;">
            Mergician helps you coordinate merge requests across multiple Git repositories.
            Sign in with your GitLab account to get started.
          </p>
          <v-btn
            color="primary"
            size="large"
            href="/api/auth/login"
            prepend-icon="mdi-login"
          >
            Sign in with GitLab
          </v-btn>
        </div>

        <div v-else-if="loading" class="text-center pa-8">
          <v-progress-circular indeterminate color="primary" size="48" />
          <p class="mt-4 text-body-1">Loading dashboard...</p>
        </div>

        <div v-else-if="groupedBranches.length === 0" class="text-center pa-8">
          <v-icon icon="mdi-source-branch" size="64" color="grey" class="mb-4" />
          <p class="text-h6 text-grey">No active branches in the last 14 days</p>
        </div>

        <div v-else>
          <h2 class="text-h5 mb-4">Dashboard</h2>
          <v-table class="dashboard-table" density="comfortable">
            <thead>
              <tr>
                <th>Branch</th>
                <th>Repository</th>
                <th class="text-center">MR</th>
                <th class="text-center">Approvals</th>
              </tr>
            </thead>
            <tbody>
              <template v-for="group in groupedBranches" :key="group.branchName">
                <tr v-for="(item, idx) in group.items" :key="`${group.branchName}-${item.projectId}`">
                  <!-- Branch name cell: only show on first row of group, span all rows -->
                  <td
                    v-if="idx === 0"
                    :rowspan="group.items.length"
                    class="branch-name-cell font-weight-medium"
                  >
                    <v-icon icon="mdi-source-branch" size="small" class="mr-1" />
                    {{ group.branchName }}
                  </td>

                  <!-- Repository name -->
                  <td>{{ item.projectName }}</td>

                  <!-- MR status -->
                  <td class="text-center">
                    <v-icon
                      v-if="item.hasMergeRequest"
                      icon="mdi-check-circle"
                      color="success"
                      size="small"
                    />
                    <v-icon
                      v-else
                      icon="mdi-minus-circle-outline"
                      color="grey"
                      size="small"
                    />
                  </td>

                  <!-- Approval status -->
                  <td class="text-center">
                    <template v-if="item.hasMergeRequest && item.approvalsRequired != null && item.approvalsGiven != null">
                      <v-chip
                        :color="item.approvalsGiven >= item.approvalsRequired ? 'success' : 'warning'"
                        size="small"
                        variant="tonal"
                      >
                        <v-icon
                          v-if="item.approvalsGiven >= item.approvalsRequired"
                          icon="mdi-check"
                          size="x-small"
                          class="mr-1"
                        />
                        {{ item.approvalsGiven }}/{{ item.approvalsRequired }}
                      </v-chip>
                    </template>
                    <span v-else class="text-grey">—</span>
                  </td>
                </tr>
              </template>
            </tbody>
          </v-table>
        </div>
      </v-col>
    </v-row>
  </v-container>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'

interface BranchActivity {
  branchName: string
  projectId: number
  projectName: string
  hasMergeRequest: boolean
  approvalsRequired: number | null
  approvalsGiven: number | null
}

interface BranchGroup {
  branchName: string
  items: BranchActivity[]
}

const activities = ref<BranchActivity[]>([])
const loading = ref(true)
const authenticated = ref(false)

const groupedBranches = computed<BranchGroup[]>(() => {
  const groups = new Map<string, BranchActivity[]>()
  for (const item of activities.value) {
    const existing = groups.get(item.branchName)
    if (existing) {
      existing.push(item)
    } else {
      groups.set(item.branchName, [item])
    }
  }
  return Array.from(groups.entries()).map(([branchName, items]) => ({
    branchName,
    items
  }))
})

onMounted(async () => {
  try {
    const meResponse = await fetch('/api/auth/me')
    if (meResponse.status === 401) {
      loading.value = false
      return
    }
    authenticated.value = true

    const response = await fetch('/api/activity')
    if (response.ok) {
      activities.value = await response.json()
    }
  } catch (err) {
    console.error('Failed to load dashboard:', err)
  } finally {
    loading.value = false
  }
})
</script>

<style scoped>
.dashboard-table {
  border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  border-radius: 4px;
}

.branch-name-cell {
  vertical-align: top;
  border-right: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  background-color: rgba(var(--v-theme-surface-variant), 0.3);
}
</style>
