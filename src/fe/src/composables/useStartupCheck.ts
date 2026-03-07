import { ref, onMounted, onUnmounted } from 'vue'

interface StartupStatus {
  isReady: boolean
  message: string
  error: string | null
}

/**
 * Polls /api/startup/status every 3 seconds until the application is ready.
 * Exposes the current startup state for the loading overlay to display.
 * When the application becomes ready, polling stops automatically.
 */
export function useStartupCheck() {
  const isReady = ref(false)
  const message = ref('Starting up...')
  const error = ref<string | null>(null)
  let timer: ReturnType<typeof setInterval> | null = null

  async function check() {
    try {
      const response = await fetch('/api/startup/status')
      if (response.ok) {
        const data: StartupStatus = await response.json()
        isReady.value = data.isReady
        message.value = data.message
        error.value = data.error ?? null

        if (data.isReady) {
          console.info('[Mergician] Application startup complete')
          if (timer) {
            clearInterval(timer)
            timer = null
          }
        }
      }
    } catch (err) {
      console.error('[Mergician] Startup check failed:', err)
    }
  }

  onMounted(() => {
    void check()
    timer = setInterval(check, 3000)
  })

  onUnmounted(() => {
    if (timer) {
      clearInterval(timer)
      timer = null
    }
  })

  return { isReady, message, error }
}
