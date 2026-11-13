import { dataOutputs, inputValue, type Graph, type GraphNode, type VisualCatalogSnapshot } from '../contract'
import { nodeTitle } from '../flow/flowModel'
import { nodeInputs, portLabel } from '../graph'
import { serviceFieldLabel } from '../nodeSummary'
import type { TraceValue } from '../../types'

/** Words for the trace view's value panel: port names, where an input came from, and the order inputs are listed in. */

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

// The recorder's secret-name rule (TraceJson.Secret in the C# library), kept identical: both are pinned to
// tests/fixtures/trace-secret-names.json. The recorder writes "***" under such a name with no flag inside a value.
const secretNames = new Set(['password', 'passwd', 'passcode', 'secret', 'client_secret', 'token', 'access_token', 'api_key', 'apikey', 'api-key', 'authorization', 'code', 'pin'])
const secretSuffixes = ['_token', '_secret', '_password', '_passcode', '_key']

/**
 * True when the recorder hides the value of a property, dictionary key or port with this name. A name in a recorded
 * tree may carry a `#n` position the recorder added to tell alike keys apart; the rule is checked without it.
 */
export function secretName(name: string): boolean {
  let key = name.replace(/#\d+$/, '')
  if (key.startsWith('field:')) key = key.slice(6)
  key = key.toLowerCase()
  return secretNames.has(key) || secretSuffixes.some(suffix => key.endsWith(suffix))
}
