import { createSignal } from 'solid-js'
import { createUuid } from './id'

// Domain data only. Library objects, JSX, selection and measured sizes never enter JSON.
export type NodeKind = 'trigger' | 'condition' | 'service' | 'delay' | 'custom' | 'if' | 'and' | 'or' | 'xor' | 'not' | 'compare' | 'read_entity' | 'set_value' | 'stop' | 'log' | 'repeat' | 'wait_until'
export type ValueType = 'boolean' | 'number' | 'string'
export type Operand = { kind: 'literal'; type: ValueType; value: string | boolean | number } | { kind: 'variable'; name: string }
export type NodeConfigData = Record<string, string | boolean | Operand>
export const categories = ['Hass', 'Logic', 'Flow', 'Data', 'Code'] as const
export type NodeCategory = typeof categories[number]
export const literal = (type: ValueType, value: string | boolean | number): Operand => ({ kind: 'literal', type, value })
export interface GraphNode {
  id: string
  kind: NodeKind
  label: string
  position: { x: number; y: number }
  size: { width: number; height: number }
  config: NodeConfigData
}
/** Backend edge ports. A missing port means `next`; on a condition that is its true branch. */
export type EdgePort = 'next' | 'true' | 'false' | 'error' | 'body'
export interface GraphEdge { id: string; source: string; target: string; port?: EdgePort }
export interface VisualGraph { version: 1 | 2 | 3; name: string; nodes: GraphNode[]; edges: GraphEdge[]; imports?: string; members?: string }

/** The backend uses uppercase hexadecimal UTF-8, not UTF-16 code units. */
export const triggerMethod = (id: string) => 'Run_' + Array.from(new TextEncoder().encode(id), byte => byte.toString(16).padStart(2, '0').toUpperCase()).join('')
export function visualRunClasses(graph: VisualGraph) {
  return [{ name: 'VisualAutomation', methods: graph.version === 1 ? ['Run'] : graph.nodes
    .filter(node => node.kind === 'trigger' && node.config.manual !== false)
    .sort((a, b) => a.id < b.id ? -1 : a.id > b.id ? 1 : 0).map(node => triggerMethod(node.id)) }]
}
export function visualActionLabels(graph: VisualGraph): Record<string, string> {
  const triggers = graph.nodes.filter(node => node.kind === 'trigger')
  return Object.fromEntries(triggers.map(node => {
    const label = node.label || node.id
    const duplicate = triggers.some(other => other.id !== node.id && (other.label || other.id) === label)
    return [`VisualAutomation::${graph.version === 1 ? 'Run' : triggerMethod(node.id)}`, duplicate ? `${label} · ${node.id}` : label]
  }))
}

export const palette: { kind: NodeKind; label: string; category: NodeCategory; description: string; config: NodeConfigData }[] = [
  { kind: 'trigger', category: 'Hass', label: 'Entity changes', description: 'Start with an entity, schedule or manual trigger', config: { entity: 'binary_sensor.motion' } },
  { kind: 'condition', category: 'Hass', label: 'State condition', description: 'Branch on an entity’s state', config: { entity: 'binary_sensor.motion', state: 'on' } },
  { kind: 'service', category: 'Hass', label: 'Call service', description: 'Ask Home Assistant to act, with JSON data', config: { service: 'light.turn_on', entity: 'light.hall' } },
  { kind: 'read_entity', category: 'Hass', label: 'Read entity', description: 'Read a state or attribute into a typed value', config: { entity: 'sensor.temperature', field: 'state', type: 'number', output: 'temperature' } },
  { kind: 'wait_until', category: 'Hass', label: 'Wait until', description: 'Poll a live state or attribute with a timeout', config: { entity: 'binary_sensor.motion', field: 'state', type: 'string', operator: 'eq', right: literal('string', 'on'), timeout: '60', interval: '1' } },
  { kind: 'if', category: 'Logic', label: 'If', description: 'Branch on a boolean value', config: { value: literal('boolean', true) } },
  ...(['and', 'or', 'xor'] as const).map(kind => ({ kind, category: 'Logic' as const, label: kind === 'and' ? 'And' : kind === 'or' ? 'Or' : 'Xor', description: kind === 'and' ? 'True when both inputs are true' : kind === 'or' ? 'True when either input is true' : 'True when exactly one input is true', config: { left: literal('boolean', true), right: literal('boolean', false), output: `${kind}_result` } })),
  { kind: 'not', category: 'Logic', label: 'Not', description: 'Invert a boolean value', config: { value: literal('boolean', true), output: 'not_result' } },
  { kind: 'compare', category: 'Logic', label: 'Compare', description: 'Compare equal values or order numbers', config: { left: literal('number', 0), right: literal('number', 0), operator: 'eq', output: 'comparison' } },
  { kind: 'delay', category: 'Flow', label: 'Wait', description: 'Pause before the next step', config: { seconds: '60' } },
  { kind: 'repeat', category: 'Flow', label: 'Repeat', description: 'Run a separate body up to 1,000 times', config: { count: '3' } },
  { kind: 'stop', category: 'Flow', label: 'Stop', description: 'End the entire invocation, including repeats', config: {} },
  { kind: 'set_value', category: 'Data', label: 'Set value', description: 'Assign or explicitly convert a local typed value', config: { output: 'value', type: 'string', value: literal('string', '') } },
  { kind: 'log', category: 'Data', label: 'Log', description: 'Write a value to the Home Assistant log', config: { level: 'Info', value: literal('string', '') } },
  { kind: 'custom', category: 'Code', label: 'Custom C#', description: 'Keep a code section in the graph', config: { code: '// Add your C# here' } },
]
export const isTypedKind = (kind: NodeKind) => !['trigger', 'condition', 'service', 'delay', 'custom'].includes(kind)
export function outputType(node: GraphNode): ValueType | undefined {
  if (['and', 'or', 'xor', 'not', 'compare'].includes(node.kind)) return 'boolean'
  if (['set_value', 'read_entity'].includes(node.kind)) return node.config.type as ValueType
}
export function nodeSummary(node: GraphNode): string {
  const cfg = node.config
  if (outputType(node)) return `${String(cfg.output)} · ${outputType(node)}`
  if (node.kind === 'repeat') return `${cfg.count} iterations · Body then Next`
  if (node.kind === 'stop') return 'End invocation'
  if (node.kind === 'if') { const value = cfg.value as Operand; return value?.kind === 'variable' ? value.name : String(value?.value ?? '') }
  if (node.kind === 'log') return `${cfg.level} message`
  if (node.kind === 'wait_until') return `${cfg.entity} · timeout ${cfg.timeout}s`
  return String(cfg.entity ?? cfg.seconds ?? cfg.code ?? '')
}

export function addNode(graph: VisualGraph, kind: NodeKind, id: string = createUuid()): VisualGraph {
  const item = palette.find(item => item.kind === kind)!
  const offset = graph.nodes.length * 35
  const config = structuredClone(item.config)
  if (typeof config.output === 'string') {
    const base = config.output
    const used = new Set(graph.nodes.map(node => node.config.output))
    for (let suffix = 2; used.has(config.output); suffix++) config.output = `${base}_${suffix}`
  }
  return { ...graph, version: isTypedKind(kind) ? 3 : graph.version, nodes: [...graph.nodes, { id, kind, label: item.label, config, position: { x: 60 + offset, y: 240 + offset }, size: { width: 220, height: 112 } }] }
}
export interface PendingConnection { source: string; port: EdgePort }
export function canCreateNode(graph: VisualGraph, kind: NodeKind, pending?: PendingConnection): boolean {
  if (kind === 'trigger' && graph.version === 1 && graph.nodes.some(node => node.kind === 'trigger')) return false
  if (!pending) return true
  let id = '$candidate'
  while (graph.nodes.some(node => node.id === id)) id += '_'
  return canConnect(addNode(graph, kind, id), pending.source, id, pending.port)
}
/** Validate against the latest graph, then create and connect as one history entry. */
export function createNodeAt(graph: VisualGraph, kind: NodeKind, position: GraphNode['position'], pending?: PendingConnection): VisualGraph {
  if (!canCreateNode(graph, kind, pending)) return graph
  const next = addNode(graph, kind)
  const id = next.nodes.at(-1)!.id
  const positioned = updateNode(next, id, { position })
  return pending ? connectNodes(positioned, pending.source, id, pending.port) : positioned
}
export function createGraph(name: string): VisualGraph {
  let graph: VisualGraph = { version: 2, name, nodes: [], edges: [] }
  for (const [i, kind] of (['trigger', 'condition', 'service'] as const).entries()) {
    const id = `${kind}-1`
    graph = addNode(graph, kind, id)
    graph = updateNode(graph, id, { position: { x: 60 + i * 300, y: 100 } })
  }
  return connectNodes(connectNodes(graph, 'trigger-1', 'condition-1'), 'condition-1', 'service-1')
}
export function updateNode(graph: VisualGraph, id: string, patch: Partial<Omit<GraphNode, 'id' | 'kind'>>): VisualGraph {
  return { ...graph, nodes: graph.nodes.map(node => node.id === id ? { ...node, ...patch } : node) }
}
export function removeNode(graph: VisualGraph, id: string): VisualGraph {
  return { ...graph, nodes: graph.nodes.filter(node => node.id !== id), edges: graph.edges.filter(edge => edge.source !== id && edge.target !== id) }
}
export const edgePort = (edge: GraphEdge): EdgePort => edge.port ?? 'next'
/** Ports a node offers as handles, in display order. Triggers have no body, so no error path. */
export const outputPorts = (kind: NodeKind): EdgePort[] =>
  kind === 'trigger' ? ['next'] : kind === 'stop' ? [] : kind === 'repeat' ? ['body', 'next', 'error'] : kind === 'condition' || kind === 'if' ? ['true', 'false', 'error'] : ['next', 'error']
/** The handle an existing edge leaves from; legacy `next` on a condition is its true branch. */
export const outputPort = (kind: NodeKind, edge: GraphEdge): EdgePort => kind === 'condition' && edgePort(edge) === 'next' ? 'true' : edgePort(edge)
export const portLabels: Record<EdgePort, string> = { body: 'Body', next: 'Next', true: 'If true', false: 'If false', error: 'On error' }
const kindOf = (graph: VisualGraph, id: string) => graph.nodes.find(n => n.id === id)?.kind

export function canConnect(graph: VisualGraph, source: string, target: string, port?: EdgePort): boolean {
  const kind = kindOf(graph, source)
  if (!kind || source === target || !kindOf(graph, target) || kindOf(graph, target) === 'trigger') return false
  const handle = port === 'next' && kind === 'condition' ? 'true' : port ?? outputPorts(kind)[0]
  // The backend accepts one edge per port, and `next` and `true` on one condition are the same branch.
  if (!outputPorts(kind).includes(handle) || graph.edges.some(e => e.source === source && outputPort(kind, e) === handle)) return false
  const visited = new Set<string>()
  const reachesSource = (id: string): boolean => {
    if (id === source) return true
    if (visited.has(id)) return false
    visited.add(id)
    return graph.edges.filter(e => e.source === id).some(e => reachesSource(e.target))
  }
  return !reachesSource(target)
}
export function connectNodes(graph: VisualGraph, source: string, target: string, port?: EdgePort): VisualGraph {
  if (!canConnect(graph, source, target, port)) return graph
  const kind = kindOf(graph, source)!
  const resolved = port === 'next' && kind === 'condition' ? 'true' : port ?? outputPorts(kind)[0]
  const base = resolved === 'next' ? `${source}->${target}` : `${source}:${resolved}->${target}`
  let id = base
  for (let n = 2; graph.edges.some(e => e.id === id); n++) id = `${base}#${n}`
  return { ...graph, edges: [...graph.edges, resolved === 'next' ? { id, source, target } : { id, source, target, port: resolved }] }
}
export const removeEdge = (graph: VisualGraph, id: string): VisualGraph => ({ ...graph, edges: graph.edges.filter(edge => edge.id !== id) })
export const serializeGraph = (graph: VisualGraph) => JSON.stringify(graph, null, 2)

export function createGraphHistory(initial: VisualGraph) {
  const [graph, setGraph] = createSignal(initial)
  const [past, setPast] = createSignal<VisualGraph[]>([])
  const [future, setFuture] = createSignal<VisualGraph[]>([])
  return {
    graph,
    reset(next: VisualGraph) { setPast([]); setFuture([]); setGraph(next) },
    canUndo: () => past().length > 0,
    canRedo: () => future().length > 0,
    commit(next: VisualGraph) {
      if (serializeGraph(next) === serializeGraph(graph())) return
      setPast([...past().slice(-99), graph()]); setFuture([]); setGraph(next)
    },
    undo() {
      if (!past().length) return
      setFuture([graph(), ...future()]); setGraph(past().at(-1)!); setPast(past().slice(0, -1))
    },
    redo() {
      if (!future().length) return
      setPast([...past(), graph()]); setGraph(future()[0]); setFuture(future().slice(1))
    },
  }
}

// Shared v4 contract types for the v4 editor integration.
export type { GraphV4, V4Node, V4Edge, V4Operand, DataType } from './graphV4'
