import { createSignal } from 'solid-js'
import { createUuid } from './id'
import {
  addV4Node,
  executionPorts,
  dataInputs,
  dataOutputs,
  inputValue,
  setInput,
  connectData,
  compatibleTypes,
  nodeCatalog,
  type GraphV4,
  type V4Node,
  type V4Edge,
  type V4Operand,
  type VisualCatalogSnapshot,
  type JsonValue,
  type InputSpec,
} from './graphV4'
export type VisualGraph = GraphV4
export type GraphNode = V4Node
export type GraphEdge = V4Edge
export type NodeKind = string
export type EdgePort = string
export type Operand = V4Operand
export type ValueType = string
export type NodeConfigData = Record<string, unknown>
export const literal = (type: string, value: JsonValue): V4Operand => ({ kind: 'literal', type, value })
export { effectiveTriggerMethod as triggerMethod } from './graphV4'
import { effectiveTriggerMethod } from './graphV4'
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
export const nodeLabel = (node: V4Node | undefined, catalog: VisualCatalogSnapshot = nodeCatalog, fallback = '') =>
  node ? (node.label ?? catalog.nodes[node.kind]?.label ?? node.kind) : fallback
export const typeLabel = (type: string, catalog: VisualCatalogSnapshot = nodeCatalog) =>
  catalog.types[type]?.label ?? catalog.types[type]?.clrType ?? catalog.types[type]?.description ?? type
export const typeCategory = (type: string, catalog: VisualCatalogSnapshot = nodeCatalog) =>
  (catalog.types[type]?.category ?? 'Object').toLowerCase()
export const typeColor = (type: string, catalog: VisualCatalogSnapshot = nodeCatalog) => `var(--hs-port-${typeCategory(type, catalog)})`
export const outputPorts = (kind: string, catalog: VisualCatalogSnapshot = nodeCatalog) => catalog.nodes[kind]?.execution ?? []
export const edgePort = (edge: V4Edge) => (edge.kind === 'data' ? edge.sourcePort : (edge.port ?? 'next'))
export const outputPort = (kind: string, edge: V4Edge) => (kind === 'condition' && edgePort(edge) === 'next' ? 'true' : edgePort(edge))
export const outputType = (node: V4Node, catalog: VisualCatalogSnapshot = nodeCatalog) => dataOutputs(node, catalog).value?.type
export const nodeSummary = (node: V4Node, catalog: VisualCatalogSnapshot = nodeCatalog) =>
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
  addV4Node(graph, kind, id, catalog)
export function createGraph(name: string): VisualGraph {
  let graph: VisualGraph = { version: 4, name, nodes: [], edges: [] }
  for (const [index, kind] of ['trigger', 'condition', 'service'].entries()) {
    const id = `${kind}-1`
    graph = addNode(graph, kind, id)
    const config =
      kind === 'trigger'
        ? { type: 'entity', entity: 'binary_sensor.motion', manual: true, mode: 'Single' }
        : kind === 'condition'
          ? { entity: literal('entity_id', 'binary_sensor.motion'), state: 'on' }
          : { ...graph.nodes.at(-1)!.config, service: 'light.turn_on', entity: 'light.hall' }
    graph = updateNode(graph, id, {
      config,
      ...(kind === 'trigger' ? { label: 'Entity changes' } : {}),
      position: { x: 60 + index * 300, y: 100 },
    })
  }
  return connectNodes(connectNodes(graph, 'trigger-1', 'condition-1'), 'condition-1', 'service-1')
}
export const updateNode = (graph: VisualGraph, id: string, patch: Partial<Omit<V4Node, 'id' | 'kind'>>): VisualGraph => ({
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
export function loopOwner(graph: VisualGraph, node: V4Node): V4Node | undefined {
  const seen = new Set<string>()
  const visit = (id: string): V4Node | undefined => {
    if (seen.has(id)) return
    seen.add(id)
    for (const edge of graph.edges.filter(e => e.kind !== 'data' && e.target === id)) {
      const parent = graph.nodes.find(n => n.id === edge.source)
      if (!parent) continue
      if (edge.kind !== 'data' && edge.port === 'body' && ['repeat', 'foreach', 'while', 'until'].includes(parent.kind)) return parent
      const found = visit(parent.id)
      if (found) return found
    }
  }
  return visit(node.id)
}
export const nodeInputs = (graph: VisualGraph, node: V4Node, catalog: VisualCatalogSnapshot = nodeCatalog) =>
  dataInputs(node, loopOwner(graph, node), catalog)
export function inputType(
  graph: VisualGraph,
  node: V4Node,
  port: string,
  input: InputSpec,
  catalog: VisualCatalogSnapshot = nodeCatalog,
): string | undefined {
  const edge = graph.edges.find(e => e.kind === 'data' && e.target === node.id && e.targetPort === port)
  if (edge?.kind === 'data') {
    const source = graph.nodes.find(n => n.id === edge.source)
    if (source) return dataOutputs(source, catalog)[edge.sourcePort]?.type
  }
  const value = inputValue(node, port, catalog)
  if (value && typeof value === 'object') {
    if ('type' in value && typeof value.type === 'string') return value.type
    if ('kind' in value && value.kind === 'variable' && 'name' in value) {
      const producer = graph.nodes.find(n => n.config.output === value.name)
      if (producer) return dataOutputs(producer, catalog).value?.type
    }
  }
  return input.types.length === 1 ? input.types[0] : undefined
}
/** Explain the same immutable typed connection operation used by every interaction. */
export function dataConnectionError(
  graph: VisualGraph,
  sourceId: string,
  sourcePort: string,
  targetId: string,
  targetPort: string,
  catalog: VisualCatalogSnapshot = nodeCatalog,
): string | undefined {
  const source = graph.nodes.find(n => n.id === sourceId),
    target = graph.nodes.find(n => n.id === targetId)
  if (!source || !target) return 'Connection endpoint missing'
  const output = dataOutputs(source, catalog)[sourcePort],
    input = nodeInputs(graph, target, catalog)[targetPort]
  if (!output || !input) return 'Unknown data port'
  if (!input.types.some(t => compatibleTypes(output.type, t, catalog)))
    return `${input.label ?? targetPort} requires ${input.types.map(t => typeLabel(t, catalog)).join(' / ')}; ${output.label ?? sourcePort} outputs ${typeLabel(output.type, catalog)}`
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
  return {
    graph,
    reset(next: VisualGraph) {
      setPast([])
      setFuture([])
      setGraph(next)
    },
    canUndo: () => past().length > 0,
    canRedo: () => future().length > 0,
    commit(next: VisualGraph) {
      if (serializeGraph(next) === serializeGraph(graph())) return
      setPast([...past().slice(-99), graph()])
      setFuture([])
      setGraph(next)
    },
    undo() {
      if (!past().length) return
      setFuture([graph(), ...future()])
      setGraph(past().at(-1)!)
      setPast(past().slice(0, -1))
    },
    redo() {
      if (!future().length) return
      setPast([...past(), graph()])
      setGraph(future()[0])
      setFuture(future().slice(1))
    },
  }
}
