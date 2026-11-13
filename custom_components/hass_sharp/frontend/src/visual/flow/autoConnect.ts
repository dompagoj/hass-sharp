import { addGraphNode, analyzeGraph, blockingDiagnostic, connectData, dataOutputs, executionPorts, isAssignable, type GraphNode, type VisualCatalogSnapshot } from '../contract'
import { canConnect, connectNodes, nodeInputs, updateNode, type VisualGraph } from '../graph'
import { createUuid } from '../id'
import { dataPortOf } from './flowModel'

/** A pin a new step is connected to: the node, the handle id and the side (`source` is an output). */
export interface PinRef {
  nodeId: string
  handle: string
  type: 'source' | 'target'
}
type Point = { x: number; y: number }
export interface Connected {
  graph: VisualGraph
  nodeId: string
  /** Why the value wire was not made, in words; the step is added all the same. */
  error?: string
  /** The input that would have taken the value wire, for the callout. */
  refused?: { nodeId: string; handle: string }
}

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
const kindLabel = (kind: string, catalog: VisualCatalogSnapshot) => (Object.hasOwn(catalog.nodes, kind) ? catalog.nodes[kind].label : kind)

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
  // A value wire runs between steps of one flow: the new step needs an execution input.
  if (kind === 'trigger') return
  if (pin.type === 'source') {
    const output = dataOutputs(pinNode, catalog)[data]
    if (!output) return
    const inputs = Object.entries(nodeInputs(next, node, catalog))
    return bestMatch(inputs, input => input.types.includes(output.type), input => input.types.some(t => isAssignable(output.type, t, catalog)))
  }
  const input = nodeInputs(graph, pinNode, catalog)[data]
  if (!input) return
  const outputs = Object.entries(dataOutputs(node, catalog))
  return bestMatch(outputs, output => input.types.includes(output.type), output => input.types.some(t => isAssignable(output.type, t, catalog)))
}

/** What the pin is called on its card ("Entity", "If true"), for messages. */
function pinLabel(graph: VisualGraph, pin: PinRef, catalog: VisualCatalogSnapshot): string {
  const node = graph.nodes.find(n => n.id === pin.nodeId)
  const data = dataPortOf(pin.handle)
  if (!node || data === undefined) return pin.handle
  const label = pin.type === 'source' ? dataOutputs(node, catalog)[data]?.label : nodeInputs(graph, node, catalog)[data]?.label
  return label || data
}

/**
 * Adds a `kind` step at `position` connected to the pin, as one graph change, without changing or removing any wire
 * already there:
 * - an execution pin gets its wire;
 * - a data output feeds the new step, which also runs after the pin's step when that port (`next`, or `body` for a
 *   value only the body sees) is free;
 * - a data input is fed by the new step, which also runs just before the pin's step when nothing runs that step yet.
 * The value wire is validated once. When it is refused (a step that does not run yet cannot pass a value) the step is
 * still added where it was dropped, without the value wire, and `error` says what to do.
 */
export function addConnected(graph: VisualGraph, kind: string, position: Point, pin: PinRef, catalog: VisualCatalogSnapshot, id = createUuid()): Connected | undefined {
  const port = portFor(graph, kind, pin, catalog, id)
  if (port === undefined) return
  const placed = updateNode(addGraphNode(graph, kind, id, catalog), id, { position })
  const data = dataPortOf(pin.handle)
  if (data === undefined) {
    const wired = pin.type === 'source' ? connectNodes(placed, pin.nodeId, id, pin.handle, catalog) : connectNodes(placed, id, pin.nodeId, port, catalog)
    return { graph: wired, nodeId: id }
  }
  const pinNode = placed.nodes.find(n => n.id === pin.nodeId)!
  let ordered = placed
  let wire: [string, string, string, string]
  if (pin.type === 'source') {
    const run = runPortFor(dataOutputs(pinNode, catalog)[data]?.visibility)
    // connectNodes leaves the graph as it was when the port is taken or the wire would make a loop.
    if (executionPorts(pinNode, catalog).includes(run)) ordered = connectNodes(placed, pin.nodeId, id, run, catalog)
    wire = [pin.nodeId, data, id, port]
  } else {
    const added = placed.nodes.find(n => n.id === id)!
    const runs = placed.edges.some(e => e.kind !== 'data' && e.target === pin.nodeId)
    if (!runs && executionPorts(added, catalog).includes('next')) ordered = connectNodes(placed, id, pin.nodeId, 'next', catalog)
    wire = [id, port, pin.nodeId, data]
  }
  const refused = { nodeId: wire[2], handle: `data:${wire[3]}` }
  try {
    return { graph: connectData(ordered, ...wire, catalog), nodeId: id }
  } catch (error) {
    // Without an execution wire the new step does not run yet, so it cannot pass or receive a value.
    if (ordered === placed)
      return { graph: placed, nodeId: id, refused, error: `Added ${kindLabel(kind, catalog)}. Connect it into the flow, then connect ${pinLabel(graph, pin, catalog)}.` }
    return { graph: ordered, nodeId: id, refused, error: error instanceof Error ? error.message : String(error) }
  }
}

/**
 * Whether a new `kind` step can be added for this pin (any kind when there is no pin): the same placement the drop
 * runs, which must not put the new step where it cannot be (a condition result outside a loop condition).
 */
export function canAddFor(graph: VisualGraph, kind: string, pin: PinRef | undefined, catalog: VisualCatalogSnapshot): boolean {
  if (!pin) return Object.hasOwn(catalog.nodes, kind)
  const id = freeId(graph)
  const result = addConnected(graph, kind, { x: 0, y: 0 }, pin, catalog, id)
  if (!result) return false
  return !analyzeGraph(result.graph, catalog).diagnostics.some(d => d.nodeId === id && d.severity === 3 && blockingDiagnostic(d))
}
