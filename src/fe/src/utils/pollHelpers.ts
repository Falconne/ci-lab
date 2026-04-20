import type { Ref } from 'vue'

export const TRANSIENT_DB_ERROR = 'Database is temporarily unavailable. Retrying...'

/**
 * Sets a transient "DB unavailable" error on the given ref if the response status is 503.
 * Returns true if handled (caller should return early), false otherwise.
 */
export function handleTransientError(errorRef: Ref<string>, status: number): boolean {
  if (status === 503) {
    errorRef.value = TRANSIENT_DB_ERROR
    return true
  }
  return false
}

/**
 * Clears the transient DB error message once a successful response arrives.
 */
export function clearTransientError(errorRef: Ref<string>) {
  if (errorRef.value === TRANSIENT_DB_ERROR) {
    errorRef.value = ''
  }
}
