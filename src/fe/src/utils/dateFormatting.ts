/**
 * Formats an ISO datetime string to a locale-specific date/time string.
 */
export function formatDateTime(isoString: string): string {
  if (!isoString) return ''
  const date = new Date(isoString)
  if (isNaN(date.getTime())) return ''
  return date.toLocaleString()
}

/**
 * Returns a human-readable relative time string (e.g. "5 minutes ago").
 * @param nowMs - current timestamp in milliseconds, for reactivity.
 */
export function formatTimeAgo(isoString: string, nowMs: number): string {
  if (!isoString) return ''
  const date = new Date(isoString)
  if (isNaN(date.getTime())) return ''
  const diffMs = nowMs - date.getTime()
  const diffSec = Math.floor(diffMs / 1000)

  if (diffSec < 60) return diffSec === 1 ? '1 second ago' : `${diffSec} seconds ago`
  const diffMin = Math.floor(diffSec / 60)
  if (diffMin < 60) return diffMin === 1 ? '1 minute ago' : `${diffMin} minutes ago`
  const diffHour = Math.floor(diffMin / 60)
  if (diffHour < 24) return diffHour === 1 ? '1 hour ago' : `${diffHour} hours ago`
  const diffDay = Math.floor(diffHour / 24)
  return diffDay === 1 ? '1 day ago' : `${diffDay} days ago`
}
