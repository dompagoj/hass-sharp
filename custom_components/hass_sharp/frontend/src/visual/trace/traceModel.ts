import type { Graph, VisualCatalogSnapshot } from '../contract'
import { execLabel, type FlowTrace, type TracedValue, type TraceStatus } from '../flow/flowModel'
import { outputPort } from '../graph'
import type { RunTrace, TraceEvent, TraceValue } from '../../types'

/**
 * A run trace (contract C10) read against the graph it ran: per-step status and run counts, the wires it travelled,
 * frames (loop iterations and parallel branches) in words, and the value of a port in a chosen frame.
 *
 * A frame is the context a step ran in: `""` at top level, else `/`-joined segments. The canvas shows a *picture*: the
 * whole run (frame `""`), or one chosen iteration or branch. In a chosen picture, a run inside that loop or parallel
 * counts only when it ran in the chosen iteration or branch ({@link framesAgree}); everything outside is unchanged.
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

/**
 * Whether a run in `frame` belongs to the picture of the `chosen` frame: false only where both pass through the same
 * loop in different iterations, or the same parallel in different branches. A loop's condition and body of one
 * iteration belong together; a run outside the chosen loop or parallel always belongs.
 */
export function framesAgree(frame: string, chosen: string): boolean {
  if (!frame || !chosen || frame === chosen) return true
  const x = parseFrame(frame), y = parseFrame(chosen)
  for (let i = 0; i < Math.min(x.length, y.length); i++) {
    const s = x[i], t = y[i]
    if (s.raw === t.raw) continue
    if (s.kind === 'loop' && t.kind === 'loop' && s.node === t.node) return s.iteration === t.iteration
    if (s.kind === 'branch' && t.kind === 'branch' && s.node === t.node) return false
    return true
  }
  return true
}

/**
 * Whether a step belongs to the loops and parallels of the chosen frame, so that the chosen iteration or branch
 * applies to it: it owns one of them, or one of its runs (`frames`) passed through one of them.
 */
export function inChosenFrame(nodeId: string, frames: string[], chosen: string): boolean {
  if (!chosen) return false
  const segments = parseFrame(chosen)
  if (segments.some(s => s.kind !== 'other' && s.node === nodeId)) return true
  return frames.some(frame => {
    const first = parseFrame(frame)[0], top = segments[0]
    return !!first && (first.raw === top.raw || (first.kind !== 'other' && top.kind === first.kind && top.node === first.node))
  })
}

/** `frame` is `ancestor` or inside it. */
export const withinFrame = (frame: string, ancestor: string) => !ancestor || frame === ancestor || frame.startsWith(`${ancestor}/`)

const plural = (count: number, word: string) => `${count} ${word}${count === 1 ? '' : 's'}`
/** A value the recorder could not read is written as its C# type name. */
export const opaque = (v: unknown) => typeof v === 'string' && v.startsWith('global::')

/** The label of a recorded choice (an option of a select or a state), when the input offers options; see `optionText`. */
export type OptionText = (value: string | number) => string | undefined
/**
 * A recorded value in words: what a card's chip says and what the panel shows for anything that is not a tree. A
 * choice reads as its option's label when `option` knows one.
 */
export function valueText(value: TraceValue, option?: OptionText, side: 'in' | 'out' = 'in'): string {
  if (value.redacted) return 'Hidden'
  if (!('v' in value) || value.v === undefined) return 'Too large to record'
  const v = value.v
  // The recorder caps depth only below the top level: a whole value cut to nothing could not be read.
  // An empty value is recorded as null, never shown as the text "null": an input was not set, an output had no value.
  if (v === null) return value.trunc ? 'Could not be recorded' : side === 'out' ? 'No value' : 'Not set'
  if (value.trunc && opaque(v)) return 'Could not be recorded'
  if (Array.isArray(v)) return `List · ${plural(v.length, 'item')}`
  if (typeof v === 'object') return `Object · ${plural(Object.keys(v).length, 'field')}`
  if ((typeof v === 'string' && v !== '') || typeof v === 'number') { const label = option?.(v); if (label !== undefined) return label }
  if (typeof v === 'string') return v === '' ? 'Empty text' : v
  if (typeof v === 'boolean') return v ? 'True' : 'False'
  return String(v)
}
/** True when the panel shows the value as a tree. */
export const isTree = (value: TraceValue) => !value.redacted && value.v !== null && typeof value.v === 'object'
/** The value was cut (as opposed to not recorded at all, which {@link valueText} already says). */
export const isCut = (value: TraceValue) => !!value.trunc && !value.redacted && 'v' in value && value.v !== null && !opaque(value.v)
/** {@link valueText} says the value is missing rather than showing it. */
export const isPlaceholder = (value: TraceValue) => !!value.redacted || value.v === undefined || value.v === null || (!!value.trunc && opaque(value.v))

/**
 * What a card's chip receives: the words of {@link valueText}, with `trunc` only where part of a value is shown, and
 * `muted` where the words stand for a missing value (as the panel shows them).
 */
export const chipValue = (value: TraceValue, option?: OptionText, side: 'in' | 'out' = 'in'): TracedValue =>
  ({ type: value.type, v: valueText(value, option, side), trunc: isCut(value) || undefined, src: value.src, ...(isPlaceholder(value) ? { muted: true } : {}) })

/** The run as the canvas shows it for one frame. */
interface Picture {
  /** Each step's runs in the picture, in the order they started. */
  runs: Map<string, TraceEvent[]>
  edges: Set<string>
}

export interface TraceModel {
  trace: RunTrace
  /** Every recorded run of a step, in the order they started. */
  events: TraceEvent[]
  eventsOf(nodeId: string): TraceEvent[]
  event(seq: number | undefined): TraceEvent | undefined
  /** The step's latest run in the picture of `frame` (`""`: the whole run). */
  eventIn(nodeId: string, frame: string): TraceEvent | undefined
  /** How many times the step ran (the summary counts runs whose event was not kept). */
  runs(nodeId: string): number
  reached(nodeId: string): boolean
  /** The step ran in the picture of `frame`; one counted only by the summary ran somewhere, so it counts. */
  reachedIn(nodeId: string, frame: string): boolean
  /** The wires the whole run travelled. */
  edges: Set<string>
  /** The wires travelled in the picture of `frame`. */
  edgesIn(frame: string): Set<string>
  /** "Iteration 3 of 12 · Branch Lights"; `""` at top level. */
  frameLabel(frame: string): string
  /** The port's value in the frame: recorded on the step, else read by a step downstream (a loop's item). */
  value(nodeId: string, port: string, side: 'in' | 'out', frame: string): TraceValue | undefined
}

export function traceModel(trace: RunTrace, graph: Graph, catalog: VisualCatalogSnapshot): TraceModel {
  const events = [...trace.events].sort((a, b) => a.seq - b.seq)
  const bySeq = new Map(events.map(event => [event.seq, event]))
  const byNode = new Map<string, TraceEvent[]>()
  // The runs that read each step's outputs (a value a step does not record itself is found there).
  const readersOf = new Map<string, TraceEvent[]>()
  for (const event of events) {
    const runs = byNode.get(event.node)
    if (runs) runs.push(event)
    else byNode.set(event.node, [event])
    for (const source of new Set(Object.values(event.inputs ?? {}).flatMap(v => (v.src ? [v.src.node] : [])))) {
      const readers = readersOf.get(source)
      if (readers) readers.push(event)
      else readersOf.set(source, [event])
    }
  }
  const nodes = new Map(graph.nodes.map(node => [node.id, node]))
  const settled = trace.complete && !trace.truncated && !trace.dropped
  const eventsOf = (id: string) => byNode.get(id) ?? []
  const summary = (id: string) => (Object.hasOwn(trace.summary ?? {}, id) ? trace.summary[id] : undefined)

  // Iterations per loop, from its bodies (a while loop checks its condition once more than it runs its body),
  // keyed by the frame the loop ran in and its id.
  const iterations = new Map<string, Set<number>>()
  for (const event of events) {
    const segments = parseFrame(event.frame)
    segments.forEach((segment, i) => {
      if (segment.kind !== 'loop' || segment.part !== 'body') return
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
      const bodies = iterations.get(`${parent}|${segment.node}`)
      const body = segment.part === 'body' || !!bodies?.has(segment.iteration)
      // A condition checked with no body after it is the check that ended the loop, once the whole run is recorded:
      // while it runs, or with steps missing, a body may still follow (or have been dropped).
      if (!body && settled) return 'Final check'
      const total = Math.max(bodies ? Math.max(...bodies) + 1 : 0, declaredCount(segment.node, parent) ?? 0, segment.iteration + 1)
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

  const travelledEdges = (runsOf: (id: string) => TraceEvent[]) => {
    const edges = new Set<string>()
    for (const edge of graph.edges) {
      const source = nodes.get(edge.source)
      if (!source) continue
      if (edge.kind === 'data') {
        if (runsOf(edge.target).some(e => Object.values(e.inputs ?? {}).some(v => v.src?.node === edge.source && v.src.port === edge.sourcePort)))
          edges.add(edge.id)
        continue
      }
      const port = outputPort(source.kind, edge)
      const entered = (match: (segment: FrameSegment) => boolean) =>
        runsOf(edge.target).some(e => { const last = parseFrame(e.frame).at(-1); return !!last && match(last) })
      const travelled =
        runsOf(source.id).some(e => e.port === port) ||
        ((port === 'body' || port === 'condition') && entered(s => s.kind === 'loop' && s.node === source.id && s.part === port)) ||
        (port.startsWith('branch:') && entered(s => s.kind === 'branch' && s.node === source.id && s.branch === port.slice(7)))
      if (travelled) edges.add(edge.id)
    }
    return edges
  }

  const pictures = new Map<string, Picture>()
  const picture = (frame: string): Picture => {
    let shown = pictures.get(frame)
    if (!shown) {
      const runs = new Map<string, TraceEvent[]>()
      for (const [id, list] of byNode) runs.set(id, frame ? list.filter(e => framesAgree(e.frame, frame)) : list)
      shown = { runs, edges: travelledEdges(id => runs.get(id) ?? []) }
      pictures.set(frame, shown)
    }
    return shown
  }
  const eventIn = (id: string, frame: string) => picture(frame).runs.get(id)?.at(-1)
  const reached = (id: string) => eventsOf(id).length > 0 || (summary(id)?.count ?? 0) > 0

  const value = (id: string, port: string, side: 'in' | 'out', frame: string): TraceValue | undefined => {
    const own = eventIn(id, frame)
    const recorded = side === 'in' ? own?.inputs?.[port] : own?.outputs?.[port]
    if (recorded || side === 'in') return recorded
    // Not recorded as an output (a loop's item and index, a trigger's outputs): what a step downstream read from it.
    const readers = (readersOf.get(id) ?? []).filter(e => framesAgree(e.frame, frame) && (!own || withinFrame(e.frame, own.frame)))
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
    reached,
    reachedIn: (id, frame) => (eventsOf(id).length ? !!eventIn(id, frame) : reached(id)),
    edges: picture('').edges,
    edgesIn: frame => picture(frame).edges,
    frameLabel,
    value,
  }
}

/**
 * The canvas's `trace` prop for a picture (`frame` `""`: the whole run): statuses and durations of the runs shown, the
 * wires travelled, chips in words. A step that did not run in a chosen iteration or branch is left out (dimmed).
 */
export function flowTrace(model: TraceModel, graph: Graph, frame: string, focusNodeId?: string, option?: (nodeId: string, port: string) => OptionText | undefined): FlowTrace {
  const nodes: FlowTrace['nodes'] = {}
  for (const node of graph.nodes) {
    if (!model.reachedIn(node.id, frame)) continue
    const shown = model.eventIn(node.id, frame)
    const status: TraceStatus = shown?.status ?? model.trace.summary[node.id]?.last ?? 'ok'
    const all = model.eventsOf(node.id)
    const runs = frame && all.length ? all.filter(e => framesAgree(e.frame, frame)).length : model.runs(node.id)
    nodes[node.id] = { status, runs, ...(shown?.ms !== undefined ? { ms: shown.ms } : {}) }
  }
  return {
    nodes,
    edges: model.edgesIn(frame),
    focusNodeId,
    value: (nodeId, port, side) => {
      const value = model.value(nodeId, port, side, frame)
      return value ? chipValue(value, side === 'in' ? option?.(nodeId, port) : undefined, side) : undefined
    },
  }
}
