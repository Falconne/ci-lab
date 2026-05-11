<template>
  <v-tabs
    :model-value="activeTab"
    bg-color="surface"
    color="primary"
    class="nav-tabs"
    density="compact"
  >
    <v-tab :to="{ name: 'home' }" value="home" class="nav-tab">
      <v-icon icon="mdi-view-dashboard-outline" size="18" class="mr-1" />
      My Merge Groups
    </v-tab>
    <v-tab :to="{ name: 'queues' }" value="queues" class="nav-tab">
      <v-icon icon="mdi-playlist-play" size="18" class="mr-1" />
      Queues
    </v-tab>
  </v-tabs>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'

const route = useRoute()

const activeTab = computed(() => {
  if (route.name === 'queues') return 'queues'
  return 'home'
})
</script>

<style scoped>
.nav-tabs {
  position: sticky;
  top: 0;
  z-index: 10;
  border-bottom: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}

.nav-tab {
  text-transform: none;
  font-size: 0.875rem;
  letter-spacing: 0;
}
</style>
