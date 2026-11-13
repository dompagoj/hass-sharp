import { nodeCatalog, refinesType, resolveTypeSpec, type VisualCatalogSnapshot } from './contract'

const builtIn: Record<string, string> = {
  string: 'Text', number: 'Number', boolean: 'Yes/No', object: 'Object', array: 'List',
  entity_id: 'Entity', target: 'Target', 'global::HassSharp.EntityStateSnapshot': 'Entity state',
}
/** Names of literal kinds and categories that are a primitive, for library types of that kind. */
const primitiveNames: Record<string, string> = { boolean: 'Yes/No', number: 'Number', integer: 'Number', string: 'Text', object: 'Object', array: 'List' }
/** C# type keywords, which catalogs use as labels of CLR types. */
const csharpKeywords = new Set(['bool', 'byte', 'sbyte', 'char', 'decimal', 'double', 'float', 'int', 'uint', 'long', 'ulong', 'short', 'ushort', 'nint', 'nuint', 'object', 'string', 'dynamic'])
/** `binary_sensor` → `Binary sensor`. */
export const domainWords = (domain: string) => { const words = domain.replaceAll('_', ' '); return words.charAt(0).toUpperCase() + words.slice(1) }
/** Generic collections read as lists of their element type. */
const collection = /^(?:global::)?System\.Collections\.Generic\.(?:IEnumerable|IReadOnlyList|IReadOnlyCollection|IList|ICollection|List)<(.+)>$/
/** The innermost name of a CLR type: no namespace, generic arguments, `?` or `[]`; an interface's `I` dropped. */
function clrWords(id: string): string {
  const name = id.replace(/^global::/, '').replace(/<.*$/s, '').replace(/[?[\]]+$/g, '').split('.').filter(Boolean).at(-1) ?? id
  const bare = /^I[A-Z][a-z]/.test(name) ? name.slice(1) : name
  const words = bare.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2').replaceAll('_', ' ').toLowerCase()
  return words.charAt(0).toUpperCase() + words.slice(1)
}
/**
 * The friendly name of a type for people (design direction, shared vocabulary): built-in names ("Text", "Yes/No"),
 * `entity_id:<domain>` as "<Domain> entity", a library type of a primitive kind by that primitive's name, a list or
 * enumerable of T as "List of <T>", a nullable as its inner type, and anything else as its last name segment in
 * words ("Reading"). Never a namespace, `global::`, generic brackets, `?` or `[]`.
 */
export function typeLabel(typeId: string, catalog: VisualCatalogSnapshot = nodeCatalog): string {
  if (Object.hasOwn(builtIn, typeId)) return builtIn[typeId]
  const entity = /^entity_id:([a-z0-9_]+)$/.exec(typeId)
  if (entity) return `${domainWords(entity[1])} entity`
  const spec = resolveTypeSpec(typeId, catalog)
  // Nullable: the inner type's name.
  if (typeId.endsWith('?')) return typeLabel(typeId.slice(0, -1), catalog)
  if (spec?.elementType) return `List of ${typeLabel(spec.elementType, catalog)}`
  if (typeId.endsWith('[]')) return `List of ${typeLabel(typeId.slice(0, -2), catalog)}`
  const generic = collection.exec(typeId)
  if (generic) return `List of ${typeLabel(generic[1], catalog)}`
  const kind = (spec?.literalKind ?? '').toLowerCase(), category = (spec?.category ?? '').toLowerCase()
  if (Object.hasOwn(primitiveNames, kind)) return primitiveNames[kind]
  if (category !== 'object' && Object.hasOwn(primitiveNames, category) && kind !== 'enum') return primitiveNames[category]
  // A label written for people keeps its text; a CLR spelling (a namespace, generics, `?`, `[]` or a C# keyword
  // such as `bool`) never does.
  if (spec?.label && !/[.:<>?[\]]/.test(spec.label) && !csharpKeywords.has(spec.label)) return spec.label
  return clrWords(typeId)
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
/** Lists (arrays and generic collections) are drawn as rounded squares; everything else as circles. */
export function typeIsList(typeId: string, catalog: VisualCatalogSnapshot = nodeCatalog): boolean {
  const spec = resolveTypeSpec(typeId, catalog)
  return typeId === 'array' || spec?.literalKind === 'array' || (spec?.category ?? '').toLowerCase() === 'array' || collection.test(typeId.replace(/\?$/, ''))
}
