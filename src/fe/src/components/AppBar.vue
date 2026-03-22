<template>
  <v-app-bar color="primary" density="comfortable">
    <v-app-bar-title>
      <div class="d-flex align-center app-title-link" @click="goHome" data-mergician-home-link>
        <v-icon icon="mdi-source-merge" class="mr-2" />
        Mergician
        <v-divider vertical class="mx-4 title-divider" />
        <span class="page-title">{{ pageTitle }}</span>
      </div>
    </v-app-bar-title>

    <template v-slot:append>
      <div class="d-flex align-center">
        <v-btn
          v-if="currentUser"
          variant="text"
          size="small"
          prepend-icon="mdi-magnify"
          class="text-none find-mr-btn mr-2"
          @click="showFindMrDialog = true"
        >
          Find by Merge Request...
        </v-btn>
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

  <!-- Find by Merge Request dialog -->
  <v-dialog v-model="showFindMrDialog" max-width="520" persistent>
    <v-card>
      <v-card-title>Find by Merge Request</v-card-title>
      <v-card-text>
        <p class="text-body-2 mb-3">
          Enter the URL of a GitLab merge request to navigate to its merge group.
          If no merge group exists yet, one will be created.
        </p>
        <v-text-field
          v-model="findMrUrl"
          label="Merge Request URL"
          placeholder="https://gitlab.example.com/group/project/-/merge_requests/123"
          variant="outlined"
          density="compact"
          :error-messages="findMrError"
          :disabled="findMrLoading"
          autofocus
          @keyup.enter="submitFindMr"
        />
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn variant="text" :disabled="findMrLoading" @click="closeFindMrDialog">Cancel</v-btn>
        <v-btn color="primary" :loading="findMrLoading" :disabled="!findMrUrl.trim()" @click="submitFindMr">Find</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { fetchBackend, isStartupRequiredError } from '@/composables/useBackendFetch'
import { useCurrentUser } from '@/composables/useCurrentUser'
import { useAppLoading } from '@/composables/useAppLoading'

const route = useRoute()
const router = useRouter()
const frontendVersion = ref(__APP_VERSION__)
const backendVersion = ref('unknown')
const { currentUser, loadCurrentUser, clearCurrentUser } = useCurrentUser()
const { appLoading } = useAppLoading()
const showFindMrDialog = ref(false)
const findMrUrl = ref('')
const findMrError = ref('')
const findMrLoading = ref(false)
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
      const data = await response.json()
      backendVersion.value = data.version || 'unknown'
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

function goHome() {
  router.push('/')
}

// --- Find by Merge Request dialog ---

function closeFindMrDialog() {
  showFindMrDialog.value = false
  findMrUrl.value = ''
  findMrError.value = ''
}

async function submitFindMr() {
  if (!findMrUrl.value.trim()) return

  findMrLoading.value = true
  findMrError.value = ''

  try {
    const response = await fetchBackend('/api/merge-groups/find-by-merge-request', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mergeRequestUrl: findMrUrl.value.trim() })
    })

    if (response.ok) {
      const data = await response.json()
      closeFindMrDialog()
      router.push(`/merge-group/${data.mergeGroupId}`)
    } else {
      const data = await response.json().catch(() => null)
      findMrError.value = data?.error || `Request failed with status ${response.status}`
    }
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('[Mergician] Find by merge request failed:', err)
    findMrError.value = 'Failed to find merge request. Please try again.'
  } finally {
    findMrLoading.value = false
  }
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

.find-mr-btn {
  font-size: 0.8rem;
}
</style>
