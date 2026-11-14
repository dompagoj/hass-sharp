import { nodeCatalog, refinesType, resolveTypeSpec, type VisualCatalogSnapshot } from './contract'

const builtIn: Record<string, string> = {
  string: 'Text', number: 'Number', boolean: 'True/False', object: 'Object', array: 'List',
  entity_id: 'Entity', target: 'Target', 'global::HassSharp.EntityStateSnapshot': 'Entity state',
}
/** Names of literal kinds and categories that are a primitive, for library types of that kind. */
const primitiveNames: Record<string, string> = { boolean: 'True/False', number: 'Number', integer: 'Number', string: 'Text', object: 'Object', array: 'List' }
/** C# type keywords, which catalogs use as labels of CLR types. */
const csharpKeywords = new Set(['bool', 'byte', 'sbyte', 'char', 'decimal', 'double', 'float', 'int', 'uint', 'long', 'ulong', 'short', 'ushort', 'nint', 'nuint', 'object', 'string', 'dynamic'])
/** A type entry's text: a key of another JSON kind (a crafted `"label": 5`) reads as none, as `Text` does in C#. */
const text = (value: unknown) => typeof value === 'string' ? value : ''
/** `binary_sensor` → `Binary sensor`. */
export const domainWords = (domain: string) => { const words = domain.replaceAll('_', ' '); return words.charAt(0).toUpperCase() + words.slice(1) }
/** How many types deep a type is worded ("List of List of …"); deeper, it reads as Object. `Words.LabelDepth` in C#. */
export const labelDepth = 16
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
 * The friendly name of a type for people (design direction, shared vocabulary): built-in names ("Text", "True/False"),
 * `entity_id:<domain>` as "<Domain> entity", a library type of a primitive kind by that primitive's name, a list or
 * enumerable of T as "List of <T>", a nullable as its inner type, and anything else as its last name segment in
 * words ("Reading"). Never a namespace, `global::`, generic brackets, `?` or `[]`.
 */
export function typeLabel(typeId: string, catalog: VisualCatalogSnapshot = nodeCatalog, seen: ReadonlySet<string> = new Set()): string {
  if (Object.hasOwn(builtIn, typeId)) return builtIn[typeId]
  const entity = /^entity_id:([a-z0-9_]+)$/.exec(typeId)
  if (entity) return `${domainWords(entity[1])} entity`
  // A type that names itself as its own element (directly or through others) reads as a plain object, and so does one
  // worded `labelDepth` types deep (a very long id or chain of element types), as `VisualGraphValidator.Words` does.
  if (seen.has(typeId) || seen.size >= labelDepth) return 'Object'
  const inner = (id: string) => typeLabel(id, catalog, new Set([...seen, typeId]))
  const spec = resolveTypeSpec(typeId, catalog)
  // Nullable: the inner type's name.
  if (typeId.endsWith('?')) return inner(typeId.slice(0, -1))
  const element = text(spec?.elementType)
  if (element) return `List of ${inner(element)}`
  if (typeId.endsWith('[]')) return `List of ${inner(typeId.slice(0, -2))}`
  // Multi-dimensional arrays and tuples have no friendly element name: "List" and "Object".
  if (/\[,+\]\??$/.test(typeId)) return 'List'
  if (typeId.replace(/^global::/, '').startsWith('(')) return 'Object'
  const generic = collection.exec(typeId)
  if (generic) return `List of ${inner(generic[1])}`
  const kind = text(spec?.literalKind).toLowerCase(), category = text(spec?.category).toLowerCase()
  if (Object.hasOwn(primitiveNames, kind)) return primitiveNames[kind]
  if (category !== 'object' && Object.hasOwn(primitiveNames, category) && kind !== 'enum') return primitiveNames[category]
  // A label written for people keeps its text; a CLR spelling (a namespace, generics, `?`, `[]` or a C# keyword
  // such as `bool`) never does.
  const label = text(spec?.label)
  if (label && !/[.:<>?[\](),]/.test(label) && !csharpKeywords.has(label)) return label
  const words = clrWords(typeId)
  return /[()[\],]/.test(words) ? 'Object' : words
}

/** The parts of a dotted name, splitting only outside type arguments (`List<System.String>` stays one part). */
function nameParts(name: string): string[] {
  const parts: string[] = []
  let depth = 0, start = 0
  for (let i = 0; i < name.length; i++) {
    const c = name[i]
    if (c === '<' || c === '[' || c === '(') depth++
    else if (c === '>' || c === ']' || c === ')') depth--
    else if (c === '.' && depth === 0) { parts.push(name.slice(start, i)); start = i + 1 }
  }
  parts.push(name.slice(start))
  return parts
}
/**
 * The C# names a type can go by after its label, shortest first: the last part of its catalog label (`int`), then its
 * full name less `global::` from the last part outwards (`Mode`, `A.Mode`, `NodeLib.A.Mode`), then its id.
 */
function csharpNames(id: string, catalog: VisualCatalogSnapshot): string[] {
  const full = id.replace(/^global::/, '')
  const parts = nameParts(full)
  const label = Object.hasOwn(catalog.types, id) ? catalog.types[id].label : undefined
  const names = [nameParts(typeof label === 'string' ? label : full).pop()!]
  for (let k = 1; k <= parts.length; k++) names.push(parts.slice(-k).join('.'))
  names.push(id)
  return [...new Set(names)]
}
/**
 * The names of types read side by side, so that none two read the same (the Constant's Type list names its types by
 * the same rule): each type's label, and where several share one, each that a C# library brings (its id starts with
 * `global::`) shows after it the shortest C# name no other of them goes by ("Mode (A.Mode)", "Mode (B.Mode)",
 * "Number (int)"), never the label again. Mirrored by `VisualGraphValidator.Words.DistinctLabels`.
 */
export function distinctTypeLabels(ids: string[], catalog: VisualCatalogSnapshot = nodeCatalog): Map<string, string> {
  const unique = [...new Set(ids)]
  const plain = new Map(unique.map(id => [id, typeLabel(id, catalog)] as const))
  const labels = new Map(plain)
  for (const label of new Set(plain.values())) {
    const group = unique.filter(id => plain.get(id) === label)
    const named = group.filter(id => id.startsWith('global::'))
    if (group.length < 2 || !named.length) continue
    const choices = named.map(id => csharpNames(id, catalog).filter(name => name !== label))
    named.forEach((id, i) => {
      const own = choices[i].find(name => choices.every((others, j) => j === i || !others.includes(name))) ?? id
      labels.set(id, `${label} (${own})`)
    })
  }
  return labels
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
