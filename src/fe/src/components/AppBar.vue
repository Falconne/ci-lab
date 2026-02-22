<template>
  <v-app-bar color="primary" density="comfortable">
    <v-app-bar-title>
      <div class="d-flex align-center app-title-link" @click="goHome" data-mergician-home-link>
        <v-icon icon="mdi-source-merge" class="mr-2" />
        Mergician
        <span class="version-text ml-3">
          fe: {{ frontendVersion.slice(0, 7) }} | be: {{ backendVersion.slice(0, 7) }}
        </span>
        <v-divider vertical class="mx-4 title-divider" />
        <span class="page-title">{{ pageTitle }}</span>
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
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useCurrentUser } from '@/composables/useCurrentUser'

const route = useRoute()
const router = useRouter()
const frontendVersion = ref(__APP_VERSION__)
const backendVersion = ref('unknown')
const { currentUser, loadCurrentUser, clearCurrentUser } = useCurrentUser()
const pageTitle = computed(() => {
  if (route.name === 'merge-group-details') {
    const mergeGroupTitle = (route.query.title as string | undefined)?.trim()
    return mergeGroupTitle ? `Merge Group: ${mergeGroupTitle}` : 'Merge Group'
  }

  return (route.meta?.title as string) ?? ''
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

function goHome() {
  router.push('/')
}
</script>

<style scoped>
.app-title-link {
  cursor: pointer;
}

.version-text {
  font-size: 0.75rem;
  opacity: 0.7;
  font-weight: normal;
}

.title-divider {
  opacity: 0.4;
  height: 24px;
  align-self: center;
}

.page-title {
  font-size: 1rem;
  font-weight: 500;
  opacity: 0.95;
}
</style>
