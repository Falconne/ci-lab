import { ref, onUnmounted } from 'vue'
import { useAppLoading } from '@/composables/useAppLoading'

interface UsePollingOptions {
  fastIntervalMs?: number
  normalIntervalMs?: number
  fastDurationMs?: number
}

/**
 * Composable for polling a backend endpoint at a fast rate initially, then a slower rate.
 *
 * Must be called during component setup (not inside callbacks or conditionals)
 * so that the onUnmounted cleanup hook is properly registered.
 */
export function usePolling(pollFn: () => Promise<void>, options: UsePollingOptions = {}) {
  const {
    fastIntervalMs = 1000,
    normalIntervalMs = 5000,
    fastDurationMs = 5000,
  } = options

  const { setAppLoading } = useAppLoading()
  const initialPhase = ref(false)

  let pollIntervalId: ReturnType<typeof setInterval> | null = null
  let fastPollTimeoutId: ReturnType<typeof setTimeout> | null = null
  let pollInProgress = false

  async function guardedPoll() {
    if (pollInProgress) return
    pollInProgress = true
    try {
      await pollFn()
    } finally {
      pollInProgress = false
    }
  }

  function start() {
    if (pollIntervalId !== null) return
    initialPhase.value = true
    setAppLoading(true)
    pollIntervalId = setInterval(guardedPoll, fastIntervalMs)

    fastPollTimeoutId = setTimeout(() => {
      initialPhase.value = false
      setAppLoading(false)
      if (pollIntervalId !== null) {
        clearInterval(pollIntervalId)
        pollIntervalId = setInterval(guardedPoll, normalIntervalMs)
      }
      fastPollTimeoutId = null
    }, fastDurationMs)

    guardedPoll()
  }

  function stop() {
    if (pollIntervalId !== null) {
      clearInterval(pollIntervalId)
      pollIntervalId = null
    }
    if (fastPollTimeoutId !== null) {
      clearTimeout(fastPollTimeoutId)
      fastPollTimeoutId = null
    }
    setAppLoading(false)
  }

  onUnmounted(stop)

  return { initialPhase, start, stop }
}
