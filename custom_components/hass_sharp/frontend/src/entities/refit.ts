import { dataInputs, dataOutputs, executionLabel, executionPorts, isAssignable, words, type GraphNode, type VisualCatalogSnapshot } from '../visual/contract'
import { graphTypes } from '../visual/connectionIndex'
import { removeEdge, updateNode, type VisualGraph } from '../visual/graph'
import { optionFits } from './definitionEdits'
import { definitionEntityKinds } from './model'

/**
 * Fitting steps to a change of an entity's type or a step's entity: the values, wires and options that no
 * longer fit go, and one notice names them. The step pickers (`NodePickers`) and the Entity node's section
 * (`EntityNodeSection`) both use it.
 */

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const nodeSpec = (kind: string, catalog: VisualCatalogSnapshot) => (Object.hasOwn(catalog.nodes, kind) ? catalog.nodes[kind] : undefined)

/**
 * What fitting steps to a change removed, by what {@link refitNotice} says of it: the wires that no longer fit
 * (`removed`) and the values (`cleared`), each by its pin's label.
 */
export interface Refitted { removed: string[]; cleared: string[] }
const refitted = (): Refitted => ({ removed: [], cleared: [] })
const gather = (into: Refitted, more: Refitted) => {
  into.removed.push(...more.removed)
  into.cleared.push(...more.cleared)
}

/** The options an entity of the type `domain` keeps of `options`: those its type has too, with a value it takes. */
function fittingOptions(options: unknown, domain: string, catalog: VisualCatalogSnapshot): Record<string, unknown> {
  const spec = catalog.entityDomains && Object.hasOwn(catalog.entityDomains, domain) ? catalog.entityDomains[domain] : undefined
  // A number's min becomes a text's; its slider does not.
  return record(options) ? Object.fromEntries(Object.entries(options).filter(([option, value]) => !!spec && Object.hasOwn(spec.options, option) && optionFits(spec.options[option], value))) : {}
}

/**
 * `next`, a change of `graph` that changed the type of its entity `key`, with every step that names the entity fitted
 * to its new type as a step is fitted to a new entity (`chooseEntity`); what that removed is added to `into`.
 */
function refitSteps(graph: VisualGraph, next: VisualGraph, key: string, catalog: VisualCatalogSnapshot, into: Refitted): VisualGraph {
  const before = graphTypes(graph, catalog), after = graphTypes(next, catalog)
  for (const node of graph.nodes) {
    if (!definitionEntityKinds.includes(node.kind) || node.config.entity !== key) continue
    const fitted = refit(next, node, { ...node.config }, before, after)
    next = fitted.graph
    gather(into, fitted)
  }
  return next
}

/**
 * The graph with the Entity node `nodeId` of the type `domain`, as one change, and what that removed for
 * {@link refitNotice}: the options its new type does not have, or whose value it does not take, go; so do its state
 * values and the wires into them that the new type does not have or take, and its wires out of a command or value the
 * new type does not have. Every step naming its entity is fitted to the new type as a step is fitted to a new entity.
 * The same graph when the node is not an Entity node or already of that type.
 */
export function retypeNode(graph: VisualGraph, nodeId: string, domain: string, catalog: VisualCatalogSnapshot): { graph: VisualGraph; removed: Refitted } {
  const removed = refitted()
  const node = graph.nodes.find(n => n.id === nodeId)
  if (!node || nodeSpec(node.kind, catalog)?.declares !== 'entity' || node.config.domain === domain) return { graph, removed }
  const config = { ...node.config, domain, options: fittingOptions(node.config.options, domain, catalog) }
  // The node's pins come from its own type: the graph's types serve before and after.
  const types = graphTypes(graph, catalog)
  const fitted = refit(graph, node, config, types, types)
  gather(removed, fitted)
  const key = node.config.key
  const next = typeof key === 'string' && key ? refitSteps(graph, fitted.graph, key, catalog, removed) : fitted.graph
  return { graph: next, removed }
}

/**
 * `graph` with `node` given `config`, its entity's state inputs as `after` types them where `before` typed them: a
 * literal value or a wire into an input the step no longer has, or one that no longer takes it, is removed (named in
 * `cleared` and `removed`). An Entity node's state values are its inputs, kept under `arguments`, and its type its
 * own; its wires out of a command or value its new type does not have go too (named in `removed`).
 */
export function refit(graph: VisualGraph, node: GraphNode, given: GraphNode['config'], before: VisualCatalogSnapshot, after: VisualCatalogSnapshot): Refitted & { graph: VisualGraph } {
  const config = { ...given }
  const spec = nodeSpec(node.kind, after)
  const { removed, cleared } = refitted()
  const declared = spec?.dynamicInputs === 'declared'
  if (spec?.dynamicInputs !== 'entity' && !declared) return { graph: updateNode(graph, node.id, { config }), removed, cleared }
  const changed = { ...node, config }
  const was = dataInputs(node, undefined, before), now = dataInputs(changed, undefined, after)
  const fits = (type: unknown, accepted: string[]) => typeof type === 'string' && accepted.some(t => isAssignable(type, t, after))
  const label = (port: string) => words.portLabel(was[port], port)

  // Values: a state input the new type also has keeps a value of a type it takes. A value stored for an input the step
  // did not show (its entity was missing, or of a type the catalog does not have) goes too, unless the new type takes it.
  // An Entity node keeps them under `arguments`, where every member is one.
  const values = declared ? (record(config.arguments) ? (config.arguments = { ...config.arguments }) : {}) : config
  for (const port of Object.keys(values)) {
    if (!declared && (port === 'entity' || Object.hasOwn(spec.config, port) || Object.hasOwn(spec.inputs, port))) continue
    const value = values[port], input = Object.hasOwn(now, port) ? now[port] : undefined
    const literal = record(value) && value.kind === 'literal'
    if (input && (!literal || fits(value.type, input.types))) continue
    delete values[port]
    if (literal) cleared.push(label(port))
  }
  let next = updateNode(graph, node.id, { config })

  // Wires into an input the step had: removed when the input is gone or no longer takes the value.
  for (const edge of graph.edges) {
    if (edge.kind !== 'data' || edge.target !== node.id || !Object.hasOwn(was, edge.targetPort)) continue
    const source = graph.nodes.find(n => n.id === edge.source)
    const output = source ? dataOutputs(source, after)[edge.sourcePort] : undefined
    const input = Object.hasOwn(now, edge.targetPort) ? now[edge.targetPort] : undefined
    if (input && (!output || fits(output.type, input.types))) continue
    next = removeEdge(next, edge.id, after)
    removed.push(label(edge.targetPort))
  }
  if (!declared) return { graph: next, removed, cleared }

  // An Entity node's wires out of a command (a step order wire) or a command's value its new type does not have.
  const hadOutputs = dataOutputs(node, before), hasOutputs = dataOutputs(changed, after)
  const hadPorts = executionPorts(node, before), hasPorts = executionPorts(changed, after)
  for (const edge of graph.edges) {
    if (edge.source !== node.id) continue
    if (edge.kind === 'data') {
      if (!Object.hasOwn(hadOutputs, edge.sourcePort) || Object.hasOwn(hasOutputs, edge.sourcePort)) continue
      removed.push(words.portLabel(hadOutputs[edge.sourcePort], edge.sourcePort))
    } else {
      if (edge.port === undefined || !hadPorts.includes(edge.port) || hasPorts.includes(edge.port)) continue
      removed.push(executionLabel(node, edge.port, before) ?? words.key(edge.port))
    }
    next = removeEdge(next, edge.id, after)
  }
  return { graph: next, removed, cleared }
}

/** What fitting steps removed, in one notice; none when nothing was. */
export function refitNotice({ removed, cleared }: Refitted): string | undefined {
  const list = (names: string[]) => [...new Set(names)].join(', ')
  const notes = [
    ...(removed.length ? [`Removed ${removed.length} ${removed.length === 1 ? 'connection that no longer fits' : 'connections that no longer fit'}: ${list(removed)}`] : []),
    ...(cleared.length ? [`Cleared ${cleared.length === 1 ? 'a value that no longer fits' : `${cleared.length} values that no longer fit`}: ${list(cleared)}`] : []),
  ]
  return notes.length ? `${notes.join('. ')}.` : undefined
}
