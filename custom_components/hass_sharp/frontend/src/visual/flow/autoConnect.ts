import { addGraphNode, analyzeGraph, blockingDiagnostic, connectData, dataOutputs, executionPorts, isAssignable, isPure, withGraphTypes, type GraphEdge, type GraphNode, type VisualCatalogSnapshot } from '../contract'
import { canConnect, connectNodes, nodeInputs, outputPort, removeEdge, updateNode, type VisualGraph } from '../graph'
import { createUuid } from '../id'
import { dataPortOf, nodeTitle } from './flowModel'

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
  /** Where the step was put into the flow ("Added Delay between Motion in the hall and State condition"). */
  notice?: string
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
    // A value node has no execution pins: it is never offered on an execution pin.
    if (isPure(kind, catalog)) return
    if (pin.type === 'source') return canConnect(next, pin.nodeId, id, pin.handle, catalog) ? 'in' : undefined
    const port = defaultExec(node, catalog)
    return port && canConnect(next, id, pin.nodeId, port, catalog) ? port : undefined
  }
  // A value wire runs between steps of one flow: the new step needs an execution input.
  if (kind === 'trigger') return
  // The types the graph's Custom C# nodes declare, beside the catalog's, decide which pins take the value.
  const types = withGraphTypes(catalog, next)
  if (pin.type === 'source') {
    const output = dataOutputs(pinNode, catalog)[data]
    if (!output) return
    const inputs = Object.entries(nodeInputs(next, node, catalog))
    return bestMatch(inputs, input => input.types.includes(output.type), input => input.types.some(t => isAssignable(output.type, t, types)))
  }
  const input = nodeInputs(graph, pinNode, catalog)[data]
  if (!input) return
  const outputs = Object.entries(dataOutputs(node, catalog))
  return bestMatch(outputs, output => input.types.includes(output.type), output => input.types.some(t => isAssignable(output.type, t, types)))
}

/** What the pin is called on its card ("Entity", "If true"), for messages. */
function pinLabel(graph: VisualGraph, pin: PinRef, catalog: VisualCatalogSnapshot): string {
  const node = graph.nodes.find(n => n.id === pin.nodeId)
  const data = dataPortOf(pin.handle)
  if (!node || data === undefined) return pin.handle
  const label = pin.type === 'source' ? dataOutputs(node, catalog)[data]?.label : nodeInputs(graph, node, catalog)[data]?.label
  return label || data
}

/** Steps that run: reached from a trigger along execution wires. */
function running(graph: VisualGraph): Set<string> {
  const reached = new Set(graph.nodes.filter(n => n.kind === 'trigger').map(n => n.id))
  const queue = [...reached]
  while (queue.length) {
    const id = queue.shift()!
    for (const edge of graph.edges) if (edge.kind !== 'data' && edge.source === id && !reached.has(edge.target)) {
      reached.add(edge.target)
      queue.push(edge.target)
    }
  }
  return reached
}

/** `graph` with the execution wire `edge` replaced by source → `id` (same port) and `id` next → its old target. */
function insertOn(graph: VisualGraph, edge: GraphEdge, id: string, catalog: VisualCatalogSnapshot): VisualGraph | undefined {
  const source = graph.nodes.find(n => n.id === edge.source)
  if (!source) return
  const port = outputPort(source.kind, edge)
  const without = removeEdge(graph, edge.id, catalog)
  const head = connectNodes(without, edge.source, id, port, catalog)
  const tail = connectNodes(head, id, edge.target, 'next', catalog)
  return head === without || tail === head ? undefined : tail
}

/**
 * Adds a `kind` step at `position` connected to the pin, as one graph change.
 *
 * An execution pin gets its wire (a value node is never offered there). A new value node gets only its value wire:
 * it is worked out wherever a step reads it, so it never joins the run order. For a data pin a new step must run on
 * the same path as the pin's step for the value wire to be valid, so it is put into the flow where that changes
 * nothing else:
 * - from a data output: into the free `next` (or `body`, for a value only a loop body sees) of the pin's step; or,
 *   when that port has exactly one wire, onto that wire (pin's step → new step → what ran next), if the new kind has
 *   a `next`;
 * - from a data input: onto the one execution wire into the pin's step (what ran before → new step → pin's step), if
 *   the new kind has a `next`.
 * Then the value wire is made, and `notice` says where the step was put. When there is no such place (the new kind has
 * no `next`, the pin's step has several incoming wires, or does not run at all) or the value wire is refused there,
 * the step is added where it was dropped with no wire changed, plus the value wire if the editor accepts one to a
 * step not in the flow yet (`notice` then says to connect it into the flow); if it does not, `error` says in plain
 * words what to connect.
 * Every graph it would return is checked as the editor checks it: returns undefined when the step would carry a
 * blocking problem wherever this puts it, in the flow or on its own (a condition result anywhere but a loop
 * condition), so `canAddFor` does not offer the kind and a drop adds nothing.
 */
export function addConnected(graph: VisualGraph, kind: string, position: Point, pin: PinRef, catalog: VisualCatalogSnapshot, id = createUuid()): Connected | undefined {
  const port = portFor(graph, kind, pin, catalog, id)
  if (port === undefined) return
  const placed = updateNode(addGraphNode(graph, kind, id, catalog), id, { position })
  const blocked = (value: VisualGraph) => analyzeGraph(value, catalog).diagnostics.some(d => d.nodeId === id && d.severity === 3 && blockingDiagnostic(d))
  const data = dataPortOf(pin.handle)
  if (data === undefined) {
    const wired = pin.type === 'source' ? connectNodes(placed, pin.nodeId, id, pin.handle, catalog) : connectNodes(placed, id, pin.nodeId, port, catalog)
    return blocked(wired) ? undefined : { graph: wired, nodeId: id }
  }
  const pinNode = placed.nodes.find(n => n.id === pin.nodeId)!
  const added = placed.nodes.find(n => n.id === id)!
  const wire: [string, string, string, string] = pin.type === 'source' ? [pin.nodeId, data, id, port] : [id, port, pin.nodeId, data]
  const kindName = kindLabel(kind, catalog), pinName = pinLabel(graph, pin, catalog)
  const tryValue = (value: VisualGraph) => {
    try {
      return connectData(value, ...wire, catalog)
    } catch {
      return undefined
    }
  }
  // A value node is connected by its value wire only: it is worked out wherever a step reads it.
  if (isPure(added, catalog)) {
    const valued = tryValue(placed)
    if (valued && !blocked(valued)) return { graph: valued, nodeId: id, notice: `Added ${kindName} and connected ${pinName}` }
    return blocked(placed) ? undefined : { graph: placed, nodeId: id, refused: { nodeId: wire[2], handle: `data:${wire[3]}` }, error: `Added ${kindName}. Connect ${pinName} to it.` }
  }
  const hasNext = executionPorts(added, catalog).includes('next')
  const runs = running(graph).has(pin.nodeId)
  const title = (nodeId: string) => nodeTitle(placed.nodes.find(n => n.id === nodeId), catalog, nodeId)
  let ordered: VisualGraph | undefined
  let between: [string, string] | undefined
  if (runs && pin.type === 'source') {
    const run = runPortFor(dataOutputs(pinNode, catalog)[data]?.visibility)
    const taken = placed.edges.filter(e => e.kind !== 'data' && e.source === pin.nodeId && outputPort(pinNode.kind, e) === run)
    if (!executionPorts(pinNode, catalog).includes(run)) ordered = undefined
    else if (!taken.length) {
      const wired = connectNodes(placed, pin.nodeId, id, run, catalog)
      ordered = wired === placed ? undefined : wired
    } else if (taken.length === 1 && hasNext) {
      ordered = insertOn(placed, taken[0], id, catalog)
      if (ordered) between = [pin.nodeId, taken[0].target]
    }
  } else if (runs) {
    const into = placed.edges.filter(e => e.kind !== 'data' && e.target === pin.nodeId)
    if (into.length === 1 && hasNext) {
      ordered = insertOn(placed, into[0], id, catalog)
      if (ordered) between = [into[0].source, pin.nodeId]
    }
  }
  /**
   * The step where it was dropped, with no wire changed, and the value wire when the editor accepts it for a step
   * that is not in the flow yet; in plain words either way (never the validator's sentence about scopes).
   */
  const alone = (): Connected | undefined => {
    const valued = tryValue(placed)
    if (valued && !blocked(valued)) return { graph: valued, nodeId: id, notice: `Added ${kindName} and connected ${pinName}. Connect ${kindName} into the flow to run it.` }
    // Not even on its own (a condition result outside a loop condition): it is not offered.
    if (blocked(placed)) return
    return { graph: placed, nodeId: id, refused: { nodeId: wire[2], handle: `data:${wire[3]}` }, error: `Added ${kindName}. Connect it into the flow, then connect ${pinName}.` }
  }
  if (!ordered) return alone()
  // The kind cannot run there at all (a condition result outside a loop condition): it is not offered.
  if (blocked(ordered)) return
  const connected = tryValue(ordered)
  // Never keep an execution wire that was only there for the value.
  if (!connected || blocked(connected)) return alone()
  return { graph: connected, nodeId: id, ...(between ? { notice: `Added ${kindName} between ${title(between[0])} and ${title(between[1])}` } : {}) }
}

/**
 * Whether a new `kind` step can be added for this pin (any kind when there is no pin): the same placement the drop
 * runs, which must not put the new step where it cannot be (a condition result outside a loop condition).
 */
export function canAddFor(graph: VisualGraph, kind: string, pin: PinRef | undefined, catalog: VisualCatalogSnapshot): boolean {
  if (!pin) return Object.hasOwn(catalog.nodes, kind)
  return !!addConnected(graph, kind, { x: 0, y: 0 }, pin, catalog, freeId(graph))
}
