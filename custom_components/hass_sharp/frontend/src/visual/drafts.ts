import { nodeCatalog } from './contract'
import { serializeGraph, type VisualGraph } from './graph'

/**
 * Unsaved changes kept in this browser, so they survive the panel being torn down without asking (Home Assistant's
 * sidebar, an entry reload). One draft per automation and Home Assistant user (`hass-sharp:draft:<user>/<id>`, or
 * `<user>/new:<editing session>` before its first save), with the revision it was made on. At most
 * {@link draftLimit} are kept. Storage may be missing, full or refused: every access is guarded, and the editor works
 * the same without it.
 */
export interface VisualDraft {
  graph: VisualGraph
  /** The saved revision the changes were made on; 0 for an automation never saved. */
  revision: number
  name: string
  /** When the changes were made (ms since the epoch): kept while the draft's content stays the same. */
  at: number
  /** Code typed in a Custom C# node's code editor and not applied yet. */
  code?: { nodeId: string; text: string }
}

const prefix = 'hass-sharp:draft:'
/** Drafts older than this are removed when the editor starts. */
export const draftMaxAge = 14 * 24 * 60 * 60 * 1000
/** Drafts kept at most; writing one more drops the oldest. */
export const draftLimit = 20

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
  const code = record(value.code) && typeof value.code.nodeId === 'string' && typeof value.code.text === 'string' ? { nodeId: value.code.nodeId, text: value.code.text } : undefined
  return { graph: graph as unknown as VisualGraph, revision: value.revision, name: typeof value.name === 'string' ? value.name : '', at: value.at, ...(code ? { code } : {}) }
}
/** The same changes: graph, base revision and unapplied code. */
const sameContent = (a: VisualDraft, b: VisualDraft) =>
  a.revision === b.revision && a.code?.nodeId === b.code?.nodeId && a.code?.text === b.code?.text && serializeGraph(a.graph) === serializeGraph(b.graph)
/** Every draft kept, with its key (without the prefix). */
function allDrafts(store: Storage): { key: string; draft: VisualDraft | undefined }[] {
  const keys: string[] = []
  for (let i = 0; i < store.length; i++) {
    const key = store.key(i)
    if (key?.startsWith(prefix)) keys.push(key.slice(prefix.length))
  }
  return keys.map(key => ({ key, draft: parse(store.getItem(prefix + key)) }))
}
/** Drops the oldest drafts other than `keep` until at most `limit` are left. */
function trim(store: Storage, limit: number, keep: string) {
  const others = allDrafts(store).filter(d => d.key !== keep).sort((a, b) => (a.draft?.at ?? 0) - (b.draft?.at ?? 0))
  for (const { key } of others.slice(0, Math.max(0, others.length + 1 - limit))) store.removeItem(prefix + key)
}

/** The draft kept under `key`, when there is a readable one. */
export function readDraft(key: string): VisualDraft | undefined {
  try {
    return parse(storage()?.getItem(prefix + key) ?? null)
  } catch {
    return undefined
  }
}
/** The drafts whose key starts with `start` (the new automations of a user), newest first. */
export function listDrafts(start: string): { key: string; draft: VisualDraft }[] {
  try {
    const store = storage()
    if (!store) return []
    return allDrafts(store).filter((d): d is { key: string; draft: VisualDraft } => d.key.startsWith(start) && !!d.draft).sort((a, b) => b.draft.at - a.draft.at)
  } catch {
    return []
  }
}
/**
 * Keeps `draft` under `key`. When the stored draft has the same content its time is kept, so the time shown is when the
 * changes were made, not when they were last opened. The oldest drafts are dropped beyond {@link draftLimit}; when the
 * storage is full, old drafts are pruned and the write is tried once more. Returns whether it was written.
 */
export function writeDraft(key: string, draft: VisualDraft): boolean {
  let store: Storage | undefined
  try {
    store = storage()
    if (!store) return false
    const stored = parse(store.getItem(prefix + key))
    const text = JSON.stringify(stored && sameContent(stored, draft) ? { ...draft, at: stored.at } : draft)
    trim(store, draftLimit, key)
    try {
      store.setItem(prefix + key, text)
    } catch {
      // Full: drop expired drafts and the older half of the others, then once more.
      pruneDrafts()
      trim(store, Math.floor(draftLimit / 2), key)
      store.setItem(prefix + key, text)
    }
    return true
  } catch {
    // Refused, or still full: the editor goes on without a draft.
    return false
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
    for (const { key, draft } of allDrafts(store)) if (!draft || now - draft.at > draftMaxAge) store.removeItem(prefix + key)
  } catch {
    // Nothing to do.
  }
}
