import { analyzeGraph, nodeCatalog, type DiagnosticCode, type GraphEdge, type GraphNode, type VisualCatalogSnapshot } from './contract'
import type { VisualGraph } from './graph'
import { createUuid } from './id'

type Point = { x: number; y: number }
/** Marks clipboard text as copied steps, so pasting someone's unrelated JSON is ignored. */
export const clipboardMarker = 'hass-sharp/visual-steps'
export interface StepClipboard {
  marker: typeof clipboardMarker
  version: 1
  /** When it was copied (ms since the epoch): a paste takes the newer of this tab's copy and the system clipboard's. */
  copiedAt: number
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
  return structuredClone({ marker: clipboardMarker, version: 1, copiedAt: Date.now(), nodes, edges })
}

/** Longer clipboard text is not a step copy anyone made here (256 steps of JSON is far less); it is not parsed. */
const maxText = 1_000_000

/** Clipboard text back to steps; undefined for anything that is not a step copy. Never throws. */
export function parseSteps(text: string): StepClipboard | undefined {
  if (text.length > maxText) return
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
  const copiedAt = typeof value.copiedAt === 'number' && Number.isFinite(value.copiedAt) ? value.copiedAt : 0
  return { marker: clipboardMarker, version: 1, copiedAt, nodes, edges: value.edges.filter(edge => wellFormedEdge(edge) && ids.has(edge.source) && ids.has(edge.target)) }
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
 * The page lost focus since this tab's copy (the user may have copied elsewhere). Until then this tab's copy is the
 * newest there can be, so a paste uses it without reading the system clipboard, which would ask for permission.
 */
let leftSinceCopy = false
let watching = false
const watchFocus = () => {
  if (watching || typeof window === 'undefined') return
  watching = true
  window.addEventListener('blur', () => (leftSinceCopy = true))
}

/**
 * Keeps the copy in memory, and also puts it on the system clipboard (JSON with the marker) where the browser has
 * the API and allows it, so it can be pasted into another tab. A missing or denied clipboard is never an error.
 */
export async function writeSteps(data: StepClipboard): Promise<void> {
  memory = structuredClone(data)
  leftSinceCopy = false
  watchFocus()
  try {
    await system()?.writeText?.(JSON.stringify(data))
  } catch {
    // The in-memory copy is enough.
  }
}

/**
 * Steps to paste. This tab's copy while the page has kept focus since it was made; otherwise (no copy here, or the
 * user went elsewhere, perhaps to copy in another tab) the newer of this tab's copy and steps on the system
 * clipboard, when it is readable. Never throws.
 */
export async function readSteps(): Promise<StepClipboard | undefined> {
  if (memory && !leftSinceCopy) return structuredClone(memory)
  let shared: StepClipboard | undefined
  try {
    const text = await system()?.readText?.()
    shared = typeof text === 'string' ? parseSteps(text) : undefined
  } catch {
    shared = undefined
  }
  const newest = shared && (!memory || shared.copiedAt > memory.copiedAt) ? shared : memory
  return newest && structuredClone(newest)
}

/**
 * Diagnostic codes that mean the editor would refuse the graph or a wire in it, as opposed to the problems pasted
 * steps normally have until they are connected (`node.unreachable`, and `scope.*` for their value wires, which cannot
 * be checked against a scope yet; a value not set): those are fixed by editing, these only by undoing.
 */
export const pasteRefusals: ReadonlySet<DiagnosticCode> = new Set<DiagnosticCode>([
  'graph.version', 'graph.shape', 'graph.limit',
  'node.invalid', 'node.duplicate', 'node.unknownKind', 'limit.nesting',
  'edge.invalid', 'edge.duplicate', 'edge.endpoint', 'edge.unknownKind', 'edge.valueWirePins', 'edge.orderWirePins', 'edge.orderPinName',
  'port.unknownExecution', 'port.executionTaken', 'port.unknownOutput', 'port.unknownInput', 'port.inputTaken', 'trigger.incoming',
  'cycle.execution', 'cycle.data',
])

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
  const types = customTypeRenames(data.nodes, ids, catalog)
  const nodes = data.nodes.map(node => ({
    ...structuredClone(node), id: ids.get(node.id)!, position: { x: snap(position(node).x + move.x), y: snap(position(node).y + move.y) },
    ...(types ? { config: renameTypes(node.config, types) } : {}),
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
    analyzeGraph(value, catalog).diagnostics.filter(d => d.severity === 3 && d.code !== undefined && pasteRefusals.has(d.code)).map(d => JSON.stringify(d))
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

/**
 * The namespace a Custom C# node's code is compiled in (`NodeLibraryCompiler.CustomNamespace`): letters and digits of
 * the node id are kept, every other character is written as `_` and four hex digits.
 */
export function customNamespace(nodeId: string): string {
  let name = 'Custom_'
  for (let i = 0; i < nodeId.length; i++) {
    const code = nodeId.charCodeAt(i)
    name += /[A-Za-z0-9]/.test(nodeId[i]) ? nodeId[i] : `_${code.toString(16).toUpperCase().padStart(4, '0')}`
  }
  return name
}
/**
 * For each copied Custom C# node, its types' namespace and the one its copy's code compiles in: a type its code
 * declares has an id under its own node's namespace (`global::NodeLib.Custom_<id>.T`), so a copy that kept the old
 * ids would not match what its code compiles to, and would read as out of date with its code.
 */
function customTypeRenames(nodes: GraphNode[], ids: Map<string, string>, catalog: VisualCatalogSnapshot) {
  const renames = nodes.flatMap(node => {
    const spec = Object.hasOwn(catalog.nodes, node.kind) ? catalog.nodes[node.kind] : undefined
    return spec?.dynamicInputs === 'custom' ? [{ pattern: new RegExp(`(^|[^A-Za-z0-9_])NodeLib\\.${customNamespace(node.id)}\\.`, 'g'), to: `NodeLib.${customNamespace(ids.get(node.id)!)}.` }] : []
  })
  return renames.length ? renames : undefined
}
/** The settings with every type id under a renamed namespace moved to the new one; a node's code stays as written. */
function renameTypes(config: GraphNode['config'], renames: { pattern: RegExp; to: string }[]): GraphNode['config'] {
  const text = (value: string) => renames.reduce((current, { pattern, to }) => current.replace(pattern, (_, before: string) => before + to), value)
  const walk = (value: unknown): unknown =>
    typeof value === 'string' ? text(value)
      : Array.isArray(value) ? value.map(walk)
        : record(value) ? Object.fromEntries(Object.entries(value).map(([key, item]) => [text(key), walk(item)]))
          : value
  return Object.fromEntries(Object.entries(config).map(([key, value]) => [key, key === 'source' ? value : walk(value)]))
}
