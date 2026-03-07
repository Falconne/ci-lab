import {
  enterStartupMode,
  isStartupReady,
  type StartupStatus,
} from '@/composables/useStartupCheck'

export class StartupRequiredError extends Error {
  constructor(message = 'Mergician is starting up') {
    super(message)
    this.name = 'StartupRequiredError'
  }
}

export function isStartupRequiredError(error: unknown): error is StartupRequiredError {
  return error instanceof StartupRequiredError
}

export async function fetchBackend(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  if (!isStartupReady()) {
    console.info('[Mergician] Skipping backend request because startup is still in progress', input)
    throw new StartupRequiredError()
  }

  try {
    const response = await fetch(input, init)

    if (response.status === 503) {
      const startupStatus = await tryReadStartupStatus(response.clone())
      if (startupStatus && !startupStatus.isReady) {
        enterStartupMode(startupStatus, { restartDetected: true })
        throw new StartupRequiredError()
      }
    }

    return response
  } catch (requestError) {
    if (requestError instanceof StartupRequiredError) {
      throw requestError
    }

    console.warn('[Mergician] Backend request failed; assuming restart is in progress', requestError)
    enterStartupMode({ message: 'Waiting for Mergician to restart...', error: null }, { restartDetected: true })
    throw new StartupRequiredError()
  }
}

async function tryReadStartupStatus(response: Response): Promise<StartupStatus | null> {
  try {
    const data = await response.json() as Partial<StartupStatus>
    if (typeof data.isReady !== 'boolean' || typeof data.message !== 'string') {
      return null
    }

    return {
      isReady: data.isReady,
      message: data.message,
      error: typeof data.error === 'string' ? data.error : null,
    }
  } catch {
    return null
  }
}