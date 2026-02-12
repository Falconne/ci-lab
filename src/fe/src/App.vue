<template>
  <v-app>
    <!-- Configuration error - blocks all functionality -->
    <v-main v-if="configError">
      <v-container>
        <v-row justify="center" align="center" style="min-height: 80vh;">
          <v-col cols="12" md="8" lg="6">
            <v-alert type="error" prominent border="start" class="mt-8">
              <v-alert-title>Configuration Error</v-alert-title>
              <div v-for="error in configErrors" :key="error" class="mt-2">
                {{ error }}
              </div>
              <div class="mt-4 text-body-2 text-medium-emphasis">
                Please configure the application and restart the server.
              </div>
            </v-alert>
          </v-col>
        </v-row>
      </v-container>
    </v-main>

    <!-- Normal layout (only shown after health check passes) -->
    <template v-else-if="healthChecked">
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
    </template>
  </v-app>
</template>

<script setup lang="ts">
import AppBar from '@/components/AppBar.vue'
import { useVersionCheck } from '@/composables/useVersionCheck'
import { useHealthCheck } from '@/composables/useHealthCheck'

const { updateAvailable, reload } = useVersionCheck()
const { configError, configErrors, healthChecked } = useHealthCheck()
</script>
