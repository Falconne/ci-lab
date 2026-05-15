<template>
  <div class="filter-row">
    <v-text-field
      :model-value="modelValue"
      prepend-inner-icon="mdi-magnify"
      label="Filter by branch name or Merge Request URL"
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
      v-if="showOpenMRButton"
      color="primary"
      variant="flat"
      size="small"
      prepend-icon="mdi-open-in-app"
      class="ml-2 text-none open-mr-btn"
      :loading="openMRLoading"
      :disabled="openMRLoading"
      @click="openMRAsGroup"
    >
      Open MR as Merge Group
    </v-btn>
  </div>

  <v-alert
    v-if="openMRError"
    type="error"
    variant="tonal"
    closable
    class="mt-2"
    @click:close="openMRError = ''"
  >
    {{ openMRError }}
  </v-alert>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { fetchBackend, isStartupRequiredError } from '@/composables/useBackendFetch'
import { extractBackendError } from '@/utils/errorHelpers'

const props = defineProps<{
  modelValue: string
  showOpenMRButton: boolean
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const router = useRouter()
const openMRLoading = ref(false)
const openMRError = ref('')

async function pasteFromClipboard() {
  try {
    const text = await navigator.clipboard.readText()
    emit('update:modelValue', text)
  } catch (err) {
    console.warn('[Mergician] Failed to read from clipboard:', err)
    openMRError.value = 'Clipboard access was denied. Please type or paste the URL directly into the field.'
  }
}

async function openMRAsGroup() {
  const url = props.modelValue.trim()
  if (!url) return

  openMRLoading.value = true
  openMRError.value = ''

  try {
    const response = await fetchBackend('/api/merge-groups/find-by-merge-request', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mergeRequestUrl: url })
    })

    if (response.ok) {
      const data = await response.json() as { mergeGroupId?: number }
      if (!data.mergeGroupId) {
        openMRError.value = 'Unexpected response: missing mergeGroupId'
        return
      }
      emit('update:modelValue', '')
      router.push(`/merge-group/${data.mergeGroupId}`)
    } else {
      openMRError.value = await extractBackendError(response, 'Failed to open merge request')
    }
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('[Mergician] Open MR as merge group failed:', err)
    openMRError.value = 'Failed to find merge request. Please try again.'
  } finally {
    openMRLoading.value = false
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
