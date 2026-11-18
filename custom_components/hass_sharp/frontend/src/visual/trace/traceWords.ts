import { dataOutputs, type Graph, type GraphNode, type VisualCatalogSnapshot } from '../contract'
import { literalTargetEntities, nodeTitle } from '../flow/flowModel'
import { nodeInputs, portLabel } from '../graph'
import { serviceFieldLabel } from '../nodeSummary'
import type { TraceValue } from '../../types'
import { optionLabel, widgetFor, withStates, type StateEntity } from '../widgets'
import type { OptionText } from './traceModel'

/** Words for the trace view's value panel: port names, where an input came from, and the order inputs are listed in. */

const own = <T,>(record: Record<string, T> | undefined, key: string) => (record && Object.hasOwn(record, key) ? record[key] : undefined)
const text = (value: unknown) => (typeof value === 'string' ? value : '')

/**
 * The catalog with the definition of the run's graph in it, when it is an entity definition's and the catalog has none
 * yet: an entity step's state values and its command's data are named from the type of the entity it names.
 */
const lifted = (catalog: VisualCatalogSnapshot, graph: Graph | undefined): VisualCatalogSnapshot =>
  !catalog.definition && graph?.definition && typeof graph.definition === 'object' ? { ...catalog, definition: graph.definition } : catalog

export function inputLabel(graph: Graph, node: GraphNode, port: string, catalog: VisualCatalogSnapshot): string {
  if (node.kind === 'service' && port.startsWith('field:')) return serviceFieldLabel(text(node.config.service), port.slice(6), catalog.services)
  return own(nodeInputs(graph, node, lifted(catalog, graph)), port)?.label ?? portLabel(port)
}
export const outputLabel = (node: GraphNode | undefined, port: string, catalog: VisualCatalogSnapshot, graph?: Graph) =>
  (node ? own(dataOutputs(node, lifted(catalog, graph)), port)?.label : undefined) ?? portLabel(port)

/**
 * How a recorded value of a step's input reads when the input is a choice (a select or a state): the same option
 * label the card and the panel show (`optionLabel`); undefined for any other input. A state is read with the states
 * of the entities the step targets (from the registry's `entities`, once loaded), as its picker labels them.
 */
export function inputOption(graph: Graph, node: GraphNode | undefined, port: string, catalog: VisualCatalogSnapshot, entities?: readonly StateEntity[]): OptionText | undefined {
  const spec = node ? own(nodeInputs(graph, node, lifted(catalog, graph)), port) : undefined
  if (!node || !spec) return
  const base = widgetFor(spec, catalog)
  if (base.kind !== 'select' && base.kind !== 'state') return
  const widget = withStates(base, literalTargetEntities(node), entities)
  return value => optionLabel(widget, value)
}

/** Where an input's value came from: "From Motion · Entity" or "Value". */
export function inputSource(graph: Graph, value: TraceValue, catalog: VisualCatalogSnapshot): string {
  if (value.src) {
    const source = graph.nodes.find(n => n.id === value.src!.node)
    return `From ${nodeTitle(source, catalog, 'a removed step')} · ${outputLabel(source, value.src.port, catalog, graph)}`
  }
  return 'Value'
}

/** The order the panel lists a step's inputs in: the step's own order, then anything else that was recorded. */
export function inputOrder(graph: Graph, node: GraphNode, recorded: Record<string, unknown>, catalog: VisualCatalogSnapshot): string[] {
  const declared = Object.keys(nodeInputs(graph, node, lifted(catalog, graph)))
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

/**
 * Why a value was not recorded (`redacted`), in words: a port whose name looks like a secret is hidden for its name; one
 * that is a secret setting's value, or a hidden response's body or headers (Hide response), or one a wire brings from
 * either, for that. The trace marks only that a value was hidden, not why (`TraceJson.Entry`): a value worked out from
 * one of them further back is said in words true of both.
 */
export function hiddenReason(graph: Graph, node: GraphNode, port: string, value: TraceValue, side: 'in' | 'out' = 'in'): string {
  if (secretName(port)) return 'Not recorded because its name looks like a secret.'
  const from = side === 'out' ? node : value.src ? graph.nodes.find(n => n.id === value.src!.node) : undefined
  const fromPort = side === 'out' ? port : value.src?.port
  if (from?.kind === 'setting') return 'Not recorded because it comes from a secret setting.'
  if (from?.kind === 'http_request' && from.config.hideResponse === true && (fromPort === 'body' || fromPort === 'headers'))
    return 'Not recorded because it is part of a response kept out of run history.'
  return 'Not recorded because it may hold a secret: it comes from a secret setting or a response kept out of run history.'
}
