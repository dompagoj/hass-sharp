import type { HomeAssistant, RunAcceptedResponse } from '../types'
import { VisualApiError, visualError, type VisualDiagnostic, type VisualPreview } from '../visual/api'
import type { DefinitionBody, DefinitionSummary, EntityDocument, HomeDefinition } from './model'

/** `preview`'s answer, and the detail of a refused save's `validation` error. */
type EntityPreview = VisualPreview
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

/**
 * A definition file the backend cannot read, as a list names it beside its result: its id, why (for a graph definition
 * an earlier development build saved, version 1 or 2, "saved by an earlier development build; delete it and build it
 * again"), and whether a delete at revision 0 can remove it (`deletable`: its name is a definition id and only its
 * content is wrong); one that is not deletable is removed by hand.
 */
export interface UnreadableDefinition { id: string; reason: string; deletable: boolean }

type Envelope<T> = { version: 1; ok: true; result: T; unreadable?: UnreadableDefinition[] } | { version: 1; ok: false; error: { code: string; detail: unknown } }

/**
 * A refused entity request: the backend's `{code, detail}` (`validation`, `conflict`, `not_found`, `invalid_request`,
 * `storage`, `internal`), or Home Assistant's own refusal (`unauthorized`, `not_ready`, a lost connection, …) with its
 * message as the detail. The message is the detail when that is a sentence. A {@link VisualApiError}, so the editor
 * session's conflict and validation handling takes it as it takes an automation's.
 */
export class EntityApiError extends VisualApiError {
  constructor(code: string, detail: unknown) {
    super(code, detail)
    this.message =
      typeof detail === 'string'
        ? detail
        : code === 'validation'
          ? 'Fix the problems before saving.'
          : `Entity definition request failed (${code}).`
  }
}
/** The query key of every list of entity definitions (the entities page's): a save, a switch or a delete makes them stale. */
export const definitionsQueryKey = ['entity-definitions'] as const

/** What to tell the user about a failed request: as `visualError` does for automations. */
export const entityError = visualError

/** home-assistant-js-websocket's code for a connection lost while a command waits (`ERR_CONNECTION_LOST`). */
const connectionLost = 3
export const connectionLostMessage = 'The connection to Home Assistant was lost.'

/**
 * Home Assistant's websocket library rejects a command with a plain value, never an Error: the result's `{code,
 * message}`, a whole failed result `{type: 'result', success: false, error: {code, message}}` (a lost connection), or
 * the bare number {@link connectionLost}.
 */
const homeAssistantError = (error: unknown) => {
  if (error === connectionLost) return new EntityApiError(String(error), connectionLostMessage)
  if (error === null || typeof error !== 'object' || error instanceof Error) return
  const found = error as { code?: unknown; message?: unknown; error?: unknown }
  const { code, message } = (typeof found.code === 'string' || typeof found.code === 'number' ? found : found.error ?? {}) as { code?: unknown; message?: unknown }
  if (typeof code !== 'string' && typeof code !== 'number') return
  return new EntityApiError(String(code), typeof message === 'string' ? message : code === connectionLost ? connectionLostMessage : undefined)
}

/** The entity definitions' websocket API (`hass_sharp/entities`). */
export function entityApi(hass: Pick<HomeAssistant, 'callWS'>) {
  const request = async <T>(operation: string, fields = {}): Promise<T> => (await answered<T>(operation, fields)).result
  const answered = async <T>(operation: string, fields = {}) => {
    let response: Envelope<T>
    try {
      response = await hass.callWS<Envelope<T>>({ type: 'hass_sharp/entities', request: { operation, ...fields } })
    } catch (error) {
      throw homeAssistantError(error) ?? error
    }
    if (!response.ok) throw new EntityApiError(response.error.code, response.error.detail)
    return response
  }
  return {
    list: () => request<DefinitionSummary[]>('list'),
    /** The entities page's list, and the definition files that cannot be read; `todayUtc` is the start of the viewer's day, for `runsToday`. */
    homePage: async (todayUtc: string) => {
      const answer = await answered<HomeDefinition[]>('list', { view: 'home', todayUtc })
      return { definitions: answer.result, unreadable: answer.unreadable ?? [] }
    },
    read: (id: string) => request<EntityDocument>('read', { id }),
    preview: (body: DefinitionBody) => request<EntityPreview>('preview', { definition: body }),
    /** `expectedRevision` 0 for a new definition; `enabled` only for a new one (saved off, it does not run). */
    save: (id: string, body: DefinitionBody, expectedRevision: number, enabled?: boolean) =>
      request<EntitySaveResult>('save', { id, expectedRevision, definition: body, ...(enabled === undefined ? {} : { enabled }) }),
    setEnabled: (id: string, enabled: boolean) => request<EntityEnabledResult>('set_enabled', { id, enabled }),
    remove: (id: string, expectedRevision: number) => request<{ deleted: true }>('delete', { id, expectedRevision }),
    /** Runs a Trigger step of the saved definition now (refused unless it is on and runs its saved revision). */
    run: (id: string, triggerId: string) => request<RunAcceptedResponse>('run', { id, triggerId }),
  }
}
