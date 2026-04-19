/**
 * Composable for managing the current authenticated user.
 *
 * State is declared at module scope so all consumers share the same
 * reactive refs. The loading promise is deduped so concurrent calls
 * don't issue parallel /api/auth/me requests.
 */
import { readonly, ref } from 'vue'
import { fetchBackend, isStartupRequiredError } from '@/composables/useBackendFetch'

export interface CurrentUser {
  id: number
  username: string
  name: string
  avatar_url: string
}

const currentUser = ref<CurrentUser | null>(null)
const loaded = ref(false)
let loadingPromise: Promise<CurrentUser | null> | null = null

async function loadCurrentUser(): Promise<CurrentUser | null> {
  if (loadingPromise) {
    return loadingPromise
  }

  if (loaded.value) {
    return currentUser.value
  }

  loadingPromise = (async () => {
    let startupRequired = false

    try {
      const response = await fetchBackend('/api/auth/me')
      if (response.ok) {
        currentUser.value = await response.json()
      } else if (response.status === 401) {
        currentUser.value = null
      } else {
        console.warn(`[Mergician] Unexpected /api/auth/me status: ${response.status}`)
        currentUser.value = null
      }
    } catch (err) {
      if (isStartupRequiredError(err)) {
        startupRequired = true
        throw err
      }

      console.error('[Mergician] Failed to load current user:', err)
      currentUser.value = null
    } finally {
      loadingPromise = null

      if (!startupRequired) {
        loaded.value = true
      }
    }

    return currentUser.value
  })()

  return loadingPromise
}

function clearCurrentUser() {
  currentUser.value = null
  loaded.value = true
}

export function useCurrentUser() {
  return {
    currentUser: readonly(currentUser),
    loaded: readonly(loaded),
    loadCurrentUser,
    clearCurrentUser
  }
}
