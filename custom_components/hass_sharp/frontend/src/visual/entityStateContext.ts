import { dataOutputs, inputValue, nodeCatalog, refinesType, resolveTypeSpec, typeProperties, validLiteral, type Graph, type GraphNode, type VisualCatalogSnapshot } from './contract'
import { graphTypes } from './connectionIndex'
import { optionWords, record } from './widgets/json'
import type { WidgetOption } from './widgets/model'

export interface EntityStateContext {
  entityIds: string[]
  fallbackStateOptions: WidgetOption[]
}

/**
 * The entities a state input refers to, without evaluating a graph: exact IDs from a literal, Constant or trigger
 * refinement, and enum choices from the connected entity type's State property. A wire always takes precedence,
 * including a wire whose source cannot be resolved.
 */
export function entityStateContext(graph: Graph, node: GraphNode, port: string, catalog: VisualCatalogSnapshot = nodeCatalog): EntityStateContext {
  catalog = graphTypes(graph, catalog)
  const empty = (): EntityStateContext => ({ entityIds: [], fallbackStateOptions: [] })
  const ids = (value: unknown): string[] => [...new Set((Array.isArray(value) ? value : [value])
    .filter((id): id is string => typeof id === 'string' && validLiteral('entity_id', id, catalog)))]
  const literalIds = (value: unknown): string[] => {
    if (!record(value) || value.kind !== 'literal' || typeof value.type !== 'string' || typeof value.value !== 'string' ||
      !refinesType(value.type, 'entity_id', catalog) || !validLiteral(value.type, value.value, catalog)) return []
    return [value.value]
  }
  const edge = graph.edges.find(e => e?.kind === 'data' && e.target === node.id && e.targetPort === port)
  let entityIds: string[], type: string
  if (edge?.kind === 'data') {
    const source = graph.nodes.find(n => n?.id === edge.source)
    const output = source && dataOutputs(source, catalog)[edge.sourcePort]
    if (!source || !output || !refinesType(output.type, 'entity_id', catalog)) return empty()
    type = output.type
    const refined = output.refineFrom?.split('.').reduce<unknown>((value, key) => record(value) && Object.hasOwn(value, key) ? value[key] : undefined, source.config)
    entityIds = output.refineFrom ? ids(refined)
      : source.kind === 'constant' && edge.sourcePort === 'value' ? literalIds(inputValue(source, 'value', catalog)) : []
  } else {
    const operand = inputValue(node, port, catalog)
    entityIds = literalIds(operand)
    if (!entityIds.length) return empty()
    type = record(operand) && typeof operand.type === 'string' ? operand.type : 'entity_id'
  }
  const stateType = typeProperties(catalog, type).find(property => property.name === 'State')?.type
  const state = stateType ? resolveTypeSpec(stateType, catalog) : undefined
  const fallbackStateOptions: WidgetOption[] = state?.literalKind === 'enum' ? (state.enumValues ?? [])
    .flatMap((value, i) => typeof value === 'string' ? [{ value, label: state.enumLabels?.[i] || optionWords(value) }] : []) : []
  return { entityIds, fallbackStateOptions }
}
