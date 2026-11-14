import {
  analyzeGraph,
  closesDataCycle,
  inputSubject,
  comparisonDiagnostics,
  connectData,
  connectable,
  dataInputs,
  dataOutputs,
  incompatibleInput,
  isAssignable,
  isPure,
  nodeCatalog,
  serviceTargetDiagnostics,
  withGraphTypes,
  words,
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
  /** The catalog with the graph's Custom C# node types (`graphTypes`); every answer of the index reads it. */
  readonly catalog: VisualCatalogSnapshot
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
   * Memoised per output -> input pair, including verdicts that fell back to connectData.
   */
  verdict(source: string, sourcePort: string, target: string, targetPort: string): ConnectionVerdict
}

// connectData rejects a new diagnostic, any diagnostic on the target input or about the source output (no inputId),
// and a wire that lies on a data cycle. A cycle or scope error the graph already has rejects nothing on its own.
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const pairKey = (a: string | null, b: string) => JSON.stringify([a, b])

const cache = new WeakMap<Graph, WeakMap<VisualCatalogSnapshot, ConnectionIndex>>()
const merged = new WeakMap<Graph, WeakMap<VisualCatalogSnapshot, VisualCatalogSnapshot>>()

/**
 * The catalog with the types of the graph's Custom C# nodes (`withGraphTypes`), merged once per immutable graph
 * snapshot and catalog object. The result maps to itself, so passing it on (to cards, wires, `connectionIndex`)
 * neither merges nor analyses the graph again.
 */
export function graphTypes(graph: Graph, catalog: VisualCatalogSnapshot = nodeCatalog): VisualCatalogSnapshot {
  let byCatalog = merged.get(graph)
  if (!byCatalog) merged.set(graph, (byCatalog = new WeakMap()))
  let types = byCatalog.get(catalog)
  if (!types) {
    byCatalog.set(catalog, (types = withGraphTypes(catalog, graph)))
    byCatalog.set(types, types)
  }
  return types
}

/**
 * The index of one immutable graph snapshot under one catalog object; a new graph or catalog object builds a new
 * index. A catalog and its `graphTypes` share one index.
 */
export function connectionIndex(graph: Graph, catalog: VisualCatalogSnapshot = nodeCatalog): ConnectionIndex {
  // The types of the graph's Custom C# nodes, as the validator reads them.
  catalog = graphTypes(graph, catalog)
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
  // Diagnostics that reject a candidate on their own: those naming the node input or port it touches.
  const byInput = new Map<string, number[]>(), byPort = new Map<string, number[]>()
  const add = (map: Map<string, number[]>, key: string, i: number) => map.set(key, [...(map.get(key) ?? []), i])
  diagnostics.forEach((d, i) => {
    if (d.inputId) add(byInput, pairKey(d.nodeId, d.inputId), i)
    if (d.portId) add(byPort, pairKey(d.nodeId, d.portId), i)
  })
  const hasDataCycle = origins.some(o => o.kind === 'dataCycle')
  // The scope problems the graph has, by step input and message.
  const oldScope = new Set(diagnostics.filter(d => d.code === 'scope.data').map(d => JSON.stringify([d.nodeId, d.inputId, d.message])))
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
  const verdicts = new Map<string, ConnectionVerdict>()
  const index: ConnectionIndex = {
    catalog,
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
      const key = JSON.stringify([sourceId, sourcePort, targetId, targetPort])
      let verdict = verdicts.get(key)
      if (!verdict) verdicts.set(key, (verdict = decide(sourceId, sourcePort, targetId, targetPort)))
      return verdict
    },
  }
  function decide(sourceId: string, sourcePort: string, targetId: string, targetPort: string): ConnectionVerdict {
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
    // connectData reads `kind` of every edge and throws on a null entry; reject such a graph without throwing from a hover.
    if (graph.edges.some(e => e === null || e === undefined)) return { message: 'Every edge must be an object', fallback: false }
    const target = graph.nodes.find(n => n?.id === targetId), source = graph.nodes.find(n => n?.id === sourceId)
    if (!target) return { message: 'Target node missing', fallback: false }
    const removed = new Set<number>()
    graph.edges.forEach((e, i) => { if (record(e) && e.kind === 'data' && e.target === targetId && e.targetPort === targetPort) removed.add(i) })
    const edgeCount = graph.edges.length - removed.size + 1
    const limited = !graph.nodes.length || graph.nodes.length > catalog.limits.nodes
    if (!analysis.complete) {
      // Both validations stop at the same limit diagnostic, so nothing is added; anything else needs the full check.
      if (!diagnostics.some(d => d.code === 'graph.limit')) return full()
      return limited || edgeCount > catalog.limits.edges ? { fallback: false } : full()
    }
    if (edgeCount > catalog.limits.edges) return { message: 'Graph limit: 1–256 nodes and at most 512 edges', fallback: false }
    if (!source || analysis.nodes.get(sourceId) !== source || analysis.nodes.get(targetId) !== target) return full()
    const spec = Object.hasOwn(catalog.nodes, target.kind) ? catalog.nodes[target.kind] : undefined
    if (!spec || !storageIsPrivate(target, targetPort, spec)) return full()
    const pureNode = (node: GraphNode | undefined) => node !== undefined && isPure(node, catalog)
    const sourcePure = pureNode(source), targetPure = pureNode(target)

    const volatile = (o: DiagnosticOrigin) =>
      (o.kind === 'edge' && removed.has(o.edge)) || o.kind === 'dataCycle' ||
      (o.kind === 'operand' && o.node === targetId && o.port === targetPort) ||
      (o.kind === 'comparison' && o.node === targetId) ||
      (o.kind === 'serviceTarget' && o.node === targetId && (targetPort === 'target' || targetPort.startsWith('field:')))
    const messages = new Set<string>()
    // Carried-over diagnostics reject when they match connectData's filters.
    for (const i of [...(byInput.get(pairKey(targetId, targetPort)) ?? []), ...(byPort.get(pairKey(targetId, targetPort)) ?? []), ...(byPort.get(pairKey(sourceId, sourcePort)) ?? []).filter(i => diagnostics[i].inputId === undefined)].sort((a, b) => a - b))
      if (!volatile(origins[i])) messages.add(diagnostics[i].message)
    // The new edge, as the validator types it.
    const output = dataOutputs(source, catalog)[sourcePort]
    const input = connectable(dataInputs(target, analysis.ownerOf(target), catalog)[targetPort])
    if (!output) messages.add('Unknown data output port')
    if (!input) messages.add(words.unknownInput(targetPort, target.kind))
    if (output && input) {
      if (!input.types.some(t => index.assignable(output.type, t))) messages.add(incompatibleInput(target, input, targetPort, output.type, catalog))
      if (analysis.scopes.has(sourceId) && analysis.scopes.has(targetId) && !analysis.visible(source, target, output)) messages.add(words.scopeData(inputSubject(target, input, targetPort, catalog)))
      // The step outputs the wire brings in: the source's own, or those a value node source uses.
      const produced = sourcePure ? analysis.pureSources(source) : [{ node: source, port: sourcePort, output }]
      const crossing = (reader: GraphNode, via: GraphNode) => produced.filter(p => analysis.scopes.has(p.node.id) && !analysis.visible(p.node, reader, p.output))
        .map(p => words.pureScope(words.stepName(via, catalog), words.stepName(p.node, catalog), words.portLabel(p.output, p.port), words.stepName(reader, catalog)))
      if (sourcePure && !targetPure && analysis.scopes.has(targetId)) for (const m of crossing(target, source)) messages.add(m)
      // Into a value node: every step reading it, directly or through other value nodes, now reads those outputs too.
      // A step's problem the graph already has is no new problem.
      if (targetPure) for (const { via, reader, port } of stepReaders(target))
        if (analysis.scopes.has(reader.id)) for (const m of crossing(reader, via)) if (!oldScope.has(JSON.stringify([reader.id, port, m]))) messages.add(m)
    }
    // The new wire lies on a cycle. Without one in the graph, a path from the target back to the source cannot use
    // the wire it replaces, so the memoised reach of the current graph answers.
    const rewired = () => [...analysis.data.filter(e => !(e.target === targetId && e.targetPort === targetPort)), { source: sourceId, target: targetId }]
    if (sourceId === targetId || (hasDataCycle ? closesDataCycle(rewired(), sourceId, targetId) : reach(targetId).has(sourceId))) messages.add(words.ownResult(words.stepName(target, catalog)))
    if (spec.comparison && input) {
      const resolved = { ...analysis.resolved.get(targetId), [targetPort]: output?.type }
      for (const d of comparisonDiagnostics(target, spec, resolved, catalog))
        if (d.inputId === targetPort || !old.has(JSON.stringify(d))) messages.add(d.message)
    }
    // The service checks that a wired target or field changes: a target the service does not take, and a field named
    // like a target key once the target is set. They name the target input, so only a wire into it, or a new one, rejects.
    if (spec.dynamicInputs === 'service' && (targetPort === 'target' || targetPort.startsWith('field:'))) {
      const connected = (port: string) => port === targetPort || analysis.data.some(e => e.target === targetId && e.targetPort === port)
      for (const d of serviceTargetDiagnostics(target, connected, catalog))
        if (d.inputId === targetPort || !old.has(JSON.stringify(d))) messages.add(d.message)
    }
    return messages.size ? { message: [...messages].join('; '), fallback: false } : { fallback: false }
  }
  /** The value wires from the value node, or a value node reading it, into a step: the value node on the wire, the step and its input. */
  function stepReaders(start: GraphNode) {
    const found: { via: GraphNode; reader: GraphNode; port: string }[] = [], seen = new Set([start.id])
    for (const stack = [start]; stack.length; ) {
      const via = stack.pop()!
      for (const e of analysis.data) if (e.source === via.id) {
        const reader = analysis.nodes.get(e.target)!
        if (!isPure(reader, catalog)) found.push({ via, reader, port: e.targetPort })
        else if (!seen.has(reader.id)) { seen.add(reader.id); stack.push(reader) }
      }
    }
    return found
  }
  return index
}

type NodeSpec = VisualCatalogSnapshot['nodes'][string]
/**
 * Config keys of this node kind that something other than the input itself reads: the catalog config, and what
 * contract.ts derives from config for the kind (ports, scopes, trigger rules, a Custom C# node's signature).
 */
function sharedConfigKeys(kind: string, spec: NodeSpec): Set<string> {
  const keys = new Set(Object.keys(spec.config))
  if (spec.dynamicInputs === 'fields' || spec.dynamicInputs === 'service') keys.add('fields')
  if (spec.dynamicInputs === 'service') for (const key of ['service', 'returnResponse']) keys.add(key)
  if (spec.dynamicInputs === 'items') keys.add('items')
  if (spec.dynamicOutputs === 'loop') for (const key of ['carry', 'exports']) keys.add(key)
  if (spec.dynamicExecution === 'branches' || kind === 'parallel') keys.add('branches')
  if (spec.dynamicOutputs === 'trigger' || kind === 'trigger') for (const key of ['type', 'trigger', 'entity', 'time', 'timeZone', 'at']) keys.add(key)
  if (kind === 'foreach') keys.add('itemType')
  if (kind === 'entity_field' || kind === 'wait_until') for (const key of ['field', 'attribute']) keys.add(key)
  if (kind === 'get_field') keys.add('pointer')
  if (spec.comparison) for (const key of [spec.comparison.typeConfig, spec.comparison.operatorConfig]) if (key) keys.add(key)
  for (const output of Object.values(spec.outputs)) {
    if (output.type.startsWith('$')) keys.add(output.type.slice(1))
    if (output.when) keys.add(output.when)
    if (output.refineFrom) keys.add(output.refineFrom.split('.')[0])
  }
  for (const input of Object.values(spec.inputs)) for (const type of input.types) if (type.startsWith('$') && type !== '$any') keys.add(type.slice(1))
  return keys
}
/** True when setInput writes the operand where nothing but this input reads it. */
function storageIsPrivate(node: GraphNode, port: string, spec: NodeSpec) {
  if (spec.argumentStorage === 'arguments') {
    const args = node.config.arguments
    if (record(args)) return true
    // setInput replaces a malformed value with { [port]: operand }; that is private unless another input read something from it.
    return port !== 'arguments' && (args === undefined || args === null ||
      Object.keys(spec.inputs).every(other => other === port || (args as Record<string, unknown>)[other] === undefined))
  }
  if (port.startsWith('field:')) return record(node.config.fields)
  if (port.startsWith('item:')) return Array.isArray(node.config.items)
  if (node.kind === 'loop_result') return node.config.values === undefined || record(node.config.values)
  return !sharedConfigKeys(node.kind, spec).has(port)
}
