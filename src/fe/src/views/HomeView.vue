<template>
  <v-container>
    <v-row justify="center" class="mt-8">
      <v-col cols="12" md="8" lg="6">
        <v-card class="text-center pa-8" elevation="2">
          <v-icon icon="mdi-source-merge" size="64" color="primary" class="mb-4" />
          <v-card-title class="text-h4 mb-2">
            Hello, World!
          </v-card-title>
          <v-card-subtitle class="text-h6">
            Welcome to Mergician
          </v-card-subtitle>
          <v-card-text class="mt-4">
            <p class="text-body-1">
              Mergician helps you manage merge groups across multiple repositories.
            </p>
          </v-card-text>
          <v-card-actions class="justify-center">
            <v-chip color="success" variant="flat" prepend-icon="mdi-check-circle">
              Backend: {{ backendStatus }}
            </v-chip>
          </v-card-actions>
        </v-card>
      </v-col>
    </v-row>
  </v-container>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'

const backendStatus = ref('Checking...')

onMounted(async () => {
  try {
    const response = await fetch('/api/health')
    if (response.ok) {
      backendStatus.value = 'Connected'
    } else {
      backendStatus.value = 'Unreachable'
    }
  } catch {
    backendStatus.value = 'Unreachable'
  }
})
</script>
