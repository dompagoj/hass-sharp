import { addGraphNode, connectData, dataOutputs, executionPorts, isAssignable, type GraphNode, type VisualCatalogSnapshot } from '../contract'
import { canConnect, connectNodes, nodeInputs, outputPort, updateNode, type VisualGraph } from '../graph'
import { createUuid } from '../id'
import { dataPortOf } from './flowModel'

/** A pin a new step is connected to: the node, the handle id and the side (`source` is an output). */
export interface PinRef {
  nodeId: string
  handle: string
  type: 'source' | 'target'
}
type Point = { x: number; y: number }

const freeId = (graph: VisualGraph) => {
  let id = '$candidate'
  while (graph.nodes.some(n => n?.id === id)) id += '_'
  return id
}
/** The output a new step continues from: `next`, else its first port that is not the error path. */
const defaultExec = (node: GraphNode, catalog: VisualCatalogSnapshot) => {
  const ports = executionPorts(node, catalog)
  return ports.includes('next') ? 'next' : ports.find(port => port !== 'error')
}
/** A value only a loop's body (or condition) can see is used inside the body; anything else after the step. */
const runPortFor = (visibility: string | undefined) => (visibility === 'body' || visibility === 'loop' ? 'body' : 'next')
const bestMatch = <T>(entries: [string, T][], exact: (value: T) => boolean, assignable: (value: T) => boolean) =>
  (entries.find(([, value]) => exact(value)) ?? entries.find(([, value]) => assignable(value)))?.[0]

/**
 * The port of a new `kind` step that would connect to the pin, or undefined when that kind has none:
 * - an execution output runs the new step (as `canCreateNode` decides);
 * - an execution input is run by the new step's default output;
 * - a data output feeds the first input of the new step that takes its type (an exact type before a wider one);
 * - a data input is fed by the first output of the new step of a type it takes.
 */
function portFor(graph: VisualGraph, kind: string, pin: PinRef, catalog: VisualCatalogSnapshot, id: string): string | undefined {
  const pinNode = graph.nodes.find(n => n?.id === pin.nodeId)
  if (!pinNode || !Object.hasOwn(catalog.nodes, kind)) return
  let next: VisualGraph
  try {
    next = addGraphNode(graph, kind, id, catalog)
  } catch {
    return
  }
  const node = next.nodes.at(-1)!
  const data = dataPortOf(pin.handle)
  if (data === undefined) {
    if (pin.type === 'source') return canConnect(next, pin.nodeId, id, pin.handle, catalog) ? 'in' : undefined
    const port = defaultExec(node, catalog)
    return port && canConnect(next, id, pin.nodeId, port, catalog) ? port : undefined
  }
  // The new step runs after the pin's step (or just before it), so it needs an execution input.
  if (kind === 'trigger') return
  if (pin.type === 'source') {
    const output = dataOutputs(pinNode, catalog)[data]
    if (!output) return
    const inputs = Object.entries(nodeInputs(next, node, catalog))
    return bestMatch(inputs, input => input.types.includes(output.type), input => input.types.some(t => isAssignable(output.type, t, catalog)))
  }
  const input = nodeInputs(graph, pinNode, catalog)[data]
  if (!input || !executionPorts(node, catalog).includes('next')) return
  const outputs = Object.entries(dataOutputs(node, catalog))
  return bestMatch(outputs, output => input.types.includes(output.type), output => input.types.some(t => isAssignable(output.type, t, catalog)))
}

/** Whether a new `kind` step can be added for this pin (any kind when there is no pin). */
export function canAddFor(graph: VisualGraph, kind: string, pin: PinRef | undefined, catalog: VisualCatalogSnapshot): boolean {
  if (!pin) return Object.hasOwn(catalog.nodes, kind)
  return portFor(graph, kind, pin, catalog, freeId(graph)) !== undefined
}

/** Wires `source.port` to `id`; a wire already on that port now leads through `id` (when it has a `next`). */
function runAfter(graph: VisualGraph, source: string, port: string, id: string, catalog: VisualCatalogSnapshot): VisualGraph {
  const node = graph.nodes.find(n => n.id === source)!
  const existing = graph.edges.find(e => e.kind !== 'data' && e.source === source && outputPort(node.kind, e) === port)
  if (!existing) return connectNodes(graph, source, id, port, catalog)
  const added = graph.nodes.find(n => n.id === id)!
  let next: VisualGraph = { ...graph, edges: graph.edges.filter(e => e !== existing) }
  next = connectNodes(next, source, id, port, catalog)
  return executionPorts(added, catalog).includes('next') ? connectNodes(next, id, existing.target, 'next', catalog) : next
}
/** Runs `id` just before `target`: the wires that ran `target` now run `id`, and `id` runs `target`. */
function runBefore(graph: VisualGraph, target: string, id: string, catalog: VisualCatalogSnapshot): VisualGraph {
  const incoming = graph.edges.filter(e => e.kind !== 'data' && e.target === target)
  let next: VisualGraph = { ...graph, edges: graph.edges.filter(e => !incoming.includes(e)) }
  for (const edge of incoming) {
    const source = next.nodes.find(n => n.id === edge.source)
    if (source) next = connectNodes(next, edge.source, id, outputPort(source.kind, edge), catalog)
  }
  return connectNodes(next, id, target, 'next', catalog)
}

/**
 * Adds a `kind` step at `position` connected to the pin, as one graph change. A data output also runs the new step
 * after the pin's step (on its free `next`, or inside the body for a value only the body sees; a wire already there
 * now leads through the new step), and a data input runs it just before the pin's step, so the value is there when
 * it is read. The data wire is validated once; when it is refused the step is still added and `error` says why.
 */
export function addConnected(
  graph: VisualGraph,
  kind: string,
  position: Point,
  pin: PinRef,
  catalog: VisualCatalogSnapshot,
): { graph: VisualGraph; nodeId: string; error?: string; refused?: { nodeId: string; handle: string } } | undefined {
  const id = createUuid()
  const port = portFor(graph, kind, pin, catalog, id)
  if (port === undefined) return
  let next = updateNode(addGraphNode(graph, kind, id, catalog), id, { position })
  const data = dataPortOf(pin.handle)
  if (data === undefined) {
    next = pin.type === 'source' ? connectNodes(next, pin.nodeId, id, pin.handle, catalog) : connectNodes(next, id, pin.nodeId, port, catalog)
    return { graph: next, nodeId: id }
  }
  const pinNode = next.nodes.find(n => n.id === pin.nodeId)!
  let wire: [string, string, string, string]
  if (pin.type === 'source') {
    const run = runPortFor(dataOutputs(pinNode, catalog)[data]?.visibility)
    if (executionPorts(pinNode, catalog).includes(run)) next = runAfter(next, pin.nodeId, run, id, catalog)
    wire = [pin.nodeId, data, id, port]
  } else {
    next = runBefore(next, pin.nodeId, id, catalog)
    wire = [id, port, pin.nodeId, data]
  }
  try {
    return { graph: connectData(next, ...wire, catalog), nodeId: id }
  } catch (error) {
    return { graph: next, nodeId: id, error: error instanceof Error ? error.message : String(error), refused: { nodeId: wire[2], handle: `data:${wire[3]}` } }
  }
}
