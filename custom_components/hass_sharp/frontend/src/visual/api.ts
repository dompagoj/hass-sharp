import type { HomeAssistant, RunStatus, RunTraceResponse } from '../types'
import type { VisualGraph } from './graph'
import type { ServicesSection, VisualCatalogSnapshot } from './contract'

export interface SourceRange {
  nodeId: string
  startLine: number
  endLine: number
}
export interface VisualDiagnostic {
  inputId?: string | null
  portId?: string | null
  /** The validator's code for a structural problem (`port.unknownInput`); most problems have none. */
  code?: string | null
  /** Set by the editor on a problem about a value wire whose pin is missing: that wire, which the problem can remove. */
  edgeId?: string
  /** That wire's place in the graph's edges (another wire may have its id). */
  edgeIndex?: number
  nodeId: string | null
  message: string
  severity: number
  startLine: number | null
  startColumn: number | null
}
export interface VisualPreview {
  valid: boolean
  source: string
  sourceMap: SourceRange[]
  diagnostics: VisualDiagnostic[]
}
export interface VisualDocument {
  version: 1
  id: string
  revision: number
  graph: VisualGraph
  source: string
  sourceMap: SourceRange[]
  catalogGeneration?: string | null
  activeRevision?: number | null
  activeSourceHash?: string | null
  currentRevisionActive?: boolean
  active?: boolean
  activationError?: string | null
  automationId: string
  /** Off: nothing starts it by itself, Run now still works. Missing means on. */
  enabled?: boolean
  /** When the graph was last saved (`read` only; null for a document saved before this was recorded). */
  savedUtc?: string | null
}
/** A `list` entry: the document without its generated source; `read` returns the source. */
export type VisualDocumentSummary = Omit<VisualDocument, 'source' | 'sourceMap'>
/** A node of an {@link GraphOutline}: trigger nodes also carry their id and config, for summaries and Run now. */
export interface OutlineNode {
  kind: string
  x: number
  y: number
  label?: string
  id?: string
  config?: Record<string, unknown>
}
/** What the automations page draws a graph from: its nodes and its execution edges as pairs of node indexes. */
export interface GraphOutline {
  name: string
  nodes: OutlineNode[]
  edges: [number, number][]
}
/** One of an automation's most recent runs. */
export interface RecentRun {
  runId: string
  status: RunStatus
  startUtc: string | null
  durationMs: number | null
}
/** An entry of the automations page's list (`list` with `view: 'home'`): no graph, an outline, and how it has been running. */
export interface HomeAutomation {
  id: string
  revision: number
  automationId: string
  enabled: boolean
  active?: boolean
  currentRevisionActive: boolean
  activationError?: string | null
  /** How many problems keep the saved version from loading, when known. */
  problemCount?: number | null
  savedUtc?: string | null
  catalogGeneration?: string | null
  outline: GraphOutline
  /** The newest runs, up to 10, newest last. */
  recentRuns: RecentRun[]
  runsToday: number
  failedLastDay: number
  lastRunUtc: string | null
}
export interface VisualSaveResult {
  document: VisualDocument
  active: boolean
  activationError: string | null
}
type Envelope<T> = { version: 1; ok: true; result: T } | { version: 1; ok: false; error: { code: string; detail: unknown } }
export class VisualApiError extends Error {
  constructor(
    public code: string,
    public detail: unknown,
  ) {
    super(
      typeof detail === 'string'
        ? detail
        : code === 'validation'
          ? 'Fix the graph errors before saving.'
          : `Visual automation request failed (${code}).`,
    )
  }
}
export const visualError = (error: unknown) => {
  const e = error as { message?: string; error?: string; code?: string }
  return e?.message ?? e?.error ?? e?.code ?? 'Check your connection and try again.'
}
export function visualApi(hass: Pick<HomeAssistant, 'callWS'>) {
  const request = async <T>(operation: string, fields = {}): Promise<T> => {
    const response = await hass.callWS<Envelope<T>>({ type: 'hass_sharp/visual', request: { operation, ...fields } })
    if (!response.ok) throw new VisualApiError(response.error.code, response.error.detail)
    return response.result
  }
  const library = async <T>(operation: string): Promise<T> => {
    const response = await hass.callWS<Envelope<T>>({ type: 'hass_sharp/node_library', request: { operation } })
    if (!response.ok) throw new VisualApiError(response.error.code, response.error.detail)
    return response.result
  }
  return {
    /** `servicesGeneration` tells the session when to refetch `services()` (C4). */
    catalog: () =>
      library<{ catalog: VisualCatalogSnapshot; generation: string; servicesGeneration?: string; saved: true; valid: boolean; diagnostics: VisualDiagnostic[] }>('catalog'),
    /** Contract C4. Never part of the catalog root; attach it as `catalog.services`. */
    services: () => library<ServicesSection>('services'),
    /** Contract C9. Not enveloped, like `run_history`. */
    runTrace: (runId: string) => hass.callWS<RunTraceResponse>({ type: 'hass_sharp/run_trace', runId }),
    preview: (graph: VisualGraph) => request<VisualPreview>('preview', { graph }),
    list: () => request<VisualDocumentSummary[]>('list'),
    /** The automations page's list; `todayUtc` is the start of the viewer's day, for `runsToday`. */
    home: (todayUtc: string) => request<HomeAutomation[]>('list', { view: 'home', todayUtc }),
    setEnabled: (id: string, enabled: boolean) => request<{ id: string; enabled: boolean }>('set_enabled', { id, enabled }),
    read: (id: string) => request<VisualDocument>('read', { id }),
    /** `enabled` only for a new automation (expectedRevision 0): saved turned off, it never runs on; the backend refuses it for a saved one. */
    save: (id: string, graph: VisualGraph, expectedRevision: number, options: { enabled?: boolean } = {}) =>
      request<VisualSaveResult>('save', { id, graph, expectedRevision, ...(options.enabled === undefined ? {} : { enabled: options.enabled }) }),
    delete: (id: string, expectedRevision: number) => request<{ deleted: true }>('delete', { id, expectedRevision }),
  }
}
