<template>
  <v-app-bar color="primary" density="comfortable">
    <v-app-bar-title>
      <v-icon icon="mdi-source-merge" class="mr-2" />
      Mergician
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

onMounted(async () => {
  try {
    const response = await fetch('/api/auth/me')
    if (response.ok) {
      user.value = await response.json()
    }
  } catch {
    // Not logged in
  }
})

async function logout() {
  await fetch('/api/auth/logout', { method: 'POST' })
  user.value = null
  window.location.href = '/'
}
</script>
