import { nodeCatalog, refinesType, resolveTypeSpec, type VisualCatalogSnapshot } from './contract'

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

const primitive: Record<string, string> = { boolean: 'boolean', number: 'number', string: 'string', object: 'object', array: 'array' }
/** A stable hue (0–359) for an id; `Reading` and `Reading?` share one. */
export function stableHue(id: string): number {
  let hash = 2166136261
  for (const char of id.replace(/\?$/, '')) hash = Math.imul(hash ^ char.charCodeAt(0), 16777619)
  return (hash >>> 0) % 360
}
/**
 * The colour of a type's pins and wires, as a CSS value built from the tokens in `styles/tokens.css`: the five
 * primitive colours, distinct ones for entity ids (any domain), entity state and target, the primitive colour for a
 * library type of a primitive category, and a stable hue derived from the id for every other library type.
 * Colour is never the only signal: pins and chips also name the type.
 */
export function typeColor(typeId: string, catalog: VisualCatalogSnapshot = nodeCatalog): string {
  if (Object.hasOwn(primitive, typeId)) return `var(--hs-port-${primitive[typeId]})`
  if (typeId === 'target') return 'var(--hs-port-target)'
  if (typeId === 'global::HassSharp.EntityStateSnapshot') return 'var(--hs-port-entity-state)'
  const spec = resolveTypeSpec(typeId, catalog)
  if (!spec) return 'var(--hs-port-object)'
  if (refinesType(typeId, 'entity_id', catalog)) return 'var(--hs-port-entity)'
  if (refinesType(typeId, 'target', catalog)) return 'var(--hs-port-target)'
  const category = (spec.category ?? '').toLowerCase()
  if (category !== 'object' && Object.hasOwn(primitive, category)) return `var(--hs-port-${category})`
  return `color-mix(in oklab, oklch(62% 0.15 ${stableHue(typeId)}) var(--hs-hue-mix), var(--hs-text))`
}
/** Lists are drawn as rounded squares; everything else as circles. */
export function typeIsList(typeId: string, catalog: VisualCatalogSnapshot = nodeCatalog): boolean {
  const spec = resolveTypeSpec(typeId, catalog)
  return typeId === 'array' || spec?.literalKind === 'array' || (spec?.category ?? '').toLowerCase() === 'array'
}
