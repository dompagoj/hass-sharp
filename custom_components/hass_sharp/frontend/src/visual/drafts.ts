import { nodeCatalog } from './contract'
import type { VisualGraph } from './graph'

/**
 * Unsaved changes kept in this browser, so they survive the panel being torn down without asking (Home Assistant's
 * sidebar, an entry reload). One draft per automation (`hass-sharp:draft:<id>`, or `new:<name>` before its first
 * save), with the revision it was made on. Storage may be missing, full or refused: every access is guarded, and the
 * editor works the same without it.
 */
export interface VisualDraft {
  graph: VisualGraph
  /** The saved revision the changes were made on; 0 for an automation never saved. */
  revision: number
  name: string
  /** When it was written (ms since the epoch). */
  at: number
}

const prefix = 'hass-sharp:draft:'
/** Drafts older than this are removed when the editor starts. */
export const draftMaxAge = 14 * 24 * 60 * 60 * 1000

const storage = (): Storage | undefined => {
  try {
    return typeof localStorage === 'undefined' ? undefined : localStorage
  } catch {
    return undefined
  }
}
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const parse = (text: string | null): VisualDraft | undefined => {
  if (text === null) return
  let value: unknown
  try {
    value = JSON.parse(text)
  } catch {
    return
  }
  if (!record(value) || !record(value.graph) || typeof value.revision !== 'number' || typeof value.at !== 'number' || !Number.isFinite(value.at)) return
  const graph = value.graph
  if (graph.version !== nodeCatalog.graphVersion || !Array.isArray(graph.nodes) || !Array.isArray(graph.edges)) return
  return { graph: graph as unknown as VisualGraph, revision: value.revision, name: typeof value.name === 'string' ? value.name : '', at: value.at }
}

/** The draft kept under `key`, when there is a readable one. */
export function readDraft(key: string): VisualDraft | undefined {
  try {
    return parse(storage()?.getItem(prefix + key) ?? null)
  } catch {
    return undefined
  }
}
export function writeDraft(key: string, draft: VisualDraft) {
  try {
    storage()?.setItem(prefix + key, JSON.stringify(draft))
  } catch {
    // Full or refused: the editor goes on without a draft.
  }
}
export function removeDraft(key: string) {
  try {
    storage()?.removeItem(prefix + key)
  } catch {
    // Nothing to do.
  }
}
/** Removes drafts older than {@link draftMaxAge}, and entries under the draft prefix that are not drafts. */
export function pruneDrafts(now = Date.now()) {
  try {
    const store = storage()
    if (!store) return
    const keys: string[] = []
    for (let i = 0; i < store.length; i++) {
      const key = store.key(i)
      if (key?.startsWith(prefix)) keys.push(key)
    }
    for (const key of keys) {
      const draft = parse(store.getItem(key))
      if (!draft || now - draft.at > draftMaxAge) store.removeItem(key)
    }
  } catch {
    // Nothing to do.
  }
}
