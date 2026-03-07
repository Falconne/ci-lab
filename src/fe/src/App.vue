<template>
  <v-app>
    <!-- Startup overlay blocks the UI until the application is fully initialized -->
    <StartupOverlay v-if="!isReady" :message="message" :error="error" />

    <!-- Normal layout shown once startup is complete -->
    <template v-else>
      <AppBar />
      <v-main>
        <router-view v-slot="{ Component, route }">
          <transition name="page-transition" mode="out-in">
            <component :is="Component" :key="route.fullPath" />
          </transition>
        </router-view>
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
    </template>
  </v-app>
</template>

<script setup lang="ts">
import { onMounted } from 'vue'
import AppBar from '@/components/AppBar.vue'
import StartupOverlay from '@/components/StartupOverlay.vue'
import { useVersionCheck } from '@/composables/useVersionCheck'
import { useStartupCheck } from '@/composables/useStartupCheck'

const { updateAvailable, reload } = useVersionCheck()
const { isReady, message, error, startMonitoring } = useStartupCheck()

onMounted(() => {
  void startMonitoring()
})
</script>

<style scoped>
.page-transition-enter-active,
.page-transition-leave-active {
  transition: opacity 0.2s ease, transform 0.2s ease;
}

.page-transition-enter-from {
  opacity: 0;
  transform: translateY(8px);
}

.page-transition-leave-to {
  opacity: 0;
  transform: translateY(-8px);
}
</style>
