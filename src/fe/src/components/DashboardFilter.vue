<template>
  <div class="filter-row">
    <v-text-field
      :model-value="modelValue"
      prepend-inner-icon="mdi-magnify"
      label="Filter by branch name or MR URL"
      variant="outlined"
      density="compact"
      clearable
      hide-details
      @update:model-value="emit('update:modelValue', $event ?? '')"
    >
      <template #append-inner>
        <v-tooltip text="Paste from clipboard" location="top">
          <template #activator="{ props }">
            <v-btn
              v-bind="props"
              icon
              size="x-small"
              variant="text"
              color="grey"
              class="paste-btn"
              aria-label="Paste from clipboard"
              @click="pasteFromClipboard"
            >
              <v-icon size="18">mdi-content-paste</v-icon>
            </v-btn>
          </template>
        </v-tooltip>
      </template>
    </v-text-field>

    <v-btn
      v-if="showOpenMrButton"
      color="primary"
      variant="flat"
      size="small"
      prepend-icon="mdi-open-in-app"
      class="ml-2 text-none open-mr-btn"
      :loading="openMrLoading"
      :disabled="openMrLoading"
      @click="openMrAsGroup"
    >
      Open MR as Merge Group
    </v-btn>
  </div>

  <v-alert
    v-if="openMrError"
    type="error"
    variant="tonal"
    closable
    class="mt-2"
    @click:close="openMrError = ''"
  >
    {{ openMrError }}
  </v-alert>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { fetchBackend, isStartupRequiredError } from '@/composables/useBackendFetch'

const props = defineProps<{
  modelValue: string
  showOpenMrButton: boolean
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const router = useRouter()
const openMrLoading = ref(false)
const openMrError = ref('')

async function pasteFromClipboard() {
  try {
    const text = await navigator.clipboard.readText()
    emit('update:modelValue', text)
  } catch (err) {
    console.warn('[Mergician] Failed to read from clipboard:', err)
    openMrError.value = 'Could not read from clipboard. Please paste manually.'
  }
}

async function openMrAsGroup() {
  const url = props.modelValue.trim()
  if (!url) return

  openMrLoading.value = true
  openMrError.value = ''

  try {
    const response = await fetchBackend('/api/merge-groups/find-by-merge-request', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mergeRequestUrl: url })
    })

    if (response.ok) {
      const data = await response.json() as { mergeGroupId?: number }
      if (!data.mergeGroupId) {
        openMrError.value = 'Unexpected response: missing mergeGroupId'
        return
      }
      emit('update:modelValue', '')
      router.push(`/merge-group/${data.mergeGroupId}`)
    } else {
      const data = await response.json().catch(() => null)
      openMrError.value = data?.error || `Request failed with status ${response.status}`
    }
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('[Mergician] Open MR as merge group failed:', err)
    openMrError.value = 'Failed to find merge request. Please try again.'
  } finally {
    openMrLoading.value = false
  }
}
</script>

<style scoped>
.filter-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.filter-row .v-text-field {
  flex: 1;
}

.open-mr-btn {
  flex-shrink: 0;
}
</style>
