import { dataOutputs, inputValue, type Graph, type GraphNode, type VisualCatalogSnapshot } from '../contract'
import { execLabel, nodeTitle, type FlowTrace, type TraceStatus } from '../flow/flowModel'
import { nodeInputs, outputPort, portLabel } from '../graph'
import { serviceFieldLabel } from '../nodeSummary'
import type { RunTrace, TraceEvent, TraceValue } from '../../types'

/**
 * A run trace (contract C10) read against the graph it ran: per-step status and run counts, the wires it travelled,
 * frames (loop iterations and parallel branches) in words, and the value of a port in a chosen frame.
 *
 * A frame is the context a step ran in: `""` at top level, else `/`-joined segments. Two frames *conflict* when they
 * are different iterations of the same loop; anything else (an enclosing frame, another parallel branch) can be seen
 * together. "The selected frame" is the one the person picked: steps show their run in a frame that does not conflict
 * with it, the latest such run when there are several.
 */

export type FrameSegment =
  | { kind: 'loop'; raw: string; node: string; iteration: number; part: 'body' | 'condition' }
  | { kind: 'branch'; raw: string; node: string; branch: string }
  | { kind: 'other'; raw: string }

const loopSegment = /^(.+)#(\d+):(body|condition)$/
const branchSegment = /^(.+?):branch:(.+)$/

export function parseFrame(frame: string): FrameSegment[] {
  if (!frame) return []
  return frame.split('/').map(raw => {
    const loop = loopSegment.exec(raw)
    if (loop) return { kind: 'loop', raw, node: loop[1], iteration: Number(loop[2]), part: loop[3] as 'body' | 'condition' }
    const branch = branchSegment.exec(raw)
    if (branch) return { kind: 'branch', raw, node: branch[1], branch: branch[2] }
    return { kind: 'other', raw }
  })
}

/** False only when the frames are different iterations of the same loop (at the first place they differ). */
export function compatibleFrames(a: string, b: string): boolean {
  if (a === b || !a || !b) return true
  const x = parseFrame(a), y = parseFrame(b)
  for (let i = 0; i < Math.min(x.length, y.length); i++) {
    const s = x[i], t = y[i]
    if (s.raw === t.raw) continue
    if (s.kind === 'loop' && t.kind === 'loop' && s.node === t.node) {
      if (s.iteration !== t.iteration) return false
      continue
    }
    return true
  }
  return true
}

/** `frame` is `ancestor` or inside it. */
export const withinFrame = (frame: string, ancestor: string) => !ancestor || frame === ancestor || frame.startsWith(`${ancestor}/`)

/**
 * The frame to show after picking a run of a step: the run's own frame, unless the current one is inside it (picking
 * a step outside a loop keeps the iteration that was being looked at).
 */
export const nextFrame = (current: string, picked: string) => (withinFrame(current, picked) ? current : picked)

const plural = (count: number, word: string) => `${count} ${word}${count === 1 ? '' : 's'}`

/** A recorded value in words: what a card's chip says and what the panel shows for anything that is not a tree. */
export function valueText(value: TraceValue): string {
  if (value.redacted) return 'Hidden'
  if (!('v' in value) || value.v === undefined) return 'Too large to record'
  const v = value.v
  if (v === null) return value.trunc ? 'Not recorded (too deep)' : 'No value'
  // A value the recorder could not read is written as its type name.
  if (value.trunc && typeof v === 'string' && v.startsWith('global::')) return 'Could not be recorded'
  if (Array.isArray(v)) return `List · ${plural(v.length, 'item')}`
  if (typeof v === 'object') return `Object · ${plural(Object.keys(v).length, 'field')}`
  if (typeof v === 'string') return v === '' ? 'Empty text' : v
  if (typeof v === 'boolean') return v ? 'Yes' : 'No'
  return String(v)
}
/** True when the panel shows the value as a tree. */
export const isTree = (value: TraceValue) => !value.redacted && value.v !== null && typeof value.v === 'object'
/** The value was cut (as opposed to not recorded at all, which {@link valueText} already says). */
export const isCut = (value: TraceValue) =>
  !!value.trunc && !value.redacted && 'v' in value && value.v !== null && !(typeof value.v === 'string' && value.v.startsWith('global::'))

/** What a card's chip receives: the words of {@link valueText}, with `trunc` only where part of a value is shown. */
export const chipValue = (value: TraceValue): TraceValue => ({ type: value.type, v: valueText(value), trunc: isCut(value) || undefined, src: value.src })

export interface TraceModel {
  trace: RunTrace
  /** Every recorded run of a step, in the order they started. */
  events: TraceEvent[]
  eventsOf(nodeId: string): TraceEvent[]
  event(seq: number | undefined): TraceEvent | undefined
  /** The step's latest run in a frame that does not conflict with `frame`. */
  eventIn(nodeId: string, frame: string): TraceEvent | undefined
  /** How many times the step ran (the summary counts runs whose event was not kept). */
  runs(nodeId: string): number
  reached(nodeId: string): boolean
  edges: Set<string>
  /** "Iteration 3 of 12 · Branch Lights"; `""` at top level. */
  frameLabel(frame: string): string
  /** The port's value in the frame: recorded on the step, else read by a step downstream (a loop's item). */
  value(nodeId: string, port: string, side: 'in' | 'out', frame: string): TraceValue | undefined
}

export function traceModel(trace: RunTrace, graph: Graph, catalog: VisualCatalogSnapshot): TraceModel {
  const events = [...trace.events].sort((a, b) => a.seq - b.seq)
  const bySeq = new Map(events.map(event => [event.seq, event]))
  const byNode = new Map<string, TraceEvent[]>()
  for (const event of events) byNode.set(event.node, [...(byNode.get(event.node) ?? []), event])
  const nodes = new Map(graph.nodes.map(node => [node.id, node]))
  const eventsOf = (id: string) => byNode.get(id) ?? []
  const summary = (id: string) => (Object.hasOwn(trace.summary ?? {}, id) ? trace.summary[id] : undefined)

  // Iterations seen per loop, keyed by the frame the loop ran in and its id.
  const iterations = new Map<string, Set<number>>()
  for (const event of events) {
    const segments = parseFrame(event.frame)
    segments.forEach((segment, i) => {
      if (segment.kind !== 'loop') return
      const key = `${segments.slice(0, i).map(s => s.raw).join('/')}|${segment.node}`
      iterations.set(key, (iterations.get(key) ?? new Set()).add(segment.iteration))
    })
  }
  /** A repeat loop's count or a for-each loop's item count, when its run recorded them whole. */
  const declaredCount = (loop: string, parent: string) => {
    const run = eventsOf(loop).filter(e => e.frame === parent).at(-1)
    const count = run?.inputs?.count, items = run?.inputs?.items
    if (count && !count.trunc && typeof count.v === 'number') return count.v
    if (items && !items.trunc && Array.isArray(items.v)) return items.v.length
  }
  const segmentLabel = (segment: FrameSegment, parent: string) => {
    if (segment.kind === 'loop') {
      const seen = iterations.get(`${parent}|${segment.node}`)
      const total = Math.max(seen ? Math.max(...seen) + 1 : 0, declaredCount(segment.node, parent) ?? 0, segment.iteration + 1)
      return `Iteration ${segment.iteration + 1} of ${total}${segment.part === 'condition' ? ' · Condition' : ''}`
    }
    if (segment.kind === 'branch') {
      const owner = nodes.get(segment.node)
      const name = owner ? execLabel(owner, `branch:${segment.branch}`, catalog) : segment.branch
      return name.startsWith('Branch ') ? name : `Branch ${name}`
    }
    return segment.raw
  }
  const frameLabel = (frame: string) => {
    const segments = parseFrame(frame)
    return segments.map((segment, i) => segmentLabel(segment, segments.slice(0, i).map(s => s.raw).join('/'))).join(' · ')
  }

  const eventIn = (id: string, frame: string) => eventsOf(id).filter(event => compatibleFrames(event.frame, frame)).at(-1)

  const edges = new Set<string>()
  for (const edge of graph.edges) {
    const source = nodes.get(edge.source)
    if (!source) continue
    if (edge.kind === 'data') {
      if (eventsOf(edge.target).some(e => Object.values(e.inputs ?? {}).some(v => v.src?.node === edge.source && v.src.port === edge.sourcePort)))
        edges.add(edge.id)
      continue
    }
    const port = outputPort(source.kind, edge)
    const entered = (match: (segment: FrameSegment) => boolean) =>
      eventsOf(edge.target).some(e => { const last = parseFrame(e.frame).at(-1); return !!last && match(last) })
    const travelled =
      eventsOf(source.id).some(e => e.port === port) ||
      ((port === 'body' || port === 'condition') && entered(s => s.kind === 'loop' && s.node === source.id && s.part === port)) ||
      (port.startsWith('branch:') && entered(s => s.kind === 'branch' && s.node === source.id && s.branch === port.slice(7)))
    if (travelled) edges.add(edge.id)
  }

  const value = (id: string, port: string, side: 'in' | 'out', frame: string): TraceValue | undefined => {
    const own = eventIn(id, frame)
    const recorded = side === 'in' ? own?.inputs?.[port] : own?.outputs?.[port]
    if (recorded || side === 'in') return recorded
    // Not recorded as an output (a loop's item and index, a trigger's outputs): what a step downstream read from it.
    const readers = events.filter(e => compatibleFrames(e.frame, frame) && (!own || withinFrame(e.frame, own.frame)))
    for (const reader of readers.reverse()) {
      const read = Object.values(reader.inputs ?? {}).find(v => v.src?.node === id && v.src.port === port)
      if (read) return { type: read.type, v: read.v, trunc: read.trunc, redacted: read.redacted }
    }
  }

  return {
    trace,
    events,
    eventsOf,
    event: seq => (seq === undefined ? undefined : bySeq.get(seq)),
    eventIn,
    runs: id => Math.max(summary(id)?.count ?? 0, eventsOf(id).length),
    reached: id => eventsOf(id).length > 0 || (summary(id)?.count ?? 0) > 0,
    edges,
    frameLabel,
    value,
  }
}

/** The canvas's `trace` prop for the selected frame: statuses and durations of the runs shown, chips in words. */
export function flowTrace(model: TraceModel, graph: Graph, frame: string, focusNodeId?: string): FlowTrace {
  const nodes: FlowTrace['nodes'] = {}
  for (const node of graph.nodes) {
    if (!model.reached(node.id)) continue
    const shown = model.eventIn(node.id, frame) ?? model.eventsOf(node.id).at(-1)
    const status: TraceStatus = shown?.status ?? model.trace.summary[node.id]?.last ?? 'ok'
    nodes[node.id] = { status, runs: model.runs(node.id), ...(shown?.ms !== undefined ? { ms: shown.ms } : {}) }
  }
  return {
    nodes,
    edges: model.edges,
    focusNodeId,
    value: (nodeId, port, side) => {
      const value = model.value(nodeId, port, side, frame)
      return value ? chipValue(value) : undefined
    },
  }
}

// ---- Words for the value panel.

const own = <T,>(record: Record<string, T> | undefined, key: string) => (record && Object.hasOwn(record, key) ? record[key] : undefined)
const text = (value: unknown) => (typeof value === 'string' ? value : '')

export function inputLabel(graph: Graph, node: GraphNode, port: string, catalog: VisualCatalogSnapshot): string {
  if (node.kind === 'service' && port.startsWith('field:')) return serviceFieldLabel(text(node.config.service), port.slice(6), catalog.services)
  return own(nodeInputs(graph, node, catalog), port)?.label ?? portLabel(port)
}
export const outputLabel = (node: GraphNode | undefined, port: string, catalog: VisualCatalogSnapshot) =>
  (node ? own(dataOutputs(node, catalog), port)?.label : undefined) ?? portLabel(port)

/** Where an input's value came from: "From Motion · Entity", "Variable level" or "Value". */
export function inputSource(graph: Graph, node: GraphNode, port: string, value: TraceValue, catalog: VisualCatalogSnapshot): string {
  if (value.src) {
    const source = graph.nodes.find(n => n.id === value.src!.node)
    return `From ${nodeTitle(source, catalog, 'a removed step')} · ${outputLabel(source, value.src.port, catalog)}`
  }
  const operand = inputValue(node, port, catalog) as { kind?: unknown; name?: unknown } | undefined
  if (operand?.kind === 'variable' && typeof operand.name === 'string') return `Variable ${operand.name}`
  return 'Value'
}

/** The order the panel lists a step's inputs in: the step's own order, then anything else that was recorded. */
export function inputOrder(graph: Graph, node: GraphNode, recorded: Record<string, unknown>, catalog: VisualCatalogSnapshot): string[] {
  const declared = Object.keys(nodeInputs(graph, node, catalog))
  return [...declared.filter(port => Object.hasOwn(recorded, port)), ...Object.keys(recorded).filter(port => !declared.includes(port))]
}
