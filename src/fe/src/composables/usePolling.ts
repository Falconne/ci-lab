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
 *
 * Uses recursive setTimeout so each poll starts only after the previous one completes,
 * preventing poll drift and timer accumulation under load.
 */
export function usePolling(pollFn: () => Promise<void>, options: UsePollingOptions = {}) {
  const {
    fastIntervalMs = 1000,
    normalIntervalMs = 5000,
    fastDurationMs = 5000,
  } = options

  const { setAppLoading } = useAppLoading()
  const initialPhase = ref(false)

  let running = false
  let active = true
  let startTime = 0

  async function loop() {
    if (!running || !active) return

    try {
      await pollFn()
    } catch (err) {
      console.error('[Mergician] Unexpected error in poll loop:', err)
    }

    if (!running || !active) return

    const elapsed = Date.now() - startTime
    if (initialPhase.value && elapsed >= fastDurationMs) {
      initialPhase.value = false
      setAppLoading(false)
    }

    const delay = initialPhase.value ? fastIntervalMs : normalIntervalMs
    setTimeout(loop, delay)
  }

  function start() {
    if (running) return
    running = true
    startTime = Date.now()
    initialPhase.value = true
    setAppLoading(true)
    loop()
  }

  function stop() {
    running = false
    initialPhase.value = false
    setAppLoading(false)
  }

  onUnmounted(() => {
    active = false
    stop()
  })

  return { initialPhase, start, stop }
}

