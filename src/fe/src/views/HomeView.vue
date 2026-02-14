<template>
  <v-container>
    <v-row justify="center" class="mt-4">
      <v-col cols="12" md="10" lg="8">
        <!-- Error alert -->
        <v-alert
          v-if="errorMessage"
          type="error"
          variant="tonal"
          closable
          @click:close="errorMessage = ''"
          class="mb-4"
        >
          {{ errorMessage }}
        </v-alert>

        <!-- Welcome page for unauthenticated users -->
        <div v-if="!authenticated && !initialLoading" class="text-center pa-12">
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

        <div v-else-if="initialLoading" class="text-center pa-8">
          <v-progress-circular indeterminate color="primary" size="48" />
          <p class="mt-4 text-body-1">Loading dashboard...</p>
        </div>

        <div v-else-if="mergeGroups.length === 0 && !streaming" class="text-center pa-8">
          <v-icon icon="mdi-source-branch" size="64" color="grey" class="mb-4" />
          <p class="text-h6 text-grey">No active branches in the last 14 days</p>
        </div>

        <div v-else>
          <h2 class="text-h5 mb-4">
            Dashboard
            <v-progress-circular
              v-if="streaming"
              indeterminate
              color="primary"
              size="18"
              width="2"
              class="ml-2"
            />
          </h2>
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
              <template v-for="group in mergeGroups" :key="group.branchName">
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
                    <v-progress-circular
                      v-if="item.hasMergeRequest === null"
                      indeterminate
                      color="grey"
                      size="16"
                      width="2"
                    />
                    <v-icon
                      v-else-if="item.hasMergeRequest"
                      icon="mdi-check-circle"
                      color="primary"
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
                    <v-progress-circular
                      v-if="item.hasMergeRequest === null"
                      indeterminate
                      color="grey"
                      size="16"
                      width="2"
                    />
                    <template v-else-if="item.hasMergeRequest && item.approvalsRequired != null && item.approvalsGiven != null">
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
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'

interface BranchActivity {
  branchName: string
  projectId: number
  projectName: string
  hasMergeRequest: boolean | null
  approvalsRequired: number | null
  approvalsGiven: number | null
}

interface MergeGroup {
  branchName: string
  items: BranchActivity[]
}

const route = useRoute()
const router = useRouter()

const activities = ref<BranchActivity[]>([])
const initialLoading = ref(true)
const authenticated = ref(false)
const streaming = ref(false)
const errorMessage = ref('')

let eventSource: EventSource | null = null

const mergeGroups = computed<MergeGroup[]>(() => {
  const groups = new Map<string, BranchActivity[]>()
  for (const item of activities.value) {
    const existing = groups.get(item.branchName)
    if (existing) {
      // Skip duplicates: same branch + project already in the group
      if (!existing.some(e => e.projectId === item.projectId)) {
        existing.push(item)
      }
    } else {
      groups.set(item.branchName, [item])
    }
  }
  return Array.from(groups.entries()).map(([branchName, items]) => ({
    branchName,
    items
  }))
})

function handleActivityEvent(data: BranchActivity) {
  const existingIndex = activities.value.findIndex(
    a => a.branchName === data.branchName && a.projectId === data.projectId
  )

  if (existingIndex >= 0) {
    // Update existing entry in place (e.g. MR/approval data arrived)
    activities.value[existingIndex] = data
  } else {
    // Find the right insertion point: if a group with this branchName already exists,
    // insert after the last item in that group to maintain grouping
    const lastGroupIndex = findLastIndexOf(activities.value, a => a.branchName === data.branchName)
    if (lastGroupIndex >= 0) {
      activities.value.splice(lastGroupIndex + 1, 0, data)
    } else {
      activities.value.push(data)
    }
  }
}

function findLastIndexOf<T>(arr: T[], predicate: (item: T) => boolean): number {
  for (let i = arr.length - 1; i >= 0; i--) {
    if (predicate(arr[i])) return i
  }
  return -1
}

function startStreaming() {
  streaming.value = true
  eventSource = new EventSource('/api/activity/stream')

  eventSource.onmessage = (event) => {
    try {
      const data: BranchActivity = JSON.parse(event.data)
      handleActivityEvent(data)
    } catch (err) {
      console.error('Failed to parse SSE data:', err)
    }
  }

  eventSource.addEventListener('done', () => {
    streaming.value = false
    eventSource?.close()
    eventSource = null
  })

  eventSource.onerror = () => {
    streaming.value = false
    eventSource?.close()
    eventSource = null
  }
}

onMounted(async () => {
  // Check for error in query parameters
  if (route.query.error && route.query.message) {
    errorMessage.value = route.query.message as string
    // Clean up URL by removing error params
    router.replace({ query: {} })
  }

  try {
    const meResponse = await fetch('/api/auth/me')
    if (meResponse.status === 401) {
      initialLoading.value = false
      return
    }
    authenticated.value = true
    initialLoading.value = false

    startStreaming()
  } catch (err) {
    console.error('Failed to load dashboard:', err)
    initialLoading.value = false
  }
})

onUnmounted(() => {
  eventSource?.close()
  eventSource = null
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
