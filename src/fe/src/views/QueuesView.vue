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

        <!-- No queues available -->
        <div v-if="queuesLoaded && queueItems.length === 0" class="text-center pa-8">
          <v-icon icon="mdi-playlist-play" size="64" color="grey" class="mb-4" />
          <p class="text-h6 text-grey">No queues are active</p>
          <p class="text-body-2 text-grey mt-2">
            Queues are created automatically when merge groups are set to
            Auto Merge + Auto Rebase with no blocking conditions.
          </p>
        </div>

        <!-- Queue selector (only shown when multiple queues exist) -->
        <div v-if="queueItems.length > 1" class="queue-selector-row mb-6">
          <v-autocomplete
            v-model="selectedQueueId"
            :items="queueItems"
            item-title="displayName"
            item-value="queueId"
            label="Select a queue"
            variant="outlined"
            density="compact"
            clearable
            hide-details
            class="queue-autocomplete"
            @update:model-value="onQueueSelected"
          >
            <template #item="{ item, props: itemProps }">
              <v-list-item v-bind="itemProps">
                <template #append>
                  <v-chip size="x-small" variant="tonal" color="primary">
                    {{ item.raw.entryCount }} {{ item.raw.entryCount === 1 ? 'group' : 'groups' }}
                  </v-chip>
                </template>
              </v-list-item>
            </template>
          </v-autocomplete>
        </div>

        <!-- Queue content -->
        <template v-else-if="selectedQueueId != null">
          <div v-if="queueGroupsLoading && queueGroups.length === 0" class="text-center pa-8">
            <p class="text-body-1 text-grey">Loading queue...</p>
          </div>

          <div v-else-if="queueGroups.length === 0 && !queueGroupsLoading" class="text-center pa-8">
            <v-icon icon="mdi-playlist-remove" size="64" color="grey" class="mb-4" />
            <p class="text-h6 text-grey">This queue is empty</p>
          </div>

          <div v-else>
            <MergeGroupGrid
              :sections="[{ title: '', groups: queueGroups }]"
              :now="now"
              @navigate="openMergeGroupDetails"
            />
          </div>
        </template>
      </v-col>
    </v-row>
  </v-container>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { fetchBackend, isStartupRequiredError } from '@/composables/useBackendFetch'
import { usePolling } from '@/composables/usePolling'
import { useNow } from '@/composables/useNow'
import type { MergeGroup } from '@/types/mergeGroup'
import MergeGroupGrid from '@/components/MergeGroupGrid.vue'

interface QueueSummary {
  queueId: number
  displayName: string
  entryCount: number
}

const route = useRoute()
const router = useRouter()
const now = useNow()

const allQueues = ref<QueueSummary[]>([])
const queuesLoaded = ref(false)
const selectedQueueId = ref<number | null>(null)
const queueGroups = ref<MergeGroup[]>([])
const queueGroupsLoading = ref(false)
const errorMessage = ref('')

const queueItems = computed(() => allQueues.value)

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

    if (selectedQueueId.value == null && data.length > 0) {
      // Auto-select first queue on initial load
      selectedQueueId.value = data[0].queueId
      await loadQueueGroups(data[0].queueId)
    } else if (selectedQueueId.value != null) {
      const stillExists = data.some(q => q.queueId === selectedQueueId.value)
      if (!stillExists && data.length > 0) {
        console.info(
          '[Queues] Selected queue %d no longer exists; switching to queue %d',
          selectedQueueId.value,
          data[0].queueId)
        selectedQueueId.value = data[0].queueId
        await loadQueueGroups(data[0].queueId)
      } else if (!stillExists) {
        selectedQueueId.value = null
        queueGroups.value = []
      }
    }
  } catch (err) {
    if (!isStartupRequiredError(err)) {
      console.error('[Queues] Error polling queue list:', err)
    }
  }
}

// ---- Polling: selected queue contents (every 5s) ----
const { start: startQueueContentsPolling } = usePolling(pollQueueContents)

async function pollQueueContents() {
  if (selectedQueueId.value == null) return

  try {
    const response = await fetchBackend(`/api/merge-queues/${selectedQueueId.value}`)
    if (response.status === 404) {
      // Queue was removed or split — handled by queue list polling
      return
    }
    if (!response.ok) {
      console.warn('[Queues] Failed to fetch queue %d contents, status', selectedQueueId.value, response.status)
      return
    }
    const data = await response.json() as MergeGroup[]
    queueGroups.value = data
    queueGroupsLoading.value = false
  } catch (err) {
    if (!isStartupRequiredError(err)) {
      console.error('[Queues] Error polling queue contents:', err)
    }
  }
}

async function loadQueueGroups(queueId: number) {
  queueGroupsLoading.value = true
  queueGroups.value = []
  try {
    const response = await fetchBackend(`/api/merge-queues/${queueId}`)
    if (!response.ok) {
      if (response.status !== 404) {
        errorMessage.value = `Failed to load queue (status ${response.status})`
      }
      queueGroupsLoading.value = false
      return
    }
    queueGroups.value = await response.json() as MergeGroup[]
  } catch (err) {
    console.error('[Queues] Error loading queue groups:', err)
    errorMessage.value = 'Failed to load queue contents'
  } finally {
    queueGroupsLoading.value = false
  }
}

function onQueueSelected(queueId: number | null) {
  if (queueId == null) {
    queueGroups.value = []
    router.replace({ name: 'queues' })
    return
  }
  router.replace({ name: 'queues', query: { queueId } })
  void loadQueueGroups(queueId)
}

function openMergeGroupDetails(group: MergeGroup) {
  void router.push({
    name: 'merge-group-details',
    params: { mergeGroupId: group.id.toString() },
    query: { title: group.name }
  })
}

onMounted(async () => {
  // Pre-select queue from query param
  const paramQueueId = route.query.queueId
  if (paramQueueId != null) {
    const id = Number(paramQueueId)
    if (!isNaN(id)) {
      selectedQueueId.value = id
      void loadQueueGroups(id)
    }
  }

  startQueueListPolling()
  startQueueContentsPolling()
})

// When route query changes (e.g., navigating from MG card link), update selection
watch(() => route.query.queueId, (newVal) => {
  if (newVal == null) return
  const id = Number(newVal)
  if (!isNaN(id) && id !== selectedQueueId.value) {
    selectedQueueId.value = id
    void loadQueueGroups(id)
  }
})
</script>

<style scoped>
.queue-selector-row {
  max-width: 600px;
}

.queue-autocomplete {
  min-width: 300px;
}
</style>
