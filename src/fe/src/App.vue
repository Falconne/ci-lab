<template>
  <v-app>
    <AppBar />
    <v-main>
      <router-view />
    </v-main>

    <!-- Non-blocking banner shown when a new version is deployed -->
    <v-snackbar
      v-model="updateAvailable"
      :timeout="-1"
      color="info"
      location="bottom"
    >
      A new version of Mergician is available.
      <template v-slot:actions>
        <v-btn variant="text" @click="reload">
          Refresh
        </v-btn>
      </template>
    </v-snackbar>
  </v-app>
</template>

<script setup lang="ts">
import AppBar from '@/components/AppBar.vue'
import { useVersionCheck } from '@/composables/useVersionCheck'

const { updateAvailable, reload } = useVersionCheck()
</script>
