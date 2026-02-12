import { ref, onMounted } from 'vue'

interface HealthStatus {
  status: string
  configurationErrors: string[]
  timestamp: string
}

/**
 * Checks /api/health on mount and exposes any configuration errors.
 * When configuration errors are present, the UI should block all
 * functionality and display the errors to the user.
 */
export function useHealthCheck() {
  const configError = ref(false)
  const configErrors = ref<string[]>([])
  const healthChecked = ref(false)

  onMounted(async () => {
    try {
      const response = await fetch('/api/health')
      if (response.ok) {
        const data: HealthStatus = await response.json()
        if (data.configurationErrors && data.configurationErrors.length > 0) {
          configError.value = true
          configErrors.value = data.configurationErrors
          console.error('[Mergician] Configuration errors detected:', data.configurationErrors)
        }
      }
    } catch (err) {
      console.error('[Mergician] Health check failed:', err)
    } finally {
      healthChecked.value = true
    }
  })

  return { configError, configErrors, healthChecked }
}
