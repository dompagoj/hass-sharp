import { dataOutputs, type DataEdge, type GraphAnalysis, type VisualCatalogSnapshot } from './contract'
import { nodeTitle } from './flow/flowModel'
import { portLabel, type VisualGraph } from './graph'

/**
 * A value wire whose pin the step does not have (any more): into a Constant, into a parameter a node library method
 * lost, from an output that went, into a service field taken out of the config. The canvas cannot draw such a wire
 * (the pin is missing, so it has no end), so the problem about it carries the way to remove it.
 */
const dangling = new Set(['port.unknownInput', 'port.unknownOutput'])
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)

/** The id of the wire each validator diagnostic about a missing pin is about, aligned with `analysis.diagnostics`. */
export function danglingWireIds(graph: VisualGraph, analysis: Pick<GraphAnalysis, 'diagnostics' | 'origins'>): (string | undefined)[] {
  return analysis.diagnostics.map((diagnostic, i) => {
    const origin = analysis.origins[i]
    if (!diagnostic.code || !dangling.has(diagnostic.code) || origin?.kind !== 'edge') return undefined
    const edge: unknown = graph.edges[origin.edge]
    return record(edge) && edge.kind === 'data' && typeof edge.id === 'string' ? edge.id : undefined
  })
}

/** The wire a problem offers to remove: a value wire of the graph, named by the problem. */
export function danglingWire(graph: VisualGraph, diagnostic: { edgeId?: string }): DataEdge | undefined {
  if (!diagnostic.edgeId) return undefined
  const edge: unknown = graph.edges.find(e => record(e) && e.id === diagnostic.edgeId)
  return record(edge) && edge.kind === 'data' ? (edge as unknown as DataEdge) : undefined
}

/** The graph without that one wire; nothing else changes (the value typed into the input, if any, stays). */
export const withoutWire = (graph: VisualGraph, id: string): VisualGraph => ({ ...graph, edges: graph.edges.filter(e => !(record(e) && e.id === id)) })

/** "Remove connection from Trigger · Entity": which wire, by where it comes from (as a connected input names it). */
export function removeWireLabel(graph: VisualGraph, edge: DataEdge, catalog: VisualCatalogSnapshot): string {
  const source = graph.nodes.find(n => n.id === edge.source)
  const output = source ? dataOutputs(source, catalog)[edge.sourcePort] : undefined
  return `Remove connection from ${nodeTitle(source, catalog, 'a removed step')} · ${output?.label ?? portLabel(edge.sourcePort)}`
}
