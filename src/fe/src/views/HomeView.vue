<template>
  <v-container>
    <v-row justify="center" class="mt-4">
      <v-col cols="12" md="10" lg="8">
        <div v-if="loading" class="text-center pa-8">
          <v-progress-circular indeterminate color="primary" size="48" />
          <p class="mt-4 text-body-1">Loading your activity...</p>
        </div>

        <div v-else-if="events.length === 0" class="text-center pa-8">
          <v-icon icon="mdi-calendar-blank" size="64" color="grey" class="mb-4" />
          <p class="text-h6 text-grey">No activity in the last 7 days</p>
        </div>

        <div v-else>
          <h2 class="text-h5 mb-4">Your GitLab Activity (Last 7 Days)</h2>
          <v-timeline density="compact" side="end">
            <v-timeline-item
              v-for="event in events"
              :key="event.id"
              :dot-color="getEventColor(event.actionName)"
              :icon="getEventIcon(event.actionName)"
              size="small"
            >
              <v-card variant="outlined" class="mb-2">
                <v-card-text>
                  <div class="d-flex justify-space-between align-center">
                    <div>
                      <strong>{{ formatAction(event) }}</strong>
                      <div v-if="event.projectName" class="text-caption text-grey">
                        {{ event.projectName }}
                      </div>
                    </div>
                    <v-chip size="small" variant="text" class="text-caption">
                      {{ formatDate(event.createdAt) }}
                    </v-chip>
                  </div>
                </v-card-text>
              </v-card>
            </v-timeline-item>
          </v-timeline>
        </div>
      </v-col>
    </v-row>
  </v-container>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'

interface PushData {
  commit_count: number
  ref: string | null
  ref_type: string | null
  action: string | null
}

interface ActivityEvent {
  id: number
  actionName: string
  targetType: string | null
  targetTitle: string | null
  createdAt: string
  pushData: PushData | null
  projectId: number
  projectName: string | null
}

const events = ref<ActivityEvent[]>([])
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/activity')
    if (response.status === 401) {
      window.location.href = '/api/auth/login'
      return
    }
    if (response.ok) {
      events.value = await response.json()
    }
  } catch (err) {
    console.error('Failed to load activity:', err)
  } finally {
    loading.value = false
  }
})

function getEventColor(action: string): string {
  switch (action) {
    case 'pushed to': case 'pushed new': return 'green'
    case 'opened': case 'created': return 'blue'
    case 'merged': case 'accepted': return 'purple'
    case 'closed': return 'red'
    case 'commented on': return 'orange'
    default: return 'grey'
  }
}

function getEventIcon(action: string): string {
  switch (action) {
    case 'pushed to': case 'pushed new': return 'mdi-source-commit'
    case 'opened': case 'created': return 'mdi-plus-circle'
    case 'merged': case 'accepted': return 'mdi-source-merge'
    case 'closed': return 'mdi-close-circle'
    case 'commented on': return 'mdi-comment'
    default: return 'mdi-circle'
  }
}

function formatAction(event: ActivityEvent): string {
  let text = event.actionName
  if (event.targetTitle) {
    text += ` "${event.targetTitle}"`
  }
  if (event.pushData?.ref) {
    text += ` branch ${event.pushData.ref}`
    if (event.pushData.commit_count > 0) {
      text += ` (${event.pushData.commit_count} commit${event.pushData.commit_count > 1 ? 's' : ''})`
    }
  }
  return text
}

function formatDate(dateStr: string): string {
  const d = new Date(dateStr)
  return d.toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit'
  })
}
</script>
