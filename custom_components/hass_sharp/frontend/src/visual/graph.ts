import { createSignal } from 'solid-js'
import { createUuid } from './id'
import { typeLabel } from './typeLabels'
import {
  addGraphNode,
  executionPorts,
  dataInputs,
  dataOutputs,
  inputValue,
  setInput,
  connectData,
  resolveTypeSpec,
  nodeCatalog,
  type Graph,
  type GraphNode,
  type GraphEdge,
  type Operand,
  type VisualCatalogSnapshot,
  type JsonValue,
  type InputSpec,
} from './contract'
import { connectionIndex } from './connectionIndex'
export type { GraphNode, GraphEdge, Operand } from './contract'
export type VisualGraph = Graph
export type NodeKind = string
export type EdgePort = string
export type ValueType = string
export type NodeConfigData = Record<string, unknown>
export const literal = (type: string, value: JsonValue): Operand => ({ kind: 'literal', type, value })
export { effectiveTriggerMethod as triggerMethod } from './contract'
import { effectiveTriggerMethod } from './contract'
export const catalogPalette = (catalog: VisualCatalogSnapshot = nodeCatalog) =>
  Object.entries(catalog.nodes).map(([kind, spec]) => ({ kind, ...spec }))
export const catalogCategories = (catalog: VisualCatalogSnapshot = nodeCatalog) => [
  ...new Set(catalogPalette(catalog).map(item => item.category)),
]
export const portLabels: Record<string, string> = {
  body: 'Body',
  condition: 'Condition',
  next: 'Next',
  true: 'If true',
  false: 'If false',
  error: 'On error',
}
export const portLabel = (id: string) => portLabels[id] ?? (id.startsWith('branch:') ? `Branch ${id.slice(7)}` : id)
/** Display metadata never changes a node's stable source-derived kind or ID. */
export const nodeLabel = (node: GraphNode | undefined, catalog: VisualCatalogSnapshot = nodeCatalog, fallback = '') =>
  node ? (node.label ?? catalog.nodes[node.kind]?.label ?? node.kind) : fallback
export const typeCategory = (type: string, catalog: VisualCatalogSnapshot = nodeCatalog) =>
  (resolveTypeSpec(type, catalog)?.category ?? 'Object').toLowerCase()
export const outputPorts = (kind: string, catalog: VisualCatalogSnapshot = nodeCatalog) => catalog.nodes[kind]?.execution ?? []
export const edgePort = (edge: GraphEdge) => (edge.kind === 'data' ? edge.sourcePort : (edge.port ?? 'next'))
export const outputPort = (kind: string, edge: GraphEdge) => (kind === 'condition' && edgePort(edge) === 'next' ? 'true' : edgePort(edge))
export const outputType = (node: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog) => dataOutputs(node, catalog).value?.type
export const nodeSummary = (node: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog) =>
  catalog.nodes[node.kind]?.description ?? 'Unknown node kind'
export const visualRunClasses = (graph: VisualGraph) => [
  {
    name: 'VisualAutomation',
    methods: graph.nodes
      .filter(n => n.kind === 'trigger' && n.config.manual !== false)
      .sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
      .map(effectiveTriggerMethod),
  },
]
export const visualActionLabels = (graph: VisualGraph, catalog: VisualCatalogSnapshot = nodeCatalog): Record<string, string> =>
  Object.fromEntries(
    graph.nodes
      .filter(n => n.kind === 'trigger')
      .map(node => [
        `VisualAutomation::${effectiveTriggerMethod(node)}`,
        graph.nodes.some(other => other.id !== node.id && nodeLabel(other, catalog) === nodeLabel(node, catalog))
          ? `${nodeLabel(node, catalog)} · ${node.id}`
          : nodeLabel(node, catalog),
      ]),
  )
export const addNode = (graph: VisualGraph, kind: string, id = createUuid(), catalog: VisualCatalogSnapshot = nodeCatalog) =>
  addGraphNode(graph, kind, id, catalog)
export function createGraph(name: string): VisualGraph {
  let graph: VisualGraph = { version: 5, name, nodes: [], edges: [] }
  for (const [index, kind] of ['trigger', 'condition', 'service'].entries()) {
    const id = `${kind}-1`
    graph = addNode(graph, kind, id)
    const config =
      kind === 'trigger'
        ? { type: 'entity', entity: 'binary_sensor.motion', manual: true, mode: 'Single' }
        : kind === 'condition'
          ? { entity: literal('entity_id', 'binary_sensor.motion'), state: 'on' }
          : { ...graph.nodes.at(-1)!.config, service: 'light.turn_on', target: literal('target', { entity_id: 'light.hall' }) }
    graph = updateNode(graph, id, {
      config,
      ...(kind === 'trigger' ? { label: 'Entity changes' } : {}),
      position: { x: 60 + index * 300, y: 100 },
    })
  }
  return connectNodes(connectNodes(graph, 'trigger-1', 'condition-1'), 'condition-1', 'service-1')
}
export const updateNode = (graph: VisualGraph, id: string, patch: Partial<Omit<GraphNode, 'id' | 'kind'>>): VisualGraph => ({
  ...graph,
  nodes: graph.nodes.map(node => (node.id === id ? { ...node, ...patch } : node)),
})
export function removeEdge(graph: VisualGraph, id: string, catalog: VisualCatalogSnapshot = nodeCatalog): VisualGraph {
  const edge = graph.edges.find(e => e.id === id)
  if (!edge) return graph
  let nodes = graph.nodes
  if (edge.kind === 'data')
    nodes = nodes.map(node => {
      if (node.id !== edge.target || !nodeInputs(graph, node, catalog)[edge.targetPort]) return node
      const next = structuredClone(node)
      setInput(next, edge.targetPort, { kind: 'unset' }, catalog)
      return next
    })
  return { ...graph, nodes, edges: graph.edges.filter(e => e.id !== id) }
}
export function removeNode(graph: VisualGraph, id: string, catalog: VisualCatalogSnapshot = nodeCatalog): VisualGraph {
  let next = graph
  for (const edge of graph.edges.filter(e => e.source === id || e.target === id)) next = removeEdge(next, edge.id, catalog)
  return { ...next, nodes: next.nodes.filter(n => n.id !== id) }
}
export function canConnect(
  graph: VisualGraph,
  source: string,
  target: string,
  port?: string,
  catalog: VisualCatalogSnapshot = nodeCatalog,
): boolean {
  const node = graph.nodes.find(n => n.id === source),
    destination = graph.nodes.find(n => n.id === target)
  if (!node || !destination || source === target || destination.kind === 'trigger') return false
  const resolved = port ?? executionPorts(node, catalog)[0]
  if (
    !executionPorts(node, catalog).includes(resolved) ||
    graph.edges.some(e => e.kind !== 'data' && e.source === source && outputPort(node.kind, e) === resolved)
  )
    return false
  const visited = new Set<string>()
  const reaches = (id: string): boolean => {
    if (id === source) return true
    if (visited.has(id)) return false
    visited.add(id)
    return graph.edges.filter(e => e.kind !== 'data' && e.source === id).some(e => reaches(e.target))
  }
  return !reaches(target)
}
export function connectNodes(
  graph: VisualGraph,
  source: string,
  target: string,
  port?: string,
  catalog: VisualCatalogSnapshot = nodeCatalog,
): VisualGraph {
  if (!canConnect(graph, source, target, port, catalog)) return graph
  const node = graph.nodes.find(n => n.id === source)!,
    resolved = port ?? executionPorts(node, catalog)[0]
  const base = resolved === 'next' ? `${source}->${target}` : `${source}:${resolved}->${target}`
  let id = base
  for (let suffix = 2; graph.edges.some(e => e.id === id); suffix++) id = `${base}#${suffix}`
  return { ...graph, edges: [...graph.edges, { id, source, target, ...(resolved === 'next' ? {} : { port: resolved }) }] }
}
export interface PendingConnection {
  source: string
  port: string
}
export function canCreateNode(
  graph: VisualGraph,
  kind: string,
  pending?: PendingConnection,
  catalog: VisualCatalogSnapshot = nodeCatalog,
): boolean {
  if (!Object.hasOwn(catalog.nodes, kind)) return false
  if (!pending) return true
  let id = '$candidate'
  while (graph.nodes.some(n => n.id === id)) id += '_'
  return canConnect(addNode(graph, kind, id, catalog), pending.source, id, pending.port, catalog)
}
export function createNodeAt(
  graph: VisualGraph,
  kind: string,
  position: { x: number; y: number },
  pending?: PendingConnection,
  catalog: VisualCatalogSnapshot = nodeCatalog,
): VisualGraph {
  if (!canCreateNode(graph, kind, pending, catalog)) return graph
  const added = addNode(graph, kind, createUuid(), catalog),
    next = updateNode(added, added.nodes.at(-1)!.id, { position })
  return pending ? connectNodes(next, pending.source, next.nodes.at(-1)!.id, pending.port, catalog) : next
}
/** Resolve loop-result declarations from its nearest execution body owner. */
export function loopOwner(graph: VisualGraph, node: GraphNode): GraphNode | undefined {
  const seen = new Set<string>()
  const visit = (id: string): GraphNode | undefined => {
    if (seen.has(id)) return
    seen.add(id)
    for (const edge of graph.edges.filter(e => e && e.kind !== 'data' && e.target === id)) {
      const parent = graph.nodes.find(n => n?.id === edge.source)
      if (!parent) continue
      if (edge.kind !== 'data' && edge.port === 'body' && ['repeat', 'foreach', 'while', 'until'].includes(parent.kind)) return parent
      const found = visit(parent.id)
      if (found) return found
    }
  }
  return visit(node.id)
}
export const nodeInputs = (graph: VisualGraph, node: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog) =>
  dataInputs(node, loopOwner(graph, node), catalog)
export function inputType(
  graph: VisualGraph,
  node: GraphNode,
  port: string,
  input: InputSpec,
  catalog: VisualCatalogSnapshot = nodeCatalog,
): string | undefined {
  // Fixed ports present their declared contract; only unions choose the bound value's type.
  if (input.types.length === 1) return input.types[0]
  // The validator's resolution: the wire, the literal, or the first producer visible from this node (D7).
  const resolved = connectionIndex(graph, catalog).analysis.resolved.get(node.id)
  if (resolved && Object.hasOwn(resolved, port)) return resolved[port]
  const edge = graph.edges.find(e => e.kind === 'data' && e.target === node.id && e.targetPort === port)
  if (edge?.kind === 'data') {
    const source = graph.nodes.find(n => n.id === edge.source)
    if (source) return dataOutputs(source, catalog)[edge.sourcePort]?.type
  }
  const value = inputValue(node, port, catalog)
  if (value && typeof value === 'object' && 'type' in value && typeof value.type === 'string') return value.type
  return undefined
}
/**
 * Explain the same immutable typed connection operation used by every interaction. The connection index
 * answers first; `quick` callers (hover) stop there, everyone else also runs the full connectData check.
 */
export function dataConnectionError(
  graph: VisualGraph,
  sourceId: string,
  sourcePort: string,
  targetId: string,
  targetPort: string,
  catalog: VisualCatalogSnapshot = nodeCatalog,
  options: { quick?: boolean } = {},
): string | undefined {
  const source = graph.nodes.find(n => n?.id === sourceId),
    target = graph.nodes.find(n => n?.id === targetId)
  if (!source || !target) return 'Connection endpoint missing'
  const index = connectionIndex(graph, catalog)
  const output = index.remember(JSON.stringify(['outputs', sourceId]), () => dataOutputs(source, catalog))[sourcePort],
    input = index.remember(JSON.stringify(['inputs', targetId]), () => nodeInputs(graph, target, catalog))[targetPort]
  if (!output || !input) return 'Unknown data port'
  if (!input.types.some(t => index.assignable(output.type, t)))
    return `${input.label ?? targetPort} requires ${input.types.map(t => typeLabel(t, catalog)).join(' / ')}; ${output.label ?? sourcePort} outputs ${typeLabel(output.type, catalog)}`
  const quick = index.verdict(sourceId, sourcePort, targetId, targetPort)
  if (quick.message || options.quick || quick.fallback) return quick.message
  try {
    connectData(graph, sourceId, sourcePort, targetId, targetPort, catalog)
  } catch (error) {
    return error instanceof Error ? error.message : String(error)
  }
}
export const serializeGraph = (graph: VisualGraph) => JSON.stringify(graph, null, 2)

export function createGraphHistory(initial: VisualGraph) {
  const [graph, setGraph] = createSignal(initial)
  const [past, setPast] = createSignal<VisualGraph[]>([])
  const [future, setFuture] = createSignal<VisualGraph[]>([])
  // Consecutive commits with the same coalesce key (typing in one field, dragging one slider) are one undo step.
  let coalescing: string | undefined
  return {
    graph,
    reset(next: VisualGraph) {
      coalescing = undefined
      setPast([])
      setFuture([])
      setGraph(next)
    },
    canUndo: () => past().length > 0,
    canRedo: () => future().length > 0,
    /**
     * A commit without `coalesce`, with a different key, or any undo/redo ends the merged run. A commit that
     * changes nothing never starts a run (its state was never stored); with the run's own key it keeps the run going.
     */
    commit(next: VisualGraph, options: { coalesce?: string } = {}) {
      const key = options.coalesce
      if (serializeGraph(next) === serializeGraph(graph())) {
        if (key !== coalescing) coalescing = undefined
        return
      }
      const merge = key !== undefined && key === coalescing && past().length > 0
      coalescing = key
      if (!merge) setPast([...past().slice(-99), graph()])
      setFuture([])
      setGraph(next)
    },
    undo() {
      coalescing = undefined
      if (!past().length) return
      setFuture([graph(), ...future()])
      setGraph(past().at(-1)!)
      setPast(past().slice(0, -1))
    },
    redo() {
      coalescing = undefined
      if (!future().length) return
      setPast([...past(), graph()])
      setGraph(future()[0])
      setFuture(future().slice(1))
    },
  }
}
