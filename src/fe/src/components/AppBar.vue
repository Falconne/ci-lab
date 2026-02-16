<template>
  <v-app-bar color="primary" density="comfortable">
    <v-app-bar-title>
      <v-icon icon="mdi-source-merge" class="mr-2" />
      Mergician
      <span class="version-text ml-3">
        fe: {{ frontendVersion.slice(0, 7) }} | be: {{ backendVersion.slice(0, 7) }}
      </span>
    </v-app-bar-title>

    <template v-slot:append>
      <div v-if="user" class="d-flex align-center">
        <v-avatar v-if="user.avatar_url" size="32" class="mr-2">
          <v-img :src="user.avatar_url" />
        </v-avatar>
        <span class="mr-4 text-body-2">{{ user.name }}</span>
        <v-btn variant="outlined" size="small" @click="logout">
          Logout
        </v-btn>
      </div>
    </template>
  </v-app-bar>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'

interface User {
  id: number
  username: string
  name: string
  avatar_url: string
}

const user = ref<User | null>(null)
const frontendVersion = ref(__APP_VERSION__)
const backendVersion = ref('unknown')

onMounted(async () => {
  try {
    const response = await fetch('/api/auth/me')
    if (response.ok) {
      user.value = await response.json()
    }
  } catch {
    // Not logged in
  }

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
  user.value = null
  window.location.href = '/'
}
</script>

<style scoped>
.version-text {
  font-size: 0.75rem;
  opacity: 0.7;
  font-weight: normal;
}
</style>
