import { nodeCatalog, resolveTypeSpec, type VisualCatalogSnapshot } from './contract'

const builtIn: Record<string, string> = {
  string: 'Text', number: 'Number', boolean: 'Yes/No', object: 'Object', array: 'List',
  entity_id: 'Entity', target: 'Target', 'global::HassSharp.EntityStateSnapshot': 'Entity state',
}
/** `binary_sensor` → `Binary sensor`. */
export const domainWords = (domain: string) => { const words = domain.replaceAll('_', ' '); return words.charAt(0).toUpperCase() + words.slice(1) }
/**
 * The friendly name of a type for people: built-in names ("Text", "Yes/No"), `entity_id:<domain>` as
 * "<Domain> entity", library types by their catalog label, else the last segment of the id. Never a `global::` name.
 */
export function typeLabel(typeId: string, catalog: VisualCatalogSnapshot = nodeCatalog): string {
  if (Object.hasOwn(builtIn, typeId)) return builtIn[typeId]
  const entity = /^entity_id:([a-z0-9_]+)$/.exec(typeId)
  if (entity) return `${domainWords(entity[1])} entity`
  const label = resolveTypeSpec(typeId, catalog)?.label
  if (label && !label.includes('::')) return label
  return typeId.split(/::|\./).filter(Boolean).at(-1) ?? typeId
}
