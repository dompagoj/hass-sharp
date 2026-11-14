import type { CompletionItem, Diagnostic, HomeAssistant } from '../types'
import type { VisualCatalogSnapshot } from './contract'
import type { VisualDocument, VisualDiagnostic } from './api'

export interface LibraryDocument { version: 1; id: string; name: string; revision: number; source: string }
export interface LibraryDiagnostic extends Diagnostic { sourceId: string }
export interface LibraryRange { start: number; length: number; startLine: number; startColumn: number; endLine: number; endColumn: number }
export interface LibraryDefinition { id: string; range: LibraryRange }
export interface LibrarySignatureHelp { signatures: { label: string; documentation: string; parameters: string[] }[]; activeSignature: number; activeParameter: number }
export interface LibraryPreview { valid: boolean; catalog: VisualCatalogSnapshot; diagnostics: LibraryDiagnostic[] }
export interface LibraryContext { id: string; source: string; workspaceVersion: string; generation: string; sources: { id: string; source: string }[] }
export interface LibraryReply<T> { id: string; workspaceVersion: string; generation: string; sourceGeneration: string; saved: false; data: T }
/** A Custom C# node's unsaved code, for `custom_preview` and the `custom: true` language services. */
export interface CustomCode { nodeId: string; source: string; workspaceVersion: string }
/** What the compiler makes of a Custom C# node's code; diagnostics are 1-based in the node's own code. */
export interface CustomPreview { valid: boolean; diagnostics: LibraryDiagnostic[]; signature: unknown }
/** A language-service reply about a Custom C# node's code: `id` is the node's id; definitions in its code name `$custom`. */
export interface CustomReply<T> { id: string; workspaceVersion: string; generation: string; saved: false; data: T }
export const customDocumentId = '$custom'
export interface LibraryActivation { id: string; saved: true; document: VisualDocument; active: boolean; activationError: string | null }
export interface LibraryMutation { saved: true; document: LibraryDocument | null; deleted: boolean; generation: string; catalog: VisualCatalogSnapshot; automations: LibraryActivation[] }
export class LibraryApiError extends Error {
  constructor(public code: string, public detail: unknown) { super(typeof detail === 'string' ? detail : code === 'validation' ? 'Fix library or dependent automation errors before saving.' : `Node library request failed (${code}).`) }
}
type Envelope<T> = { version: 1; ok: true; result: T } | { version: 1; ok: false; error: { code: string; detail: unknown } }
export function libraryApi(hass: Pick<HomeAssistant, 'callWS'>) {
  const request = async <T>(operation: string, fields = {}): Promise<T> => {
    const response = await hass.callWS<Envelope<T>>({ type: 'hass_sharp/node_library', request: { operation, ...fields } })
    if (!response.ok) throw new LibraryApiError(response.error.code, response.error.detail)
    return response.result
  }
  const language = <T>(operation: string, context: LibraryContext, position = 0) => request<LibraryReply<T>>(operation, { ...context, position })
  // The node's own id always goes along: the namespace its code compiles in, and so its own type ids, derive from it.
  const custom = <T>(operation: string, code: CustomCode, position = 0) =>
    request<CustomReply<T>>(operation, { custom: true, nodeId: code.nodeId, source: code.source, workspaceVersion: code.workspaceVersion, position })
  return {
    customPreview: (code: Pick<CustomCode, 'nodeId' | 'source'>) => request<CustomPreview>('custom_preview', { nodeId: code.nodeId, source: code.source }),
    customCompletion: (code: CustomCode, position: number) => custom<CompletionItem[]>('completion', code, position),
    customHover: (code: CustomCode, position: number) => custom<{ text?: string; range?: LibraryRange }>('hover', code, position),
    customSignature: (code: CustomCode, position: number) => custom<LibrarySignatureHelp>('signature', code, position),
    customDefinition: (code: CustomCode, position: number) => custom<LibraryDefinition[]>('definition', code, position),
    customFormat: (code: CustomCode) => custom<string>('format', code),
    list: () => request<{ documents: LibraryDocument[]; generation: string }>('list'),
    read: (id: string) => request<LibraryDocument>('read', { id }),
    catalog: () => request<LibraryPreview & { generation: string; saved: true }>('catalog'),
    save: (document: LibraryDocument) => request<LibraryMutation>('save', { id: document.id, name: document.name, source: document.source, expectedRevision: document.revision }),
    delete: (id: string, expectedRevision: number) => request<LibraryMutation>('delete', { id, expectedRevision }),
    preview: (context: LibraryContext) => language<LibraryPreview>('preview', context),
    completion: (context: LibraryContext, position: number) => language<CompletionItem[]>('completion', context, position),
    hover: (context: LibraryContext, position: number) => language<{ text?: string; range?: LibraryRange }>('hover', context, position),
    signature: (context: LibraryContext, position: number) => language<LibrarySignatureHelp>('signature', context, position),
    definition: (context: LibraryContext, position: number) => language<LibraryDefinition[]>('definition', context, position),
    format: (context: LibraryContext) => language<string>('format', context),
  }
}
export type LibraryApi = ReturnType<typeof libraryApi>
export interface LibraryValidationDetail { diagnostics?: LibraryDiagnostic[]; automations?: { id: string; diagnostics: VisualDiagnostic[] }[] }
export const librariesChanged = () => window.dispatchEvent(new Event('hass-sharp-libraries-changed'))
