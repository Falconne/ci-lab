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

        <!-- Queue selector -->
        <div class="queue-selector-row mb-6">
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
            :no-data-text="queuesLoaded ? 'No active queues' : 'Loading queues...'"
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

        <!-- No queue selected -->
        <div v-if="selectedQueueId == null" class="text-center pa-8">
          <v-icon icon="mdi-playlist-play" size="64" color="grey" class="mb-4" />
          <p class="text-h6 text-grey">Select a queue to view and manage its merge groups</p>
          <p v-if="queuesLoaded && queueItems.length === 0" class="text-body-2 text-grey mt-2">
            No queues are active. Queues are created automatically when merge groups are set to
            Auto Merge + Auto Rebase with no blocking conditions.
          </p>
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
            <p class="text-body-2 text-grey mb-4">
              Drag to reorder. The first merge group in the queue is rebased and merged next.
            </p>

            <draggable
              v-model="queueGroups"
              item-key="id"
              handle=".drag-handle"
              ghost-class="drag-ghost"
              animation="200"
              @end="onDragEnd"
            >
              <template #item="{ element, index }">
                <div class="queue-entry">
                  <div class="drag-handle">
                    <v-icon icon="mdi-drag-vertical" size="20" color="grey" />
                  </div>
                  <div class="position-badge" :class="index === 0 ? 'position-first' : ''">
                    {{ index === 0 ? '▶' : `#${index + 1}` }}
                  </div>
                  <div class="queue-card-wrapper">
                    <MergeGroupCard
                      :group="element"
                      :now="now"
                      @navigate="openMergeGroupDetails"
                    />
                  </div>
                </div>
              </template>
            </draggable>
          </div>
        </template>
      </v-col>
    </v-row>
  </v-container>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import draggable from 'vuedraggable'
import { fetchBackend, isStartupRequiredError } from '@/composables/useBackendFetch'
import { usePolling } from '@/composables/usePolling'
import { useNow } from '@/composables/useNow'
import type { MergeGroup } from '@/types/mergeGroup'
import MergeGroupCard from '@/components/MergeGroupCard.vue'

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
const reorderPending = ref(false)

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

    // If selected queue was merged into another queue (id no longer exists), update selection
    if (selectedQueueId.value != null) {
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
  if (selectedQueueId.value == null || reorderPending.value) return

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

async function onDragEnd() {
  if (selectedQueueId.value == null) return
  const orderedIds = queueGroups.value.map(g => g.id)
  reorderPending.value = true
  try {
    const response = await fetchBackend(`/api/merge-queues/${selectedQueueId.value}/reorder`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mergeGroupIds: orderedIds })
    })
    if (!response.ok) {
      console.warn('[Queues] Reorder failed, status', response.status)
      errorMessage.value = 'Reorder failed. The list will refresh automatically.'
    }
  } catch (err) {
    console.error('[Queues] Error sending reorder request:', err)
    errorMessage.value = 'Reorder failed. The list will refresh automatically.'
  } finally {
    reorderPending.value = false
  }
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

.queue-entry {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  margin-bottom: 12px;
}

.drag-handle {
  cursor: grab;
  display: flex;
  align-items: center;
  padding-top: 18px;
  flex-shrink: 0;
}

.drag-handle:active {
  cursor: grabbing;
}

.position-badge {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  min-width: 32px;
  height: 32px;
  border-radius: 50%;
  background: rgba(var(--v-theme-on-surface), 0.08);
  font-size: 0.75rem;
  font-weight: 700;
  color: rgba(var(--v-theme-on-surface), 0.6);
  margin-top: 14px;
  flex-shrink: 0;
}

.position-first {
  background: rgba(var(--v-theme-primary), 0.15);
  color: rgb(var(--v-theme-primary));
}

.queue-card-wrapper {
  flex: 1;
  min-width: 0;
}

.drag-ghost {
  opacity: 0.4;
}
</style>
