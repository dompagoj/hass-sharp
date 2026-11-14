import { expect, it } from 'vitest'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
import { nodeCatalog, type VisualCatalogSnapshot } from '../src/visual/contract'
import { csharpType, csharpTypeParts, csharpTypes, hasDefinition } from '../src/visual/csharpType'

const catalog = rich.catalog as unknown as VisualCatalogSnapshot

// Every type of the rich catalog, as a node method declares it.
const expected: Record<string, string> = {
  boolean: 'bool',
  number: 'double',
  string: 'string',
  object: 'JsonObject',
  array: 'JsonArray',
  entity_id: 'string',
  target: 'JsonObject',
  'global::HassSharp.EntityStateSnapshot': 'EntityStateSnapshot',
  'global::System.Boolean': 'bool',
  'global::System.Double': 'double',
  'global::System.String': 'string',
  'global::System.Text.Json.Nodes.JsonObject': 'JsonObject',
  'global::System.Text.Json.Nodes.JsonArray': 'JsonArray',
  'global::NodeLib.Reading': 'NodeLib.Reading',
  'global::NodeLib.IReading': 'NodeLib.IReading',
  'global::System.Int32': 'int',
  'global::System.String?': 'string?',
  'global::NodeLib.Mode': 'NodeLib.Mode',
  'global::NodeLib.Reading?': 'NodeLib.Reading?',
  'global::System.String?[]': 'string?[]',
  'global::System.String[]': 'string[]',
  'global::System.String[]?': 'string[]?',
  'global::System.Collections.Generic.IEnumerable<global::System.String>': 'IEnumerable<string>',
  'global::System.Collections.Generic.IReadOnlyList<global::System.String>': 'IReadOnlyList<string>',
  'global::System.Collections.Generic.IList<global::System.String>': 'IList<string>',
  'global::System.Collections.Generic.IEnumerable<global::System.String?>': 'IEnumerable<string?>',
  'global::System.Double?': 'double?',
  'global::System.Single?': 'float?',
  'global::System.Single': 'float',
  'global::System.IComparable': 'IComparable',
  'global::System.IComparable<global::System.Boolean>': 'IComparable<bool>',
  'global::System.Object': 'object',
  'global::System.Numerics.INumber<global::System.Double>': 'System.Numerics.INumber<double>',
  'global::System.Numerics.IBitwiseOperators<global::System.Double, global::System.Double, global::System.Double>': 'System.Numerics.IBitwiseOperators<double, double, double>',
  'global::System.Collections.IEnumerable': 'System.Collections.IEnumerable',
}

it.each(Object.entries(expected))('writes the catalog type %s as the C# type %s', (id, written) => {
  expect(Object.hasOwn(catalog.types, id)).toBe(true)
  expect(csharpType(id, catalog)).toBe(written)
})

it('writes every type of the rich catalog as C#: no global::, no Nullable<>, keywords for the built-in CLR types', () => {
  for (const id of Object.keys(catalog.types)) {
    const written = csharpType(id, catalog)
    expect(written, id).toBeDefined()
    expect(written).not.toMatch(/global::|System\.(Boolean|Double|String|Int32|Single|Object|Nullable)\b/)
    // The nullable flag always shows as `?`.
    if (catalog.types[id].nullable === true) expect(written!.endsWith('?'), id).toBe(true)
  }
})

it.each([
  ['global::System.Nullable<global::System.Double>', 'double?'],
  ['global::System.Collections.Generic.List<global::System.String>', 'List<string>'],
  ['global::System.Collections.Generic.Dictionary<global::System.String, global::System.Int32?>', 'Dictionary<string, int?>'],
  ['global::System.Int32[,]', 'int[,]'],
  ['global::System.Threading.Tasks.Task<global::System.Int64>', 'Task<long>'],
  ['global::System.ValueTuple<global::System.Double, global::System.Double>', '(double, double)'],
  ['(global::System.Double Average, global::System.String? Name)', '(double Average, string? Name)'],
  // A Custom C# node's own type, as that node's code writes it.
  ['global::NodeLib.Custom_make2.Reading', 'Reading'],
  ['global::NodeLib.Custom_a_002D1.Outer.Inner', 'Outer.Inner'],
  ['global::NodeLib.Custom_x.Box<global::NodeLib.Mode>', 'Box<NodeLib.Mode>'],
  ['global::NodeLib.@class', 'NodeLib.@class'],
])('writes %s as %s', (id, written) => {
  expect(csharpType(id, catalog)).toBe(written)
})

it('writes a parametric entity type as its root, the nullable flag as ?, and nothing for what it cannot name', () => {
  expect(csharpType('entity_id:light', nodeCatalog)).toBe('string')
  const flagged = { ...catalog, types: { ...catalog.types, 'global::NodeLib.Flagged': { description: '', nullable: true } } }
  expect(csharpType('global::NodeLib.Flagged', flagged)).toBe('NodeLib.Flagged?')
  // Unknown, without a CLR type, or not a type id: none rather than a guess.
  expect(csharpType('not_a_type', catalog)).toBeUndefined()
  const bare = { ...catalog, types: { ...catalog.types, mystery: { description: 'no CLR type' } } }
  expect(csharpType('mystery', bare)).toBeUndefined()
  expect(csharpType('global::A<', catalog)).toBeUndefined()
  expect(csharpType('global::A B C', catalog)).toBeUndefined()
})

it('names each C# type of several types once, in order', () => {
  expect(csharpTypes(['target', 'entity_id', 'entity_id:light', 'unknown'], nodeCatalog)).toEqual(['JsonObject', 'string'])
})

/** The links of a type's C# text: [text, the type id it asks for]. */
const links = (id: string, types: VisualCatalogSnapshot = catalog) => (csharpTypeParts(id, types) ?? []).filter(p => p.type).map(p => [p.text, p.type])

// Which part of each type of the rich catalog is a link to its definition: library, Custom C# and HassSharp types only.
const linked: Record<string, string[][]> = {
  'global::HassSharp.EntityStateSnapshot': [['EntityStateSnapshot', 'global::HassSharp.EntityStateSnapshot']],
  'global::NodeLib.Reading': [['NodeLib.Reading', 'global::NodeLib.Reading']],
  'global::NodeLib.IReading': [['NodeLib.IReading', 'global::NodeLib.IReading']],
  'global::NodeLib.Mode': [['NodeLib.Mode', 'global::NodeLib.Mode']],
  'global::NodeLib.Profile': [['NodeLib.Profile', 'global::NodeLib.Profile']],
  // A nullable reference: the type it may hold is the link, the ? is not.
  'global::NodeLib.Reading?': [['NodeLib.Reading', 'global::NodeLib.Reading']],
  // A System interface of a library type: only the library type is a link.
  'global::System.IEquatable<global::NodeLib.Reading>': [['NodeLib.Reading', 'global::NodeLib.Reading']],
  'global::System.IEquatable<global::NodeLib.Profile>': [['NodeLib.Profile', 'global::NodeLib.Profile']],
}

it('links, over every type of the rich catalog, exactly the parts that name a library, Custom C# or HassSharp type', () => {
  for (const id of Object.keys(catalog.types)) expect(links(id), id).toEqual(linked[id] ?? [])
  // The pieces always read as the plain C# type.
  for (const id of Object.keys(catalog.types)) expect(csharpTypeParts(id, catalog)!.map(p => p.text).join(''), id).toBe(csharpType(id, catalog))
})

it.each([
  // C#'s keywords and the System namespaces are never links, alone or composed.
  ['boolean', []],
  ['entity_id:light', []],
  ['target', []],
  ['global::System.Collections.Generic.List<global::System.String>', []],
  ['global::System.Text.Json.JsonElement', []],
  ['global::Microsoft.Extensions.Primitives.StringValues', []],
  // Composed: only the part that qualifies.
  ['global::NodeLib.Mode[]', [['NodeLib.Mode', 'global::NodeLib.Mode']]],
  ['global::System.Collections.Generic.List<global::NodeLib.Person>', [['NodeLib.Person', 'global::NodeLib.Person']]],
  ['global::System.Nullable<global::NodeLib.Point>', [['NodeLib.Point', 'global::NodeLib.Point']]],
  ['global::NodeLib.Point?', [['NodeLib.Point', 'global::NodeLib.Point']]],
  ['global::System.Collections.Generic.Dictionary<global::System.String, global::NodeLib.Mode?>', [['NodeLib.Mode', 'global::NodeLib.Mode']]],
  ['(global::NodeLib.Mode Mode, global::System.Double Value)', [['NodeLib.Mode', 'global::NodeLib.Mode']]],
  // A generic library type: its name asks for the whole constructed type; its arguments are links of their own.
  ['global::NodeLib.Box<global::System.String>', [['NodeLib.Box', 'global::NodeLib.Box<global::System.String>']]],
  ['global::NodeLib.Box<global::NodeLib.Mode>', [['NodeLib.Box', 'global::NodeLib.Box<global::NodeLib.Mode>'], ['NodeLib.Mode', 'global::NodeLib.Mode']]],
  // A Custom C# node's own type, written without its namespace, asks for its full id.
  ['global::NodeLib.Custom_make2.Reading', [['Reading', 'global::NodeLib.Custom_make2.Reading']]],
  ['global::HassSharp.ServiceCalls.LightServices', [['HassSharp.ServiceCalls.LightServices', 'global::HassSharp.ServiceCalls.LightServices']]],
])('links in %s: %j', (id, expected) => {
  expect(links(id)).toEqual(expected)
})

it('decides by the namespace: NodeLib and HassSharp have definitions, keywords and System do not', () => {
  expect(hasDefinition('global::NodeLib.Mode')).toBe(true)
  expect(hasDefinition('NodeLib.Custom_x.Reading')).toBe(true)
  expect(hasDefinition('HassSharp.EntityStateSnapshot')).toBe(true)
  expect(hasDefinition('global::NodeLib.@class')).toBe(true)
  for (const id of ['System.String', 'global::System.Text.Json.Nodes.JsonObject', 'System', 'NodeLib', 'HassSharpish.Thing', 'Microsoft.Thing', 'bool', 'Mode'])
    expect(hasDefinition(id), id).toBe(false)
})
