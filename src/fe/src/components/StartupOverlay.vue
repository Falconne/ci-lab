<template>
  <v-overlay :model-value="true" persistent class="startup-overlay">
    <v-card
      class="startup-card"
      :color="cardColor"
      min-width="380"
      max-width="520"
      rounded="lg"
      elevation="12"
    >
      <v-card-text class="pa-8">
        <div class="d-flex flex-column align-center text-center">
          <!-- GitLab recovery state: amber theme with connectivity icon -->
          <template v-if="isGitLabRecovery">
            <v-icon icon="mdi-server-network-off" color="white" size="56" class="mb-4" />
            <div class="text-h6 text-white font-weight-medium mb-3">Waiting for GitLab to recover</div>
            <v-divider color="white" opacity="0.3" class="w-100 mb-3" />
            <div class="text-body-2 text-white" style="opacity: 0.9">{{ error }}</div>
            <v-progress-linear
              indeterminate
              color="white"
              class="mt-4"
              rounded
              height="4"
            />
          </template>

          <!-- Error state: red theme with error icon -->
          <template v-else-if="isError">
            <v-icon icon="mdi-alert-circle-outline" color="white" size="56" class="mb-4" />
            <div class="text-h6 text-white font-weight-medium mb-3">{{ message }}</div>
            <v-divider color="white" opacity="0.3" class="w-100 mb-3" />
            <div class="text-body-2 text-white" style="opacity: 0.9">{{ error }}</div>
          </template>

          <!-- Loading state: blue theme with spinner -->
          <template v-else>
            <v-progress-circular
              indeterminate
              color="white"
              size="56"
              width="5"
              class="mb-6"
            />
            <div class="text-h6 text-white font-weight-medium">{{ message }}</div>
          </template>
        </div>
      </v-card-text>
    </v-card>
  </v-overlay>
</template>

<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{
  message: string
  error: string | null
  isGitLabRecovery: boolean
}>()

const isError = computed(() => props.error !== null)

const cardColor = computed(() => {
  if (props.isGitLabRecovery) return '#E65100' // deep orange for GitLab recovery
  if (isError.value) return 'error'
  return 'primary'
})
</script>

<style scoped>
.startup-overlay {
  display: flex;
  align-items: center;
  justify-content: center;
}

.startup-card {
  transition: background-color 0.3s ease;
}
</style>
