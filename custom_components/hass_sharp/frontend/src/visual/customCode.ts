import { dataOutputs, isAssignable, isPure, nodeCatalog, resolveTypeSpec, validLiteral, wellFormedSignature, withGraphTypes, type CustomSignature, type ExecutionEdge, type VisualCatalogSnapshot } from './contract'
import { connectNodes, edgePort, nodeInputs, removeEdge, updateNode, type VisualGraph } from './graph'
import { nodeTitle } from './flow/flowModel'

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const list = (names: string[]) => [...new Set(names)].join(', ')
const and = (names: string[]) => (names.length < 2 ? names.join('') : `${names.slice(0, -1).join(', ')} and ${names.at(-1)}`)
const numeric = (type: string, catalog: VisualCatalogSnapshot) => { const kind = resolveTypeSpec(type, catalog)?.literalKind; return kind === 'integer' || kind === 'number' }

/** The code a new Custom C# node starts from: the catalog's example. */
export function exampleSource(kind = 'custom', catalog: VisualCatalogSnapshot = nodeCatalog): string {
  const fallback = Object.hasOwn(catalog.nodes, kind) ? catalog.nodes[kind].config.source?.default : undefined
  return typeof fallback === 'string' ? fallback : ''
}

/**
 * Writes a Custom C# node's new code and the signature the compiler gave it, as one new graph (one undo step). Values
 * and wires of pins that still exist, and whose types still take them, are kept; the rest are removed and named in the
 * notice. A node that becomes a value node loses its step order wires. The node's own label is left alone.
 */
export function applyCustomCode(graph: VisualGraph, nodeId: string, source: string, signature: CustomSignature, catalog: VisualCatalogSnapshot = nodeCatalog): { graph: VisualGraph; notice?: string } {
  const node = graph.nodes.find(n => n.id === nodeId)
  if (!node || !wellFormedSignature(signature)) return { graph }
  const before = withGraphTypes(catalog, graph)
  const oldInputs = nodeInputs(graph, node, before), oldOutputs = dataOutputs(node, before)
  const wasPure = isPure(node, catalog)
  let next = updateNode(graph, nodeId, { config: { ...node.config, source, signature: structuredClone(signature), arguments: {} } })
  const types = withGraphTypes(catalog, next)
  const updated = next.nodes.find(n => n.id === nodeId)!
  const inputs = nodeInputs(next, updated, types), outputs = dataOutputs(updated, types)
  // The validators' own rule, with the types as they will be after Apply (the code's own types included).
  const fits = (type: string | undefined, accepted: string[]) => type !== undefined && accepted.some(t => isAssignable(type, t, types))
  // A number typed in must also fit a narrower number pin it goes into (3.5 does not go into an int), as validating asks.
  const literalFits = (type: string | undefined, value: unknown, accepted: string[]) =>
    type !== undefined && accepted.some(t => isAssignable(type, t, types) && (t === type || typeof value !== 'number' || !numeric(t, types) || validLiteral(t, value, types)))

  // Values: an input that still exists keeps its value while the value's type still fits it.
  const kept: Record<string, unknown> = {}, cleared: string[] = []
  for (const [port, value] of Object.entries(record(node.config.arguments) ? node.config.arguments : {})) {
    const input = Object.hasOwn(inputs, port) ? inputs[port] : undefined
    const literal = record(value) && value.kind === 'literal'
    if (input && (!literal || literalFits(typeof value.type === 'string' ? value.type : undefined, value.value, input.types))) kept[port] = value
    else if (literal) cleared.push(oldInputs[port]?.label ?? port)
  }
  next = updateNode(next, nodeId, { config: { ...updated.config, arguments: kept } })

  // Wires into and out of the node.
  const removed: string[] = []
  const drop = (id: string, name: string) => { next = removeEdge(next, id, types); removed.push(name) }
  for (const edge of graph.edges) {
    if (edge.kind !== 'data') continue
    if (edge.target === nodeId) {
      const sourceNode = graph.nodes.find(n => n.id === edge.source)
      const input = Object.hasOwn(inputs, edge.targetPort) ? inputs[edge.targetPort] : undefined
      const type = sourceNode && (sourceNode.id === nodeId ? outputs : dataOutputs(sourceNode, types))[edge.sourcePort]?.type
      if (!input || !fits(type, input.types)) drop(edge.id, oldInputs[edge.targetPort]?.label ?? edge.targetPort)
    } else if (edge.source === nodeId) {
      const output = Object.hasOwn(outputs, edge.sourcePort) ? outputs[edge.sourcePort] : undefined
      const target = next.nodes.find(n => n.id === edge.target)
      const input = target ? nodeInputs(next, target, types)[edge.targetPort] : undefined
      if (!output || (input && !fits(output.type, input.types))) drop(edge.id, oldOutputs[edge.sourcePort]?.label ?? edge.sourcePort)
    }
  }

  // A value node has no place in the step order. Between one step before and one after, those two are joined (so
  // the run still reaches what followed); otherwise the steps that only it led to are named.
  const pure = signature.pure === true
  // A step is named as on its card; when another step reads the same, its id tells them apart (as Run now's list does).
  const plainTitle = (id: string) => nodeTitle(next.nodes.find(n => n.id === id), types, id)
  const title = (id: string) => {
    const name = plainTitle(id)
    return next.nodes.some(node => node.id !== id && plainTitle(node.id) === name) ? `${name} · ${id}` : name
  }
  let steps = 0, joined: { before: string; after: string } | undefined
  const unreached: string[] = []
  if (pure) {
    const order = next.edges.filter((edge): edge is ExecutionEdge => edge.kind !== 'data' && (edge.source === nodeId || edge.target === nodeId))
    const into = order.filter(edge => edge.target === nodeId), out = order.filter(edge => edge.source === nodeId)
    for (const edge of order) next = removeEdge(next, edge.id, types)
    steps = order.length
    if (into.length === 1 && out.length === 1 && edgePort(out[0]) === 'next') {
      const bridged = connectNodes(next, into[0].source, out[0].target, into[0].port, types)
      if (bridged !== next) { next = bridged; joined = { before: title(into[0].source), after: title(out[0].target) } }
    }
    if (!joined)
      for (const edge of out) if (!next.edges.some(e => e.kind !== 'data' && e.target === edge.target)) unreached.push(title(edge.target))
  }

  const notes: string[] = []
  const names = (items: string[]) => [...new Set(items)].length
  if (removed.length) notes.push(`Removed ${removed.length} ${removed.length === 1 ? 'connection that no longer fits' : 'connections that no longer fit'}: ${list(removed)}`)
  if (cleared.length) notes.push(`Cleared ${names(cleared) === 1 ? 'a value that no longer fits' : `${names(cleared)} values that no longer fit`}: ${list(cleared)}`)
  if (pure && !wasPure) {
    if (joined) notes.push(`${title(nodeId)} is now a value node; ${joined.after} now follows ${joined.before}`)
    else if (!steps) notes.push('It is now a value node, outside the step order')
    else {
      const lost = [...new Set(unreached)]
      notes.push(`It is now a value node: removed its ${steps} step order ${steps === 1 ? 'wire' : 'wires'}${lost.length ? `; ${and(lost)} ${lost.length === 1 ? 'is' : 'are'} no longer reached` : ''}`)
    }
  }
  if (!pure && wasPure) notes.push('It is now a step: connect it into the step order')
  return { graph: next, notice: notes.length ? `${notes.join('. ')}.` : undefined }
}
