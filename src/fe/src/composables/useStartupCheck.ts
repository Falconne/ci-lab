import { readonly, ref } from 'vue'
import router from '@/router'

export interface StartupStatus {
  isReady: boolean
  message: string
  error: string | null
  isGitLabRecovery?: boolean
}

interface EnterStartupOptions {
  restartDetected?: boolean
}

const STARTUP_POLL_INTERVAL_MS = 3000

const isReady = ref(false)
const message = ref('Starting up...')
const error = ref<string | null>(null)
const isGitLabRecovery = ref(false)

let timer: ReturnType<typeof setInterval> | null = null
let checkPromise: Promise<void> | null = null

/**
 * Tracks the backend startup state for the whole SPA.
 * Polling runs until startup completes, and can resume later if a restart is detected.
 */
function stopPolling() {
  if (timer) {
    clearInterval(timer)
    timer = null
  }
}

function ensurePolling() {
  if (timer) {
    return
  }

  timer = setInterval(() => {
    void refreshStartupStatus()
  }, STARTUP_POLL_INTERVAL_MS)
}

function applyStatus(status: StartupStatus, options: EnterStartupOptions = {}) {
  const wasReady = isReady.value

  isReady.value = status.isReady
  message.value = status.message
  error.value = status.error ?? null
  isGitLabRecovery.value = status.isGitLabRecovery ?? false

  if (status.isReady) {
    if (!wasReady) {
      console.info('[Mergician] Application startup complete')
    }

    stopPolling()
    return
  }

  ensurePolling()

  if (options.restartDetected || wasReady) {
    console.warn('[Mergician] Startup resumed while the app was running; returning to dashboard')

    if (router.currentRoute.value.name !== 'home') {
      void router.replace({ name: 'home' }).catch((navigationError) => {
        console.warn('[Mergician] Failed to navigate back to the dashboard during restart', navigationError)
      })
    }
  }
}

export function enterStartupMode(
  status: Partial<StartupStatus> = {},
  options: EnterStartupOptions = {}
) {
  applyStatus(
    {
      isReady: false,
      message: status.message ?? 'Waiting for Mergician to start...',
      error: status.error ?? null,
      isGitLabRecovery: status.isGitLabRecovery ?? false,
    },
    options
  )
}

export async function refreshStartupStatus() {
  if (checkPromise) {
    return checkPromise
  }

  checkPromise = (async () => {
    try {
      const response = await fetch('/api/startup/status', { cache: 'no-store' })
      if (!response.ok) {
        throw new Error(`Startup status request failed with status ${response.status}`)
      }

      const data: StartupStatus = await response.json()
      applyStatus(data)
    } catch (startupError) {
      console.warn('[Mergician] Startup check failed, assuming the app is still starting', startupError)

      // If we were already in GitLab recovery mode, preserve that state rather than
      // downgrading to a generic "starting" message. The backend is simply slow to
      // respond because all threads are failing against an unreachable GitLab.
      if (isGitLabRecovery.value) {
        console.info('[Mergician] Preserving GitLab recovery state during poll failure')
      } else {
        enterStartupMode({ message: 'Waiting for Mergician to start...', error: null })
      }
    } finally {
      checkPromise = null
    }
  })()

  return checkPromise
}

export function useStartupCheck() {
  async function startMonitoring() {
    ensurePolling()
    await refreshStartupStatus()
  }

  return {
    isReady: readonly(isReady),
    message: readonly(message),
    error: readonly(error),
    isGitLabRecovery: readonly(isGitLabRecovery),
    startMonitoring,
  }
}

export function isStartupReady() {
  return isReady.value
}
