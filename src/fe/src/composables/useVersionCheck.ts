import { ref, onMounted, onUnmounted } from 'vue'

declare const __APP_VERSION__: string

/**
 * Periodically checks /version.json for a new build hash.
 * When the deployed version differs from the running version,
 * sets `updateAvailable` to true so the UI can prompt the user to reload.
 *
 * This avoids false positives because:
 * - In dev mode (__APP_VERSION__ is 'dev'), version checking is disabled.
 * - The check only fires after a successful fetch with a valid JSON body.
 * - It compares against the build hash baked in at compile time.
 */
export function useVersionCheck(intervalMs = 60_000) {
  const updateAvailable = ref(false)
  let timer: ReturnType<typeof setInterval> | null = null

  const currentVersion = typeof __APP_VERSION__ !== 'undefined' ? __APP_VERSION__ : 'dev'

  async function check() {
    if (currentVersion === 'dev') return

    try {
      const res = await fetch('/version.json', { cache: 'no-store' })
      if (!res.ok) return

      const data = await res.json()
      if (data.version && data.version !== currentVersion) {
        console.info(`[Mergician] New version detected: ${data.version} (running: ${currentVersion})`)
        updateAvailable.value = true
        // Stop polling once we know there's an update
        if (timer) {
          clearInterval(timer)
          timer = null
        }
      }
    } catch {
      // Network errors are silently ignored — will retry next interval
    }
  }

  onMounted(() => {
    // Delay first check so it doesn't interfere with initial page load
    setTimeout(check, 5_000)
    timer = setInterval(check, intervalMs)
  })

  onUnmounted(() => {
    if (timer) {
      clearInterval(timer)
      timer = null
    }
  })

  function reload() {
    window.location.reload()
  }

  return { updateAvailable, reload }
}
