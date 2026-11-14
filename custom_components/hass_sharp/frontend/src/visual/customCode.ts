import { dataOutputs, isAssignable, isPure, nodeCatalog, wellFormedSignature, withGraphTypes, type CustomSignature, type VisualCatalogSnapshot } from './contract'
import { nodeInputs, removeEdge, updateNode, type VisualGraph } from './graph'

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const list = (names: string[]) => [...new Set(names)].join(', ')

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
  const fits = (type: string | undefined, accepted: string[]) => type !== undefined && accepted.some(t => isAssignable(type, t, types))

  // Values: an input that still exists keeps its value while the value's type still fits it.
  const kept: Record<string, unknown> = {}, cleared: string[] = []
  for (const [port, value] of Object.entries(record(node.config.arguments) ? node.config.arguments : {})) {
    const input = Object.hasOwn(inputs, port) ? inputs[port] : undefined
    const literal = record(value) && value.kind === 'literal'
    if (input && (!literal || fits(typeof value.type === 'string' ? value.type : undefined, input.types))) kept[port] = value
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

  // A value node has no place in the step order.
  const pure = signature.pure === true
  let steps = 0
  if (pure) for (const edge of graph.edges) if (edge.kind !== 'data' && (edge.source === nodeId || edge.target === nodeId)) { next = removeEdge(next, edge.id, types); steps++ }

  const notes: string[] = []
  const names = (items: string[]) => [...new Set(items)].length
  if (removed.length) notes.push(`Removed ${removed.length} ${removed.length === 1 ? 'connection that no longer fits' : 'connections that no longer fit'}: ${list(removed)}`)
  if (cleared.length) notes.push(`Cleared ${names(cleared) === 1 ? 'a value that no longer fits' : `${names(cleared)} values that no longer fit`}: ${list(cleared)}`)
  if (pure && !wasPure) notes.push(steps ? `It is now a value node: removed its ${steps} step order ${steps === 1 ? 'wire' : 'wires'}` : 'It is now a value node, outside the step order')
  if (!pure && wasPure) notes.push('It is now a step: connect it into the step order')
  return { graph: next, notice: notes.length ? `${notes.join('. ')}.` : undefined }
}
