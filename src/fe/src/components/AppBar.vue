<template>
  <v-app-bar color="primary" density="comfortable">
    <v-app-bar-title>
      <div class="d-flex align-center">
        <v-icon icon="mdi-source-merge" class="mr-2" />
        Mergician
        <span class="version-text ml-3">
          fe: {{ frontendVersion.slice(0, 7) }} | be: {{ backendVersion.slice(0, 7) }}
        </span>
        <!-- Page title zone: populated by individual views via usePageTitle composable -->
        <template v-if="pageTitle">
          <v-divider vertical class="title-divider mx-3" />
          <span class="page-title-text">{{ pageTitle }}</span>
        </template>
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
import { ref, onMounted } from 'vue'
import { useCurrentUser } from '@/composables/useCurrentUser'
import { usePageTitle } from '@/composables/usePageTitle'

const frontendVersion = ref(__APP_VERSION__)
const backendVersion = ref('unknown')
const { currentUser, loadCurrentUser, clearCurrentUser } = useCurrentUser()
const { pageTitle } = usePageTitle()

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
.version-text {
  font-size: 0.75rem;
  opacity: 0.7;
  font-weight: normal;
}

.title-divider {
  height: 18px;
  opacity: 0.4;
  align-self: center;
}

.page-title-text {
  font-size: 0.9rem;
  font-weight: 500;
  opacity: 0.9;
  letter-spacing: 0.02em;
}
</style>
