import type { HomeAssistant } from '../types'
import { visualError, type VisualDiagnostic, type VisualPreview } from '../visual/api'
import type { DefinitionBody, DefinitionSummary, EntityDocument, EntityType, HomeDefinition } from './model'

/** `preview`'s answer, and the detail of a refused save's `validation` error. */
export type EntityPreview = VisualPreview
export interface EntitySaveResult {
  id: string
  saved: true
  document: EntityDocument
  /** Whether the saved revision runs now. */
  active: boolean
  activationError: string | null
  /** A valid definition's warnings, when it has any. */
  diagnostics?: VisualDiagnostic[]
}
export interface EntityEnabledResult {
  id: string
  enabled: boolean
  active: boolean
  activationError: string | null
}

type Envelope<T> = { version: 1; ok: true; result: T } | { version: 1; ok: false; error: { code: string; detail: unknown } }

/**
 * A refused entity request: the backend's `{code, detail}` (`validation`, `conflict`, `not_found`, `invalid_request`,
 * `storage`, `internal`), or Home Assistant's own `{code, message}` (`unauthorized`, `not_ready`, …) with the message
 * as its detail. The message is the detail when that is a sentence.
 */
export class EntityApiError extends Error {
  constructor(
    public code: string,
    public detail: unknown,
  ) {
    super(
      typeof detail === 'string'
        ? detail
        : code === 'validation'
          ? 'Fix the problems before saving.'
          : `Entity definition request failed (${code}).`,
    )
  }
}
/** What to tell the user about a failed request: as `visualError` does for automations. */
export const entityError = visualError

/** Home Assistant answers a refused websocket command with a plain `{code, message}` object, not an Error. */
const homeAssistantError = (error: unknown) => {
  if (error === null || typeof error !== 'object' || error instanceof Error) return
  const { code, message } = error as { code?: unknown; message?: unknown }
  if (typeof code !== 'string' && typeof code !== 'number') return
  return new EntityApiError(String(code), typeof message === 'string' ? message : undefined)
}

/** The entity definitions' websocket API (`hass_sharp/entities`). */
export function entityApi(hass: Pick<HomeAssistant, 'callWS'>) {
  const request = async <T>(operation: string, fields = {}): Promise<T> => {
    let response: Envelope<T>
    try {
      response = await hass.callWS<Envelope<T>>({ type: 'hass_sharp/entities', request: { operation, ...fields } })
    } catch (error) {
      throw homeAssistantError(error) ?? error
    }
    if (!response.ok) throw new EntityApiError(response.error.code, response.error.detail)
    return response.result
  }
  return {
    /** The classes the node libraries declare, by id. */
    types: () => request<{ types: EntityType[]; generation: string }>('types'),
    list: () => request<DefinitionSummary[]>('list'),
    /** The entities page's list; `todayUtc` is the start of the viewer's day, for `runsToday`. */
    home: (todayUtc: string) => request<HomeDefinition[]>('list', { view: 'home', todayUtc }),
    read: (id: string) => request<EntityDocument>('read', { id }),
    preview: (body: DefinitionBody) => request<EntityPreview>('preview', { definition: body }),
    /** `expectedRevision` 0 for a new definition; `enabled` only for a new one (saved off, it does not run). */
    save: (id: string, body: DefinitionBody, expectedRevision: number, enabled?: boolean) =>
      request<EntitySaveResult>('save', { id, expectedRevision, definition: body, ...(enabled === undefined ? {} : { enabled }) }),
    setEnabled: (id: string, enabled: boolean) => request<EntityEnabledResult>('set_enabled', { id, enabled }),
    remove: (id: string, expectedRevision: number) => request<{ deleted: true }>('delete', { id, expectedRevision }),
  }
}
