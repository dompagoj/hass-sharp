import { createContext, useContext } from 'solid-js'
import {
  dataOutputs,
  executionPorts,
  inputValue,
  nodeCatalog,
  type GraphEdge,
  type GraphNode,
  type InputSpec,
  type Operand,
  type VisualCatalogSnapshot,
} from '../contract'
import { connectionIndex } from '../connectionIndex'
import { inputType, nodeInputs, outputPort, portLabel, type VisualGraph } from '../graph'
import { nodeIdentity, type NodeIdentity } from '../icons'
import { nodeSummary, type SummaryNames } from '../nodeSummary'
import { typeColor, typeIsList, typeLabel } from '../typeLabels'
import { targetIds, widgetFor } from '../widgets'
import type { VisualDiagnostic } from '../api'
import type { TraceEventStatus, TraceValue } from '../../types'

/** C11 trace statuses and traced values (the C10 event status and value shapes). */
export type TraceStatus = TraceEventStatus
export type TracedValue = TraceValue
/** C11 `trace` prop: a node absent from `nodes` was not reached. */
export interface FlowTrace {
  nodes: Record<string, { status: TraceStatus; runs: number; ms?: number }>
  edges: Set<string>
  focusNodeId?: string
  value?(nodeId: string, port: string, side: 'in' | 'out'): TracedValue | undefined
}

/** Handle ids: execution ports keep their backend id (`in` is the execution input); data ports are `data:<port>`. */
export const dataHandle = (port: string) => `data:${port}`
export const dataPortOf = (handle?: string | null) => (handle?.startsWith('data:') ? handle.slice(5) : undefined)

export interface PinView {
  /** The handle id. */
  id: string
  side: 'in' | 'out'
  kind: 'exec' | 'data'
  label: string
  /** Accessible name: side, label, type and state ("Input Brightness, Number, not connected"). */
  name: string
  typeId?: string
  typeName?: string
  color: string
  list: boolean
  connected: boolean
  /** The execution error output: red and secondary. */
  error: boolean
  description?: string
}
/** How an input row shows its value. */
export type InputMode = 'value' | 'connected' | 'muted' | 'wire'
export interface InputView {
  port: string
  pin: PinView
  spec: InputSpec
  mode: InputMode
  /** The wired source, "Trigger · New state". */
  source?: string
  sourceNode?: string
  errors: string[]
}
export interface RowView {
  id: string
  input?: InputView
  output?: PinView
}
export interface Problem {
  severity: number
  message: string
  inputId?: string
  portId?: string
}
export interface NodeView {
  id: string
  kind: string
  known: boolean
  title: string
  kindLabel: string
  summary: string
  identity: NodeIdentity
  execIn?: PinView
  /** The default execution output, drawn at the right of the header. */
  next?: PinView
  rows: RowView[]
  /** The error output, a secondary row at the bottom. */
  footer?: PinView
  problems: Problem[]
  errors: number
  warnings: number
  /** Entities the node acts on, for state options of its inputs. */
  targetEntityIds: string[]
  /** Not reachable by execution from a trigger: drawn muted. */
  unreachable: boolean
  /** Accessible name of the whole card. */
  label: string
}

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const wellFormedNode = (node: unknown): node is GraphNode => record(node) && typeof node.id === 'string' && node.id !== '' && typeof node.kind === 'string'
const wellFormedEdge = (edge: unknown): edge is GraphEdge =>
  record(edge) && typeof edge.id === 'string' && typeof edge.source === 'string' && typeof edge.target === 'string'
/**
 * The graph without malformed entries: null or id-less nodes and edges, duplicate node ids, nodes without a config
 * object and edges to missing nodes are left out, so rendering never throws. The original object is returned when
 * nothing was dropped (the connection index is cached per graph object).
 */
export function renderableGraph(graph: VisualGraph): VisualGraph {
  const rawNodes: unknown[] = Array.isArray(graph?.nodes) ? graph.nodes : []
  const rawEdges: unknown[] = Array.isArray(graph?.edges) ? graph.edges : []
  const ids = new Set<string>()
  const nodes = rawNodes.filter((node): node is GraphNode => {
    if (!wellFormedNode(node) || !record(node.config) || ids.has(node.id)) return false
    ids.add(node.id)
    return true
  })
  const edges = rawEdges.filter((edge): edge is GraphEdge => wellFormedEdge(edge) && ids.has(edge.source) && ids.has(edge.target))
  return nodes.length === rawNodes.length && edges.length === rawEdges.length && graph.nodes === rawNodes ? graph : { ...graph, nodes, edges }
}

export const nodeTitle = (node: GraphNode | undefined, catalog: VisualCatalogSnapshot, fallback = '') =>
  node ? (text(node.label) || (Object.hasOwn(catalog.nodes, node.kind) ? catalog.nodes[node.kind].label : node.kind)) : fallback
const text = (value: unknown) => (typeof value === 'string' ? value.trim() : '')

/** Nodes whose `next` means "after the structure" are drawn with a named Done row instead of a header pin. */
const structured = (node: GraphNode, catalog: VisualCatalogSnapshot) =>
  node.kind === 'parallel' || (Object.hasOwn(catalog.nodes, node.kind) && (catalog.nodes[node.kind].scopePorts?.length ?? 0) > 0)
export function execLabel(node: GraphNode, port: string, catalog: VisualCatalogSnapshot) {
  if (port === 'next' && structured(node, catalog)) return 'Done'
  return portLabel(port)
}
const connectedText = (connected: boolean) => (connected ? 'connected' : 'not connected')

/** Everything the per-node view depends on besides the node itself. */
export interface ViewContext {
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  diagnostics: readonly VisualDiagnostic[]
  names?: SummaryNames
  /** A connection the adapter just refused, shown on its pin. */
  rejected?: { nodeId: string; handle: string; message: string }
}

const typeName = (types: string[], catalog: VisualCatalogSnapshot) => {
  const names = [...new Set(types.map(t => typeLabel(t, catalog)))]
  return names.length > 3 ? 'Any value' : names.join(' or ')
}

const unreachable = (node: GraphNode, ctx: ViewContext) => {
  const analysis = connectionIndex(ctx.graph, ctx.catalog).analysis
  return analysis.complete && node.kind !== 'trigger' && !analysis.scopes.has(node.id)
}
/** The card of one node: pins, rows, summary and problems. Pure: the adapter caches it per node content. */
export function buildNodeView(node: GraphNode, ctx: ViewContext): NodeView {
  const { graph, catalog } = ctx
  const known = Object.hasOwn(catalog.nodes, node.kind)
  const spec = known ? catalog.nodes[node.kind] : undefined
  const identity = nodeIdentity(node.kind, catalog)
  const title = nodeTitle(node, catalog)
  const incoming = graph.edges.filter(e => e.target === node.id)
  const outgoing = graph.edges.filter(e => e.source === node.id)
  const problems: Problem[] = ctx.diagnostics
    .filter(d => d.nodeId === node.id && d.severity >= 2)
    .map(d => ({ severity: d.severity, message: d.message, ...(d.inputId ? { inputId: d.inputId } : {}), ...(d.portId ? { portId: d.portId } : {}) }))
  const rejected = ctx.rejected?.nodeId === node.id ? ctx.rejected : undefined
  if (rejected) problems.unshift({ severity: 3, message: rejected.message, inputId: dataPortOf(rejected.handle) ?? rejected.handle })
  const messagesFor = (port: string) => problems.filter(p => p.severity === 3 && (p.inputId === port || p.portId === port)).map(p => p.message)

  // Execution pins.
  const execPorts = executionPorts(node, catalog)
  const execConnected = (port: string) => outgoing.some(e => e.kind !== 'data' && outputPort(node.kind, e) === port)
  const execPin = (port: string): PinView => {
    const label = execLabel(node, port, catalog)
    const connected = execConnected(port)
    return {
      id: port, side: 'out', kind: 'exec', label, name: `${label} execution output, ${connectedText(connected)}`,
      color: port === 'error' ? 'var(--hs-port-error)' : 'var(--hs-port-execution)', list: false, connected, error: port === 'error',
    }
  }
  const execInConnected = incoming.some(e => e.kind !== 'data')
  const execIn: PinView | undefined = node.kind === 'trigger' || !known ? undefined : {
    id: 'in', side: 'in', kind: 'exec', label: 'Execution input', name: `Execution input, ${connectedText(execInConnected)}`,
    color: 'var(--hs-port-execution)', list: false, connected: execInConnected, error: false,
  }
  const headerNext = execPorts.includes('next') && !structured(node, catalog)
  const namedExec = execPorts.filter(p => p !== 'error' && !(p === 'next' && headerNext))

  // Data inputs.
  const inputs: InputView[] = Object.entries(nodeInputs(graph, node, catalog)).map(([port, input]) => {
    const wire = incoming.find(e => e.kind === 'data' && e.targetPort === port)
    const sourceNode = wire ? graph.nodes.find(n => n.id === wire.source) : undefined
    const sourceOutput = wire?.kind === 'data' && sourceNode ? dataOutputs(sourceNode, catalog)[wire.sourcePort] : undefined
    const resolved = inputType(graph, node, port, input, catalog)
    const typeId = resolved ?? (input.types.length === 1 ? input.types[0] : undefined)
    const typeText = typeId ? typeLabel(typeId, catalog) : typeName(input.types, catalog) || 'Any value'
    const label = input.label ?? port
    const source = wire?.kind === 'data' ? `${nodeTitle(sourceNode, catalog, wire.source)} · ${sourceOutput?.label ?? wire.sourcePort}` : undefined
    const operand = inputValue(node, port, catalog) as Operand | undefined
    const unset = !record(operand) || operand.kind === 'unset'
    const mode: InputMode = wire ? 'connected' : widgetFor(input, catalog).kind === 'none' ? 'wire' : unset && !input.required ? 'muted' : 'value'
    const errors = messagesFor(port)
    return {
      port, spec: input, mode, source, sourceNode: wire?.source, errors,
      pin: {
        id: dataHandle(port), side: 'in', kind: 'data', label, typeId, typeName: typeText,
        name: `Input ${label}, ${typeText}, ${source ? `connected to ${source}` : unset || mode === 'wire' ? 'not connected' : 'not connected, has a value'}`,
        color: typeId ? typeColor(typeId, catalog) : 'var(--hs-muted)', list: typeId ? typeIsList(typeId, catalog) : false,
        connected: !!wire, error: false, description: input.description,
      },
    }
  })
  // Data outputs.
  const outputs: PinView[] = Object.entries(dataOutputs(node, catalog)).map(([port, output]) => {
    const connected = outgoing.some(e => e.kind === 'data' && e.sourcePort === port)
    const label = output.label ?? port
    const typeText = typeLabel(output.type, catalog)
    return {
      id: dataHandle(port), side: 'out', kind: 'data', label, typeId: output.type, typeName: typeText,
      name: `Output ${label}, ${typeText}, ${connectedText(connected)}`, color: typeColor(output.type, catalog),
      list: typeIsList(output.type, catalog), connected, error: false, description: output.description,
    }
  })
  const right = [...namedExec.map(execPin), ...outputs]
  const rows: RowView[] = Array.from({ length: Math.max(inputs.length, right.length) }, (_, i) => ({
    id: `${inputs[i]?.port ?? ''}|${right[i]?.id ?? ''}`,
    ...(inputs[i] ? { input: inputs[i] } : {}),
    ...(right[i] ? { output: right[i] } : {}),
  }))
  const summary = nodeSummary(node, catalog, ctx.names, graph)
  const errors = problems.filter(p => p.severity === 3).length
  const warnings = problems.filter(p => p.severity === 2).length
  const target = node.config.target
  const targetEntityIds = record(target) && target.kind === 'literal' ? targetIds(target.value as never).entity_id : []
  const problemText = errors || warnings ? `. ${[errors && `${errors} error${errors === 1 ? '' : 's'}`, warnings && `${warnings} warning${warnings === 1 ? '' : 's'}`].filter(Boolean).join(', ')}: ${problems.map(p => p.message).join('; ')}` : ''
  return {
    id: node.id, kind: node.kind, known, title, kindLabel: spec?.label ?? node.kind, summary, identity,
    execIn, next: headerNext ? execPin('next') : undefined, rows,
    footer: execPorts.includes('error') ? execPin('error') : undefined,
    problems, errors, warnings, targetEntityIds, unreachable: unreachable(node, ctx),
    label: `${title}, ${identity.label}. ${summary}${problemText}`,
  }
}

/**
 * What a node's view depends on, as one string: its content, the wires touching it (with their source names),
 * the types the validator resolved for its inputs, its diagnostics and a refused connection. Equal keys give an
 * equal view, so the adapter reuses it instead of deriving ports, summaries and diagnostics again.
 */
export function viewKey(node: GraphNode, ctx: ViewContext, index = connectionIndex(ctx.graph, ctx.catalog)): string {
  const titles = (id: string) => nodeTitle(ctx.graph.nodes.find(n => n.id === id), ctx.catalog, id)
  const wires = ctx.graph.edges
    .filter(e => e.source === node.id || e.target === node.id)
    .map(e => (e.kind === 'data' ? ['d', e.source, e.sourcePort, e.target, e.targetPort, e.target === node.id ? titles(e.source) : ''] : ['x', e.source, e.port ?? '', e.target]))
  const diagnostics = ctx.diagnostics.filter(d => d.nodeId === node.id).map(d => [d.severity, d.message, d.inputId ?? '', d.portId ?? ''])
  const rejected = ctx.rejected?.nodeId === node.id ? ctx.rejected : undefined
  // Labels of the nodes a summary may name (wired sources) are already in `wires`.
  return JSON.stringify([node.kind, node.label ?? null, node.config, wires, unreachable(node, ctx), index.analysis.resolved.get(node.id) ?? null, diagnostics, rejected ?? null])
}

/** Variables visible to inline editors: named `output`s of nodes with a value output (D7). */
export function graphVariables(graph: VisualGraph, catalog: VisualCatalogSnapshot = nodeCatalog) {
  return graph.nodes.flatMap(node => {
    const output = node.config.output, value = dataOutputs(node, catalog).value
    return typeof output === 'string' && output && value ? [{ name: output, type: value.type }] : []
  })
}

export interface EdgeView {
  id: string
  source: string
  target: string
  sourceHandle: string
  targetHandle: string
  kind: 'exec' | 'data'
  color: string
  error: boolean
  label: string
}
/** The wire of one graph edge; `undefined` for an edge whose endpoints are missing. */
export function buildEdgeView(edge: GraphEdge, graph: VisualGraph, catalog: VisualCatalogSnapshot): EdgeView | undefined {
  const source = graph.nodes.find(n => n.id === edge.source), target = graph.nodes.find(n => n.id === edge.target)
  if (!source || !target) return
  if (edge.kind === 'data') {
    const output = dataOutputs(source, catalog)[edge.sourcePort]
    const input = nodeInputs(graph, target, catalog)[edge.targetPort]
    return {
      id: edge.id, source: edge.source, target: edge.target, sourceHandle: dataHandle(edge.sourcePort), targetHandle: dataHandle(edge.targetPort),
      kind: 'data', color: typeColor(output?.type ?? 'object', catalog), error: false,
      label: `Data wire from ${nodeTitle(source, catalog)} · ${output?.label ?? edge.sourcePort} to ${nodeTitle(target, catalog)} · ${input?.label ?? edge.targetPort}`,
    }
  }
  const port = outputPort(source.kind, edge)
  return {
    id: edge.id, source: edge.source, target: edge.target, sourceHandle: port, targetHandle: 'in', kind: 'exec',
    color: port === 'error' ? 'var(--hs-port-error)' : 'var(--hs-port-execution)', error: port === 'error',
    label: `Execution wire from ${nodeTitle(source, catalog)} · ${execLabel(source, port, catalog)} to ${nodeTitle(target, catalog)}`,
  }
}

/** "340 ms", "1.2 s", "2 min 5 s". */
export function durationLabel(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return ''
  if (ms < 1000) return `${Math.round(ms)} ms`
  if (ms < 60000) return `${Math.round(ms / 100) / 10} s`
  return `${Math.floor(ms / 60000)} min ${Math.round((ms % 60000) / 1000)} s`
}
export const traceStatusLabel: Record<TraceStatus, string> = {
  ok: 'Ran', error: 'Failed, error path taken', failed: 'Failed', running: 'Running', cancelled: 'Cancelled', stopped: 'Stopped the run',
}

/** A pin's role while a wire is being dragged (or a pin was picked from the keyboard). */
export type PinState = 'origin' | 'compatible' | 'incompatible'
/** What the adapter shares with its cards, pins and wires. */
export interface FlowContextValue {
  readOnly(): boolean
  catalog(): VisualCatalogSnapshot
  view(id: string): NodeView | undefined
  graphNode(id: string): GraphNode | undefined
  variables(): { name: string; type: string }[]
  /** False without the query client and Home Assistant context (a bare adapter): editors become value chips. */
  editable(): boolean
  trace(): FlowTrace | undefined
  pinState(nodeId: string, handle: string, type: 'source' | 'target'): PinState | undefined
  activateHandle(nodeId: string, handle: string, type: 'source' | 'target'): void
  /** True when the pointer moved since it went down (a drag, not a click). */
  moved(): boolean
  setInput(nodeId: string, port: string, operand: Operand, options?: { coalesce?: string }): void
  locateProblem(nodeId: string): void
}
export const FlowContext = createContext<FlowContextValue>()
export function useFlow(): FlowContextValue {
  const context = useContext(FlowContext)
  if (!context) throw new Error('Node cards render inside the flow adapter')
  return context
}
