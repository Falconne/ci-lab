import { ref, onMounted, onUnmounted } from 'vue'

/**
 * Returns a reactive `now` ref that updates at the given interval.
 *
 * Must be called during component setup so that lifecycle hooks are registered.
 */
export function useNow(intervalMs = 10_000) {
  const now = ref(Date.now())
  let timer: ReturnType<typeof setInterval> | null = null

  onMounted(() => {
    timer = setInterval(() => { now.value = Date.now() }, intervalMs)
  })
  onUnmounted(() => {
    if (timer) clearInterval(timer)
  })

  return now
}
