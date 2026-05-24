<template>
  <v-container fluid class="px-6">
    <v-row class="mt-4">
      <v-col cols="12">
        <h1 class="text-h5 mb-2">Admin</h1>
        <p class="text-body-2 text-medium-emphasis mb-6">
          Manage monitored GitLab projects. Merge requests with the
          <strong>AutoMerge</strong> label in these projects will have auto merge
          automatically enabled on their corresponding merge groups.
        </p>

        <v-alert
          v-if="errorMessage"
          type="error"
          variant="tonal"
          closable
          class="mb-4"
          @click:close="errorMessage = ''"
        >
          {{ errorMessage }}
        </v-alert>

        <div class="d-flex align-center justify-space-between mb-4">
          <h2 class="text-subtitle-1 font-weight-medium">Monitored Projects</h2>
          <v-btn
            color="primary"
            variant="tonal"
            size="small"
            prepend-icon="mdi-plus"
            class="text-none"
            @click="openAddDialog"
          >
            Add Project
          </v-btn>
        </div>

        <div v-if="loading" class="text-center pa-8">
          <p class="text-body-1 text-grey">Loading...</p>
        </div>

        <div v-else-if="projects.length === 0" class="text-center pa-8">
          <v-icon icon="mdi-eye-off-outline" size="64" color="grey" class="mb-4" />
          <p class="text-h6 text-grey">No monitored projects</p>
          <p class="text-body-2 text-grey mt-2">
            Add a GitLab project ID to start monitoring it for the AutoMerge label.
          </p>
        </div>

        <v-table v-else density="compact" class="monitored-projects-table">
          <thead>
            <tr>
              <th>Project ID</th>
              <th>Project Name</th>
              <th class="text-right">Actions</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="project in projects" :key="project.id">
              <td>{{ project.projectId }}</td>
              <td>{{ project.projectName }}</td>
              <td class="text-right">
                <v-btn
                  variant="text"
                  size="small"
                  color="error"
                  icon="mdi-delete-outline"
                  :loading="deletingId === project.projectId"
                  @click="removeProject(project.projectId)"
                />
              </td>
            </tr>
          </tbody>
        </v-table>
      </v-col>
    </v-row>
  </v-container>

  <!-- Add Project Dialog -->
  <v-dialog v-model="showAddDialog" max-width="480" persistent>
    <v-card>
      <v-card-title class="text-subtitle-1 pt-5 px-5">Add Monitored Project</v-card-title>
      <v-card-text class="px-5">
        <p class="text-body-2 text-medium-emphasis mb-4">
          Enter the numeric GitLab project ID. You can find it on the project's main page.
        </p>
        <v-text-field
          v-model="newProjectIdInput"
          label="GitLab Project ID"
          type="number"
          placeholder="e.g. 42"
          variant="outlined"
          density="compact"
          :error-messages="addError"
          :disabled="addLoading"
          autofocus
          @keyup.enter="submitAdd"
        />
      </v-card-text>
      <v-card-actions class="px-5 pb-4">
        <v-spacer />
        <v-btn variant="text" :disabled="addLoading" @click="closeAddDialog">Cancel</v-btn>
        <v-btn
          color="primary"
          :loading="addLoading"
          :disabled="!newProjectIdInput.trim()"
          @click="submitAdd"
        >
          Add
        </v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { fetchBackend, isStartupRequiredError } from '@/composables/useBackendFetch'
import { extractBackendError } from '@/utils/errorHelpers'

interface MonitoredProject {
  id: number
  projectId: number
  projectName: string
}

const projects = ref<MonitoredProject[]>([])
const loading = ref(true)
const errorMessage = ref('')
const deletingId = ref<number | null>(null)

const showAddDialog = ref(false)
const newProjectIdInput = ref('')
const addError = ref('')
const addLoading = ref(false)

async function loadProjects() {
  loading.value = true
  try {
    const response = await fetchBackend('/api/admin/monitored-projects')
    if (response.ok) {
      projects.value = await response.json() as MonitoredProject[]
    } else {
      errorMessage.value = await extractBackendError(response, 'Failed to load monitored projects')
    }
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('Failed to load monitored projects:', err)
    errorMessage.value = 'Failed to load monitored projects.'
  } finally {
    loading.value = false
  }
}

function openAddDialog() {
  newProjectIdInput.value = ''
  addError.value = ''
  showAddDialog.value = true
}

function closeAddDialog() {
  showAddDialog.value = false
  newProjectIdInput.value = ''
  addError.value = ''
}

async function submitAdd() {
  const projectId = parseInt(newProjectIdInput.value, 10)
  if (!newProjectIdInput.value.trim() || isNaN(projectId) || projectId <= 0) {
    addError.value = 'Please enter a valid positive project ID.'
    return
  }

  addLoading.value = true
  addError.value = ''

  try {
    const response = await fetchBackend('/api/admin/monitored-projects', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ projectId })
    })

    if (response.ok) {
      closeAddDialog()
      await loadProjects()
    } else {
      addError.value = await extractBackendError(response, 'Failed to add project')
    }
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('Failed to add monitored project:', err)
    addError.value = 'Failed to add project. Please try again.'
  } finally {
    addLoading.value = false
  }
}

async function removeProject(projectId: number) {
  deletingId.value = projectId
  errorMessage.value = ''

  try {
    const response = await fetchBackend(`/api/admin/monitored-projects/${projectId}`, {
      method: 'DELETE'
    })

    if (response.ok || response.status === 204) {
      projects.value = projects.value.filter(p => p.projectId !== projectId)
    } else {
      errorMessage.value = await extractBackendError(response, 'Failed to remove project')
    }
  } catch (err) {
    if (isStartupRequiredError(err)) return
    console.error('Failed to remove monitored project:', err)
    errorMessage.value = 'Failed to remove project.'
  } finally {
    deletingId.value = null
  }
}

onMounted(() => {
  loadProjects()
})
</script>

<style scoped>
.monitored-projects-table {
  border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  border-radius: 8px;
}
</style>
