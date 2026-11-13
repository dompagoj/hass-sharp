import {
  analyzeGraph,
  comparisonDiagnostics,
  connectData,
  dataInputs,
  dataOutputs,
  isAssignable,
  nodeCatalog,
  serviceSchema,
  type DiagnosticOrigin,
  type Graph,
  type GraphAnalysis,
  type GraphNode,
  type VisualCatalogSnapshot,
} from './contract'

/** The outcome connectData would reach; `fallback` is true when the index could not decide and ran connectData itself. */
export interface ConnectionVerdict {
  message?: string
  fallback: boolean
}
export interface ConnectionIndex {
  readonly analysis: GraphAnalysis
  /** Memoised C1 assignability for this catalog. */
  assignable(source: string, target: string): boolean
  /** The validator's resolved type of a node input; undefined when unbound or unknown. */
  resolvedType(nodeId: string, port: string): string | undefined
  /** Per-index memo for derived values keyed by the caller. */
  remember<T>(key: string, compute: () => T): T
  /**
   * Whether connectData(graph, source, sourcePort, target, targetPort) would succeed, without cloning the
   * graph or validating it again. Callers check that both ports exist and their types match first.
   */
  verdict(source: string, sourcePort: string, target: string, targetPort: string): ConnectionVerdict
}

// connectData rejects a new diagnostic, any diagnostic on the target input or source output, and any cycle or scope error.
const blocking = /cycle|scope/
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const pairKey = (a: string | null, b: string) => JSON.stringify([a, b])
// Inputs stored next to configuration a validator rule or output type also reads; wiring them changes more than one input.
const sharedConfig = ['output', 'carry', 'exports', 'branches', 'itemType', 'values', 'fields', 'items', 'arguments']

const cache = new WeakMap<Graph, WeakMap<VisualCatalogSnapshot, ConnectionIndex>>()

/** The index of one immutable graph snapshot under one catalog object; a new graph or catalog object builds a new index. */
export function connectionIndex(graph: Graph, catalog: VisualCatalogSnapshot = nodeCatalog): ConnectionIndex {
  let byCatalog = cache.get(graph)
  if (!byCatalog) cache.set(graph, (byCatalog = new WeakMap()))
  let index = byCatalog.get(catalog)
  if (!index) byCatalog.set(catalog, (index = buildIndex(graph, catalog)))
  return index
}

function buildIndex(graph: Graph, catalog: VisualCatalogSnapshot): ConnectionIndex {
  const analysis = analyzeGraph(graph, catalog)
  const { diagnostics, origins } = analysis
  const old = new Set(diagnostics.map(d => JSON.stringify(d)))
  // Diagnostics that can reject a candidate on their own: by message, or by the node input/port they name.
  const blockingAll = diagnostics.flatMap((d, i) => (blocking.test(d.message) ? [i] : []))
  const byInput = new Map<string, number[]>(), byPort = new Map<string, number[]>()
  const add = (map: Map<string, number[]>, key: string, i: number) => map.set(key, [...(map.get(key) ?? []), i])
  diagnostics.forEach((d, i) => {
    if (d.inputId) add(byInput, pairKey(d.nodeId, d.inputId), i)
    if (d.portId) add(byPort, pairKey(d.nodeId, d.portId), i)
  })
  const hasDataCycle = origins.some(o => o.kind === 'dataCycle')
  const adjacency = new Map<string, string[]>()
  for (const edge of analysis.data) adjacency.set(edge.source, [...(adjacency.get(edge.source) ?? []), edge.target])
  const reachable = new Map<string, Set<string>>()
  const reach = (start: string) => {
    let seen = reachable.get(start)
    if (seen) return seen
    seen = new Set([start])
    for (const stack = [start]; stack.length; ) for (const next of adjacency.get(stack.pop()!) ?? []) if (!seen.has(next)) { seen.add(next); stack.push(next) }
    reachable.set(start, seen)
    return seen
  }
  const assignability = new Map<string, boolean>()
  const memo = new Map<string, unknown>()
  const index: ConnectionIndex = {
    analysis,
    assignable(source, target) {
      const key = JSON.stringify([source, target])
      let value = assignability.get(key)
      if (value === undefined) assignability.set(key, (value = isAssignable(source, target, catalog)))
      return value
    },
    resolvedType: (nodeId, port) => analysis.resolved.get(nodeId)?.[port],
    remember<T>(key: string, compute: () => T): T {
      if (!memo.has(key)) memo.set(key, compute())
      return memo.get(key) as T
    },
    verdict(sourceId, sourcePort, targetId, targetPort) {
      const full = (): ConnectionVerdict => {
        try {
          connectData(graph, sourceId, sourcePort, targetId, targetPort, catalog)
          return { fallback: true }
        } catch (error) {
          return { message: error instanceof Error ? error.message : String(error), fallback: true }
        }
      }
      if (graph.version !== catalog.graphVersion) return { message: `Only graph version ${catalog.graphVersion} is supported`, fallback: false }
      if (!Array.isArray(graph.nodes) || !Array.isArray(graph.edges)) return full()
      const target = graph.nodes.find(n => n.id === targetId), source = graph.nodes.find(n => n.id === sourceId)
      if (!target) return { message: 'Target node missing', fallback: false }
      const removed = new Set<number>()
      graph.edges.forEach((e, i) => { if (e.kind === 'data' && e.target === targetId && e.targetPort === targetPort) removed.add(i) })
      const edgeCount = graph.edges.length - removed.size + 1
      const limited = !graph.nodes.length || graph.nodes.length > catalog.limits.nodes
      if (!analysis.complete) {
        // Both validations stop at the same limit diagnostic, so nothing is added; anything else needs the full check.
        if (!diagnostics.some(d => d.message.startsWith('Graph limit'))) return full()
        return limited || edgeCount > catalog.limits.edges ? { fallback: false } : full()
      }
      if (edgeCount > catalog.limits.edges) return { message: 'Graph limit: 1–256 nodes and at most 512 edges', fallback: false }
      if (!source || analysis.nodes.get(sourceId) !== source || analysis.nodes.get(targetId) !== target) return full()
      const spec = Object.hasOwn(catalog.nodes, target.kind) ? catalog.nodes[target.kind] : undefined
      if (!spec || !storageIsPrivate(target, targetPort, spec)) return full()

      const volatile = (o: DiagnosticOrigin) =>
        (o.kind === 'edge' && removed.has(o.edge)) || o.kind === 'dataCycle' ||
        (o.kind === 'operand' && o.node === targetId && o.port === targetPort) ||
        (o.kind === 'comparison' && o.node === targetId) ||
        (o.kind === 'serviceTarget' && o.node === targetId && targetPort === 'target')
      const messages = new Set<string>()
      // Carried-over diagnostics reject when they match connectData's filters.
      for (const i of [...blockingAll, ...(byInput.get(pairKey(targetId, targetPort)) ?? []), ...(byPort.get(pairKey(targetId, targetPort)) ?? []), ...(byPort.get(pairKey(sourceId, sourcePort)) ?? [])].sort((a, b) => a - b))
        if (!volatile(origins[i])) messages.add(diagnostics[i].message)
      // The new edge, as the validator types it.
      const output = dataOutputs(source, catalog)[sourcePort]
      const input = dataInputs(target, analysis.ownerOf(target), catalog)[targetPort]
      if (!output) messages.add('Unknown data output port')
      if (!input) messages.add('Unknown data input port')
      if (output && input) {
        if (!input.types.some(t => index.assignable(output.type, t))) messages.add(`Incompatible input type ${output.type}; expected ${input.types.join('/')}`)
        if (!analysis.visible(source, target, output)) messages.add('Data cannot cross this structured scope')
      }
      if (sourceId === targetId || (hasDataCycle ? dataCycleAfter(analysis, sourceId, targetId, targetPort) : reach(targetId).has(sourceId))) messages.add('Data cycle is forbidden')
      if (spec.comparison && input) {
        const resolved = { ...analysis.resolved.get(targetId), [targetPort]: output?.type }
        for (const d of comparisonDiagnostics(target, spec, resolved, catalog))
          if (d.inputId === targetPort || !old.has(JSON.stringify(d))) messages.add(d.message)
      }
      // Mirrors validateService: a wired target on a known service without a target.
      const service = target.config.service
      if (targetPort === 'target' && spec.dynamicInputs === 'service' && catalog.services && typeof service === 'string' &&
        new RegExp(spec.config.service?.pattern ?? '').test(service)) {
        const schema = serviceSchema(target, catalog)
        if (schema && !record(schema.target)) messages.add(`Service ${service} does not take a target`)
      }
      return messages.size ? { message: [...messages].join('; '), fallback: false } : { fallback: false }
    },
  }
  return index
}

/** True when setInput writes the operand where nothing but this input reads it. */
function storageIsPrivate(node: GraphNode, port: string, spec: VisualCatalogSnapshot['nodes'][string]) {
  if (spec.argumentStorage === 'arguments') return record(node.config.arguments)
  if (port.startsWith('field:')) return record(node.config.fields)
  if (port.startsWith('item:')) return Array.isArray(node.config.items)
  if (node.kind === 'loop_result') return node.config.values === undefined || record(node.config.values)
  if (Object.hasOwn(spec.config, port) || sharedConfig.includes(port)) return false
  if (spec.comparison && (spec.comparison.typeConfig === port || spec.comparison.operatorConfig === port)) return false
  const outputs = Object.values(spec.outputs)
  if (outputs.some(o => o.type === `$${port}` || o.when === port || o.refineFrom?.split('.')[0] === port)) return false
  return !Object.values(spec.inputs).some(i => i.types.includes(`$${port}`))
}

/** Whether the data edges, with the target input rewired to the new source, contain any cycle. */
function dataCycleAfter(analysis: GraphAnalysis, sourceId: string, targetId: string, targetPort: string) {
  const adjacency = new Map<string, string[]>()
  const link = (from: string, to: string) => adjacency.set(from, [...(adjacency.get(from) ?? []), to])
  for (const edge of analysis.data) if (!(edge.target === targetId && edge.targetPort === targetPort)) link(edge.source, edge.target)
  link(sourceId, targetId)
  const active = new Set<string>(), done = new Set<string>()
  const visit = (id: string): boolean => {
    if (active.has(id)) return true
    if (done.has(id)) return false
    active.add(id)
    const cyclic = (adjacency.get(id) ?? []).some(visit)
    active.delete(id); done.add(id)
    return cyclic
  }
  return [...analysis.nodes.keys()].some(visit)
}
