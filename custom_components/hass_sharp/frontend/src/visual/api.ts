import type { HomeAssistant } from '../types'
import type { VisualGraph } from './graph'

export interface SourceRange { nodeId: string; startLine: number; endLine: number }
export interface VisualDiagnostic {
  inputId?: string | null
  portId?: string | null
  nodeId: string | null
  message: string
  severity: number
  startLine: number | null
  startColumn: number | null
}
export interface VisualPreview { valid: boolean; source: string; sourceMap: SourceRange[]; diagnostics: VisualDiagnostic[] }
export interface VisualDocument {
  version: 1
  id: string
  revision: number
  graph: VisualGraph
  source: string
  sourceMap: SourceRange[]
  automationId: string
}
export interface VisualSaveResult { document: VisualDocument; active: boolean; activationError: string | null }
type Envelope<T> = { version: 1; ok: true; result: T } | { version: 1; ok: false; error: { code: string; detail: unknown } }
export class VisualApiError extends Error {
  constructor(public code: string, public detail: unknown) {
    super(typeof detail === 'string' ? detail : code === 'validation' ? 'Fix the graph errors before saving.' : `Visual automation request failed (${code}).`)
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
  return {
    preview: (graph: VisualGraph) => request<VisualPreview>('preview', { graph }),
    list: () => request<VisualDocument[]>('list'),
    read: (id: string) => request<VisualDocument>('read', { id }),
    save: (id: string, graph: VisualGraph, expectedRevision: number) => request<VisualSaveResult>('save', { id, graph, expectedRevision }),
    delete: (id: string, expectedRevision: number) => request<{ deleted: true }>('delete', { id, expectedRevision }),
  }
}
