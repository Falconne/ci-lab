<template>
  <v-app-bar color="primary" density="comfortable">
    <v-app-bar-title class="app-bar-title">
      <div class="d-flex align-center">
        <v-icon icon="mdi-source-merge" class="mr-2" />
        Mergician
        <span class="version-text ml-3">
          fe: {{ frontendVersion.slice(0, 7) }} | be: {{ backendVersion.slice(0, 7) }}
        </span>
        <div class="d-flex align-center ml-3 page-title-zone">
          <span class="title-divider" />
          <span class="page-title-text ml-3">{{ pageTitle }}</span>
        </div>
      </div>
    </v-app-bar-title>

    <template v-slot:append>
      <div v-if="currentUser" class="d-flex align-center">
        <v-avatar v-if="currentUser.avatar_url" size="32" class="mr-2">
          <v-img :src="currentUser.avatar_url" />
        </v-avatar>
        <span class="mr-4 text-body-2">{{ currentUser.name }}</span>
        <v-btn variant="outlined" size="small" @click="logout">
          Logout
        </v-btn>
      </div>
    </template>
  </v-app-bar>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { useCurrentUser } from '@/composables/useCurrentUser'

const frontendVersion = ref(__APP_VERSION__)
const backendVersion = ref('unknown')
const route = useRoute()
const { currentUser, loadCurrentUser, clearCurrentUser } = useCurrentUser()

const pageTitle = computed(() => {
  return typeof route.meta.title === 'string' ? route.meta.title : ''
})

onMounted(async () => {
  await loadCurrentUser()

  // Fetch backend version
  try {
    const response = await fetch('/api/version')
    if (response.ok) {
      const data = await response.json()
      backendVersion.value = data.version || 'unknown'
    }
  } catch {
    // Could not fetch backend version
  }
})

async function logout() {
  await fetch('/api/auth/logout', { method: 'POST' })
  clearCurrentUser()
  window.location.href = '/'
}
</script>

<style scoped>
.app-bar-title {
  overflow: visible;
}

.version-text {
  font-size: 0.75rem;
  opacity: 0.7;
  font-weight: normal;
}

.page-title-zone {
  min-width: 0;
}

.title-divider {
  border-left: 1px solid rgba(var(--v-theme-on-primary), 0.35);
  height: 1.25rem;
}

.page-title-text {
  font-size: 0.95rem;
  font-weight: 500;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 18rem;
}
</style>
