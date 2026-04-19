<template>
  <v-app-bar color="primary" density="comfortable">
    <v-app-bar-title>
      <router-link to="/" class="d-flex align-center app-title-link text-white" style="text-decoration: none;" data-mergician-home-link>
        <v-icon icon="mdi-source-merge" class="mr-2" />
        Mergician
        <v-divider vertical class="mx-4 title-divider" />
        <span class="page-title">{{ pageTitle }}</span>
      </router-link>
    </v-app-bar-title>

    <template v-slot:append>
      <div class="d-flex align-center">
        <span class="version-text mr-4">
          fe: {{ frontendVersion.slice(0, 7) }} | be: {{ backendVersion.slice(0, 7) }}
        </span>
        <template v-if="currentUser">
          <v-avatar v-if="currentUser.avatar_url" size="32" class="mr-2">
            <v-img :src="currentUser.avatar_url" />
          </v-avatar>
          <span class="mr-4 text-body-2">{{ currentUser.name }}</span>
          <v-btn variant="outlined" size="small" @click="logout">
            Logout
          </v-btn>
        </template>
      </div>
    </template>

    <v-progress-linear
      v-if="appLoading"
      indeterminate
      color="white"
      height="3"
      class="app-bar-progress"
    />
  </v-app-bar>

</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { fetchBackend, isStartupRequiredError } from '@/composables/useBackendFetch'
import { useCurrentUser } from '@/composables/useCurrentUser'
import { useAppLoading } from '@/composables/useAppLoading'

const route = useRoute()
const frontendVersion = ref(__APP_VERSION__)
const backendVersion = ref('unknown')
const { currentUser, loadCurrentUser, clearCurrentUser } = useCurrentUser()
const { appLoading } = useAppLoading()
const pageTitle = computed(() => {
  if (route.name === 'merge-group-details') {
    const mergeGroupTitle = (route.query.title as string | undefined)?.trim()
    return mergeGroupTitle ? `Merge Group: ${mergeGroupTitle}` : 'Merge Group'
  }

  return (route.meta?.title as string) ?? ''
})

onMounted(async () => {
  try {
    await loadCurrentUser()
  } catch (loadError) {
    if (!isStartupRequiredError(loadError)) {
      console.error('[Mergician] Failed to load current user in the app bar', loadError)
    }

    return
  }

  // Fetch backend version
  try {
    const response = await fetchBackend('/api/version')
    if (response.ok) {
      const data = await response.json() as { version?: string }
      backendVersion.value = data.version ?? 'unknown'
    }
  } catch (versionError) {
    if (!isStartupRequiredError(versionError)) {
      console.warn('[Mergician] Could not fetch backend version', versionError)
    }
  }
})

async function logout() {
  try {
    await fetchBackend('/api/auth/logout', { method: 'POST' })
  } catch (logoutError) {
    if (isStartupRequiredError(logoutError)) {
      return
    }

    console.error('[Mergician] Logout failed', logoutError)
    return
  }

  clearCurrentUser()
  window.location.href = '/'
}
</script>

<style scoped>
.app-title-link {
  cursor: pointer;
}

.version-text {
  font-size: 0.65rem;
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

.app-bar-progress {
  position: absolute;
  top: auto !important;
  bottom: 0;
  left: 0;
  right: 0;
}

</style>
