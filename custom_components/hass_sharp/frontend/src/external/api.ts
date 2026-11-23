import type { HomeAssistant } from '../types'
import { connectionLostMessage } from '../entities/api'
import type { LibraryDocument, LibraryMutation } from '../visual/library-api'

/** A node library file or a definition already stored under an item's name: what an overwrite of it would replace. */
export interface ExternalExisting { id: string; revision: number; name: string }
/**
 * One item a repository's hass_sharp.toml lists: `existing` are the stored library files or entity definitions of
 * exactly its name, `problem` why its file cannot be imported (missing, too large, not an entity definition…).
 */
export interface ExternalItem {
  kind: 'library' | 'entity'
  name: string
  description: string
  path: string
  existing: ExternalExisting[]
  problem: string | null
}
/** `inspect`'s answer: the repository at the commit its address named (`ref` null for the default branch). */
export interface ExternalInspection { owner: string; repo: string; ref: string | null; sha: string; items: ExternalItem[] }
/**
 * An item to import, by its path. A library is `new` only when nothing of its name is stored, and then takes no
 * target; an overwrite names the stored file or definition and the revision inspect answered for it.
 */
export type ExternalPick =
  | { kind: ExternalItem['kind']; path: string; mode: 'new' }
  | { kind: ExternalItem['kind']; path: string; mode: 'overwrite'; target: string; expectedRevision: number }
/** What became of one picked item; `id` is where it is stored, `enabled` whether a definition is on (null for a library). */
export interface ExternalImportItem {
  kind: ExternalItem['kind']
  name: string
  path: string
  outcome: 'imported' | 'overwritten' | 'failed'
  id: string | null
  enabled: boolean | null
  message: string | null
}
/** `import`'s answer; `libraries` is the library import's own answer, null when none was picked or it was refused. */
export interface ExternalImportResult {
  owner: string
  repo: string
  sha: string
  items: ExternalImportItem[]
  libraries: (Omit<LibraryMutation, 'document' | 'deleted'> & { documents: LibraryDocument[] }) | null
}

type Envelope<T> = { version: 1; ok: true; result: T } | { version: 1; ok: false; error: { code: string; detail: unknown } }

/** A refused request: the backend's `{code, detail}`, or Home Assistant's own refusal; the message is the sentence. */
export class ExternalApiError extends Error {
  constructor(public code: string, detail: unknown) {
    super(typeof detail === 'string' ? detail : `External import request failed (${code}).`)
  }
}
/**
 * Home Assistant rejects a command with a plain value: `{code, message}`, a failed result `{error: {code, message}}`
 * (a lost connection), or the bare number 3 (`ERR_CONNECTION_LOST`).
 */
const homeAssistantError = (error: unknown) => {
  if (error === 3) return new ExternalApiError('3', connectionLostMessage)
  if (error === null || typeof error !== 'object' || error instanceof Error) return
  const found = error as { code?: unknown; message?: unknown; error?: { code?: unknown; message?: unknown } }
  const { code, message } = found.code !== undefined ? found : found.error ?? {}
  if (typeof code !== 'string' && typeof code !== 'number') return
  return new ExternalApiError(String(code), message)
}

/** The Add external dialog's websocket API (`hass_sharp/external`, administrators only). */
export function externalApi(hass: Pick<HomeAssistant, 'callWS'>) {
  const request = async <T>(operation: string, fields: object): Promise<T> => {
    let response: Envelope<T>
    try {
      response = await hass.callWS<Envelope<T>>({ type: 'hass_sharp/external', request: { operation, ...fields } })
    } catch (error) {
      throw homeAssistantError(error) ?? error
    }
    if (!response.ok) throw new ExternalApiError(response.error.code, response.error.detail)
    return response.result
  }
  return {
    /** Reads the repository's hass_sharp.toml at the commit `url` names, and every file it lists. */
    inspect: (url: string) => request<ExternalInspection>('inspect', { url }),
    /** Imports the picked items from the inspected commit: the libraries together, then each definition. */
    import: (picked: { owner: string; repo: string; sha: string; picks: ExternalPick[] }) => request<ExternalImportResult>('import', picked),
  }
}
