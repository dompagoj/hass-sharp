import { dataInputs, dataOutputs, words, type DataEdge, type GraphAnalysis, type VisualCatalogSnapshot } from './contract'
import { nodeTitle } from './flow/flowModel'
import { portLabel, type VisualGraph } from './graph'

/**
 * A value wire whose pin the step does not have (any more): into a Constant, into a parameter a node library method
 * lost, from an output that went, into a service field taken out of the config. The canvas cannot draw such a wire
 * (the pin is missing, so it has no end), so the problem about it carries the way to remove it.
 */
const dangling = new Set(['port.unknownInput', 'port.unknownOutput'])
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)

/**
 * The wire each validator diagnostic about a missing pin is about, aligned with `analysis.diagnostics`: its id and its
 * place in `graph.edges` (two wires can share an id, itself an error; the place tells them apart).
 */
export function danglingWireIds(graph: VisualGraph, analysis: Pick<GraphAnalysis, 'diagnostics' | 'origins'>): ({ id: string; index: number } | undefined)[] {
  return analysis.diagnostics.map((diagnostic, i) => {
    const origin = analysis.origins[i]
    if (!diagnostic.code || !dangling.has(diagnostic.code) || origin?.kind !== 'edge') return undefined
    const edge: unknown = graph.edges[origin.edge]
    return record(edge) && edge.kind === 'data' && typeof edge.id === 'string' ? { id: edge.id, index: origin.edge } : undefined
  })
}

/** Where the wire a problem names is in `graph.edges`: at its recorded place when that still holds it, else by its id. */
function wireIndex(graph: VisualGraph, diagnostic: { edgeId?: string; edgeIndex?: number }): number {
  if (!diagnostic.edgeId) return -1
  const at = diagnostic.edgeIndex
  if (at !== undefined && record(graph.edges[at]) && graph.edges[at].id === diagnostic.edgeId) return at
  return graph.edges.findIndex(e => record(e) && e.id === diagnostic.edgeId)
}

/** The wire a problem offers to remove: a value wire of the graph, named by the problem, and its place. */
export function danglingWire(graph: VisualGraph, diagnostic: { edgeId?: string; edgeIndex?: number }): { edge: DataEdge; index: number } | undefined {
  const index = wireIndex(graph, diagnostic)
  const edge: unknown = graph.edges[index]
  return index >= 0 && record(edge) && edge.kind === 'data' ? { edge: edge as unknown as DataEdge, index } : undefined
}

/** The graph without the one wire at that place; nothing else changes (another wire with its id, a typed value, stay). */
export const withoutWire = (graph: VisualGraph, index: number): VisualGraph => ({ ...graph, edges: graph.edges.filter((_, i) => i !== index) })

/**
 * "Remove connection from Trigger · Entity": which wire, by where it comes from (as a connected input names it). When
 * another wire leaves the same output, where it goes is added: "… into Value", or "… into Log · Value" for another step.
 */
export function removeWireLabel(graph: VisualGraph, edge: DataEdge, catalog: VisualCatalogSnapshot): string {
  const source = graph.nodes.find(n => n.id === edge.source)
  const output = source ? dataOutputs(source, catalog)[edge.sourcePort] : undefined
  const from = `Remove connection from ${nodeTitle(source, catalog, 'a removed step')} · ${output?.label ?? portLabel(edge.sourcePort)}`
  const siblings = graph.edges.filter(e => record(e) && e !== edge && e.kind === 'data' && e.source === edge.source && e.sourcePort === edge.sourcePort) as DataEdge[]
  if (!siblings.length) return from
  const target = graph.nodes.find(n => n.id === edge.target)
  // The pin is missing (that is the problem), so its name is the step's input of that id if it has one, else the id in words.
  const pin = words.portLabel(target ? dataInputs(target, undefined, catalog)[edge.targetPort] : undefined, edge.targetPort)
  if (siblings.every(e => e.target === edge.target)) return `${from} into ${pin}`
  return `${from} into ${nodeTitle(target, catalog, 'a removed step')} · ${pin}`
}
