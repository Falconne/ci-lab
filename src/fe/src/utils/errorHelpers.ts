export async function extractBackendError(response: Response, fallback: string): Promise<string> {
  try {
    const data = await response.json()
    if (data?.error) return data.error
  } catch {
    try {
      const text = (await response.text()).trim()
      if (text) return text
    } catch { /* ignore */ }
  }
  return `${fallback} (status ${response.status})`
}
