import { analyzeGraph, nodeCatalog, type GraphEdge, type GraphNode, type VisualCatalogSnapshot } from './contract'
import type { VisualGraph } from './graph'
import { createUuid } from './id'

type Point = { x: number; y: number }
/** Marks clipboard text as copied steps, so pasting someone's unrelated JSON is ignored. */
export const clipboardMarker = 'hass-sharp/visual-steps'
export interface StepClipboard {
  marker: typeof clipboardMarker
  version: 1
  nodes: GraphNode[]
  /** Only the wires between the copied steps. */
  edges: GraphEdge[]
}

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const wellFormedNode = (node: unknown): node is GraphNode =>
  record(node) && typeof node.id === 'string' && node.id !== '' && typeof node.kind === 'string' && record(node.config)
const wellFormedEdge = (edge: unknown): edge is GraphEdge =>
  record(edge) && typeof edge.id === 'string' && typeof edge.source === 'string' && typeof edge.target === 'string' &&
  (edge.kind === 'data' ? typeof edge.sourcePort === 'string' && typeof edge.targetPort === 'string' : edge.port === undefined || typeof edge.port === 'string')

/** The chosen steps and the wires between them, as a detached copy; undefined when none of the ids is a step. */
export function copySteps(graph: VisualGraph, ids: Iterable<string>): StepClipboard | undefined {
  const wanted = new Set(ids)
  const seen = new Set<string>()
  const nodes = (Array.isArray(graph.nodes) ? graph.nodes : []).filter(node => {
    if (!wellFormedNode(node) || !wanted.has(node.id) || seen.has(node.id)) return false
    seen.add(node.id)
    return true
  })
  if (!nodes.length) return
  const edges = (Array.isArray(graph.edges) ? graph.edges : []).filter(edge => wellFormedEdge(edge) && seen.has(edge.source) && seen.has(edge.target))
  return structuredClone({ marker: clipboardMarker, version: 1, nodes, edges })
}

/** Clipboard text back to steps; undefined for anything that is not a step copy. Never throws. */
export function parseSteps(text: string): StepClipboard | undefined {
  let value: unknown
  try {
    value = JSON.parse(text)
  } catch {
    return
  }
  if (!record(value) || value.marker !== clipboardMarker || value.version !== 1 || !Array.isArray(value.nodes) || !Array.isArray(value.edges)) return
  const nodes = value.nodes.filter(wellFormedNode)
  const ids = new Set(nodes.map(node => node.id))
  if (!nodes.length || ids.size !== nodes.length) return
  return { marker: clipboardMarker, version: 1, nodes, edges: value.edges.filter(edge => wellFormedEdge(edge) && ids.has(edge.source) && ids.has(edge.target)) }
}

let memory: StepClipboard | undefined
type SystemClipboard = { writeText?(text: string): Promise<void>; readText?(): Promise<string> }
const system = (): SystemClipboard | undefined => {
  try {
    return typeof navigator === 'undefined' ? undefined : (navigator.clipboard as SystemClipboard | undefined)
  } catch {
    return undefined
  }
}

/**
 * Keeps the copy in memory, and also puts it on the system clipboard (JSON with the marker) where the browser has
 * the API and allows it, so it can be pasted into another tab. A missing or denied clipboard is never an error.
 */
export async function writeSteps(data: StepClipboard): Promise<void> {
  memory = structuredClone(data)
  try {
    await system()?.writeText?.(JSON.stringify(data))
  } catch {
    // The in-memory copy is enough.
  }
}

/** The in-memory copy first; otherwise steps on the system clipboard, when readable. Never throws. */
export async function readSteps(): Promise<StepClipboard | undefined> {
  if (memory) return structuredClone(memory)
  try {
    const text = await system()?.readText?.()
    return typeof text === 'string' ? parseSteps(text) : undefined
  } catch {
    return undefined
  }
}

/** Forgets the in-memory copy (tests, and a new editor session that should not paste stale steps). */
export function clearSteps() {
  memory = undefined
}

/**
 * Messages that mean the editor would refuse the graph or a wire in it, as opposed to the problems pasted steps
 * normally have until they are connected (not reachable from a trigger, so their data wires cannot be checked
 * against a scope yet, a value not set): those are fixed by editing, these only by undoing.
 */
const refusal = /^(?:Graph limit|Expected graph version|Nodes and edges arrays|Each node requires|Duplicate (?:node|edge) id|Unknown node kind|Edge id is required|Edge endpoint missing|Do not mix|Execution port must be|Unknown execution port|Unknown edge kind|Only one execution edge per port|Only one data source per input|Trigger cannot have incoming|Unknown data (?:input|output) port|Structured nesting limit|(?:Execution|Data) cycle is forbidden)/

export type PasteResult = { graph: VisualGraph; ids: string[] } | { error: string }
/**
 * Adds a copy of the steps to the graph with new ids, at `at` (their top-left corner, in flow coordinates) or
 * moved by `offset` from where they were copied. Wires between the pasted steps are kept with new ids; wires to
 * steps outside the copy were never copied. The result goes through the editor's validation: a paste that would
 * add a problem the editor refuses (too many steps, an unknown kind, a cycle, a second wire into one port) is not
 * made, and the result says why.
 */
export function pasteSteps(
  graph: VisualGraph,
  data: StepClipboard,
  catalog: VisualCatalogSnapshot = nodeCatalog,
  place: { at: Point } | { offset: Point },
  grid?: number,
): PasteResult {
  if (!data.nodes.length) return { error: 'There is nothing to paste' }
  const taken = new Set((Array.isArray(graph.nodes) ? graph.nodes : []).flatMap(node => (record(node) && typeof node.id === 'string' ? [node.id] : [])))
  const ids = new Map<string, string>()
  for (const node of data.nodes) {
    let id = createUuid()
    while (taken.has(id)) id = createUuid()
    taken.add(id)
    ids.set(node.id, id)
  }
  const position = (node: GraphNode): Point => ({ x: finite(node.position?.x), y: finite(node.position?.y) })
  const corner = data.nodes.reduce<Point>((min, node) => ({ x: Math.min(min.x, position(node).x), y: Math.min(min.y, position(node).y) }), { x: Infinity, y: Infinity })
  const move = 'at' in place ? { x: place.at.x - corner.x, y: place.at.y - corner.y } : place.offset
  const snap = (value: number) => (grid ? Math.round(value / grid) * grid : Math.round(value))
  const nodes = data.nodes.map(node => ({
    ...structuredClone(node),
    id: ids.get(node.id)!,
    position: { x: snap(position(node).x + move.x), y: snap(position(node).y + move.y) },
  }))
  const edgeIds = new Set((Array.isArray(graph.edges) ? graph.edges : []).flatMap(edge => (record(edge) && typeof edge.id === 'string' ? [edge.id] : [])))
  const unique = (base: string) => {
    let id = base
    for (let suffix = 2; edgeIds.has(id); suffix++) id = `${base}#${suffix}`
    edgeIds.add(id)
    return id
  }
  const edges = data.edges.flatMap((edge): GraphEdge[] => {
    const source = ids.get(edge.source), target = ids.get(edge.target)
    if (!source || !target) return []
    if (edge.kind === 'data')
      return [{ id: unique(JSON.stringify([source, edge.sourcePort, target, edge.targetPort])), kind: 'data' as const, source, target, sourcePort: edge.sourcePort, targetPort: edge.targetPort }]
    const port = edge.port
    return [{ id: unique(port === undefined || port === 'next' ? `${source}->${target}` : `${source}:${port}->${target}`), source, target, ...(port === undefined ? {} : { port }) }]
  })
  const next: VisualGraph = { ...graph, nodes: [...graph.nodes, ...nodes], edges: [...graph.edges, ...edges] }
  const refused = (value: VisualGraph) =>
    analyzeGraph(value, catalog).diagnostics.filter(d => d.severity === 3 && refusal.test(d.message)).map(d => JSON.stringify(d))
  const before = new Map<string, number>()
  for (const key of refused(graph)) before.set(key, (before.get(key) ?? 0) + 1)
  const added = refused(next).filter(key => {
    const count = before.get(key) ?? 0
    if (count > 0) before.set(key, count - 1)
    return count === 0
  })
  if (added.length) return { error: [...new Set(added.map(key => (JSON.parse(key) as { message: string }).message))].join('; ') }
  return { graph: next, ids: nodes.map(node => node.id) }
}

const finite = (value: unknown) => (typeof value === 'number' && Number.isFinite(value) ? value : 0)
