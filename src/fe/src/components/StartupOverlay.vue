<template>
  <v-overlay :model-value="true" persistent class="startup-overlay">
    <v-card
      class="startup-card"
      :color="isError ? 'error' : 'primary'"
      min-width="380"
      max-width="520"
      rounded="lg"
      elevation="12"
    >
      <v-card-text class="pa-8">
        <div class="d-flex flex-column align-center text-center">
          <!-- Loading state: blue theme with spinner -->
          <template v-if="!isError">
            <v-progress-circular
              indeterminate
              color="white"
              size="56"
              width="5"
              class="mb-6"
            />
            <div class="text-h6 text-white font-weight-medium">{{ message }}</div>
          </template>

          <!-- Error state: red theme with error icon -->
          <template v-else>
            <v-icon icon="mdi-alert-circle-outline" color="white" size="56" class="mb-4" />
            <div class="text-h6 text-white font-weight-medium mb-3">{{ message }}</div>
            <v-divider color="white" opacity="0.3" class="w-100 mb-3" />
            <div class="text-body-2 text-white" style="opacity: 0.9">{{ error }}</div>
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
}>()

const isError = computed(() => props.error !== null)
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
