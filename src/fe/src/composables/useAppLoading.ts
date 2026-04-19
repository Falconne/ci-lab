import { ref } from 'vue'

/**
 * Shared reactive loading state for the app bar progress indicator.
 * Views set this during initial data loads; AppBar reads it to show
 * a thin indeterminate progress bar at the bottom of the top bar.
 *
 * State is declared at module scope so every call to useAppLoading()
 * returns the same reactive ref — effectively a singleton.
 */
const loading = ref(false)

export function useAppLoading() {
  return {
    appLoading: loading,
    setAppLoading(value: boolean) {
      loading.value = value
    }
  }
}
