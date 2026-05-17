<template>
  <v-container fluid class="px-6">
    <v-row class="mt-4">
      <v-col cols="12">
        <v-alert
          v-if="errorMessage"
          type="error"
          variant="tonal"
          closable
          class="mb-4"
          @click:close="errorMessage = ''"
        >
          {{ errorMessage }}
        </v-alert>

        <!-- No queues active at all -->
        <div v-if="queuesLoaded && allQueues.length === 0" class="text-center pa-8">
          <v-icon icon="mdi-playlist-play" size="64" color="grey" class="mb-4" />
          <p class="text-h6 text-grey">No queues are active</p>
          <p class="text-body-2 text-grey mt-2">
            Queues are created automatically when merge groups are set to
            Auto Merge + Auto Rebase with no blocking conditions.
          </p>
        </div>

        <template v-else-if="queuesLoaded">
          <!-- Show all queues checkbox -->
          <div class="queues-toolbar mb-4">
            <v-checkbox
              v-model="showAllQueues"
              label="Show all queues"
              hide-details
              density="compact"
            />
          </div>

          <!-- No queues match the current filter -->
          <div v-if="visibleQueues.length === 0" class="text-center pa-8">
            <v-icon icon="mdi-playlist-check" size="48" color="grey" class="mb-3" />
            <p class="text-body-1 text-grey">No queues have your tracked merge groups.</p>
            <p class="text-body-2 text-grey mt-1">Check "Show all queues" to see all active queues.</p>
          </div>

          <!-- One section per visible queue -->
          <div class="queues-sections">
            <div
              v-for="queue in visibleQueues"
              :key="queue.queueId"
              class="queue-section"
            >
              <div class="queue-section-header">{{ queue.displayName }}</div>

              <div v-if="!queueGroupsMap.has(queue.queueId)" class="text-center pa-4">
                <p class="text-body-2 text-grey">Loading queue...</p>
              </div>

              <div v-else-if="(queueGroupsMap.get(queue.queueId) ?? []).length === 0" class="text-center pa-4">
                <v-icon icon="mdi-playlist-remove" size="40" color="grey" class="mb-2" />
                <p class="text-body-2 text-grey">This queue is empty</p>
              </div>

              <MergeGroupGrid
                v-else
                :sections="[{ title: '', groups: queueGroupsMap.get(queue.queueId) ?? [] }]"
                :now="now"
                @navigate="openMergeGroupDetails"
              />
            </div>
          </div>
        </template>
      </v-col>
    </v-row>
  </v-container>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { fetchBackend, isStartupRequiredError } from '@/composables/useBackendFetch'
import { usePolling } from '@/composables/usePolling'
import { useNow } from '@/composables/useNow'
import type { MergeGroup } from '@/types/mergeGroup'
import MergeGroupGrid from '@/components/MergeGroupGrid.vue'

interface QueueSummary {
  queueId: number
  displayName: string
  entryCount: number
  hasTrackedGroups: boolean
}

const router = useRouter()
const now = useNow()

const allQueues = ref<QueueSummary[]>([])
const queuesLoaded = ref(false)
const queueGroupsMap = ref(new Map<number, MergeGroup[]>())
const showAllQueues = ref(false)
const errorMessage = ref('')

const visibleQueues = computed(() => {
  if (showAllQueues.value) return allQueues.value
  return allQueues.value.filter(q => q.hasTrackedGroups)
})

// ---- Polling: list of queues (every 5s) ----
const { start: startQueueListPolling } = usePolling(pollQueueList)

async function pollQueueList() {
  try {
    const response = await fetchBackend('/api/merge-queues')
    if (!response.ok) {
      console.warn('[Queues] Failed to fetch queue list, status', response.status)
      return
    }
    const data = await response.json() as QueueSummary[]
    allQueues.value = data
    queuesLoaded.value = true

    // Remove stale queues from the content map
    const activeIds = new Set(data.map(q => q.queueId))
    for (const id of queueGroupsMap.value.keys()) {
      if (!activeIds.has(id)) {
        queueGroupsMap.value.delete(id)
        console.info('[Queues] Removed stale queue %d from content map', id)
      }
    }
  } catch (err) {
    if (!isStartupRequiredError(err)) {
      console.error('[Queues] Error polling queue list:', err)
    }
  }
}

// ---- Polling: all queue contents (every 5s) ----
const { start: startQueueContentsPolling } = usePolling(pollAllQueueContents)

async function pollAllQueueContents() {
  const queues = allQueues.value
  if (queues.length === 0) return

  await Promise.all(queues.map(q => fetchQueueContents(q.queueId)))
}

async function fetchQueueContents(queueId: number) {
  try {
    const response = await fetchBackend(`/api/merge-queues/${queueId}`)
    if (response.status === 404) {
      queueGroupsMap.value.delete(queueId)
      console.info('[Queues] Queue %d no longer exists, removed from map', queueId)
      return
    }
    if (!response.ok) {
      console.warn('[Queues] Failed to fetch queue %d contents, status', queueId, response.status)
      return
    }
    const data = await response.json() as MergeGroup[]
    queueGroupsMap.value.set(queueId, data)
  } catch (err) {
    if (!isStartupRequiredError(err)) {
      console.error('[Queues] Error fetching queue %d contents:', queueId, err)
    }
  }
}

function openMergeGroupDetails(group: MergeGroup) {
  void router.push({
    name: 'merge-group-details',
    params: { mergeGroupId: group.id.toString() },
    query: { title: group.name }
  })
}

onMounted(() => {
  startQueueListPolling()
  startQueueContentsPolling()
})
</script>

<style scoped>
.queues-toolbar {
  display: flex;
  align-items: center;
}

.queues-sections {
  display: flex;
  flex-direction: column;
  gap: 32px;
}

.queue-section-header {
  font-size: 0.75rem;
  font-weight: 600;
  color: rgba(var(--v-theme-on-surface), 0.6);
  text-transform: uppercase;
  letter-spacing: 0.08em;
  margin-bottom: 10px;
  padding-bottom: 6px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.08);
}
</style>
