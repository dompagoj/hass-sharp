import { resolveTypeSpec, type VisualCatalogSnapshot } from './contract'

/**
 * The C# type a `[Node]` method declares to take or give a value of a catalog type, written as C# code writes it: `bool`
 * for True/False, `double` for Number, `List<string>`, `double?`, `NodeLib.Mode`. Shown beside the friendly name in
 * the inspector, never on cards.
 *
 * - A built-in type (`boolean`, `number`, `entity_id:light`, `target`, …) by the CLR type the catalog gives it
 *   (`clrType`), which is the type the generated code passes to a node method: `NodeLibraryCompiler` maps `bool`,
 *   `double`, `string`, `JsonObject` and `JsonArray` back to these.
 * - A library or Custom C# type by its id (`global::System.Int32` → `int`), with C#'s keywords, `T?` for
 *   `Nullable<T>`, tuples as `(a, b)`, and no namespace that every node's code already has in scope (the global usings
 *   of `DiagnosticsProvider.GlobalUsings`, and `System.Text.Json.Nodes`). A Custom C# node's own type drops its
 *   `NodeLib.Custom_<id>` namespace: in that node's code it is written by its plain name.
 * - Undefined when the type has no single C# type (unknown, or a built-in type without a CLR type): shown as none
 *   rather than as a guess.
 */
export function csharpType(typeId: string, catalog: VisualCatalogSnapshot): string | undefined {
  return csharpTypeParts(typeId, catalog)?.map(part => part.text).join('')
}

/**
 * One piece of a C# type as {@link csharpType} writes it: plain text, or the name of a type with a definition to show
 * (`type`, the catalog id the `type_definition` operation takes; see {@link hasDefinition}).
 */
export interface CSharpPart { text: string; type?: string }

/**
 * The C# type of a catalog type in pieces, so that only the part with a definition to show is a link: in
 * `List<Person>` the `Person`, in `Mode[]` the `Mode`, in `Box<string>` the `Box`. Undefined as {@link csharpType}.
 */
export function csharpTypeParts(typeId: string, catalog: VisualCatalogSnapshot): CSharpPart[] | undefined {
  const spec = resolveTypeSpec(typeId, catalog)
  const own = typeId.startsWith('global::') || typeId.startsWith('(')
  const source = own ? typeId : spec?.clrType
  if (!source) return
  let parts = render(source)
  if (parts === undefined) return
  // A built-in type written by its CLR type (`clrType`) is asked for by its own catalog id.
  if (!own) parts = parts.map(part => (part.type ? { ...part, type: typeId } : part))
  const written = parts.map(part => part.text).join('')
  return spec?.nullable === true && !written.endsWith('?') ? join([...parts, { text: '?' }]) : parts
}

/**
 * Whether a type, by its qualified name (`NodeLib.Mode`, `global::HassSharp.EntityStateSnapshot`), has a C# definition
 * to show: one declared in a node library or a Custom C# node's code (namespace `NodeLib`), or one of HassSharp's own
 * (namespace `HassSharp`). C#'s keywords and everything in the `System` namespaces have none, nor has anything else.
 */
export function hasDefinition(qualified: string): boolean {
  const plain = qualified.replace(/^global::/, '').replaceAll('@', '')
  const space = plain.split('.')[0]
  return plain.includes('.') && (space === 'NodeLib' || space === 'HassSharp')
}

/** The C# types of several catalog types, each once and in order; those with no C# type are left out. */
export function csharpTypes(typeIds: readonly string[], catalog: VisualCatalogSnapshot): string[] {
  return [...new Set(typeIds.map(id => csharpType(id, catalog)).filter((t): t is string => t !== undefined))]
}

const keywords: Record<string, string> = {
  'System.Boolean': 'bool', 'System.Byte': 'byte', 'System.SByte': 'sbyte', 'System.Char': 'char', 'System.Decimal': 'decimal',
  'System.Double': 'double', 'System.Single': 'float', 'System.Int32': 'int', 'System.UInt32': 'uint', 'System.Int64': 'long',
  'System.UInt64': 'ulong', 'System.Int16': 'short', 'System.UInt16': 'ushort', 'System.IntPtr': 'nint', 'System.UIntPtr': 'nuint',
  'System.Object': 'object', 'System.String': 'string', 'System.Void': 'void',
}
/** Namespaces every node's code has in scope: the global usings, and `System.Text.Json.Nodes` for JsonObject and JsonArray. */
const inScope = new Set(['System', 'System.Threading', 'System.Threading.Tasks', 'System.Collections.Generic', 'System.Linq', 'HassSharp', 'System.Text.Json.Nodes'])
const customNamespace = /^NodeLib\.Custom_[A-Za-z0-9_]+\./

/** Neighbouring plain pieces as one. */
function join(parts: CSharpPart[]): CSharpPart[] {
  const joined: CSharpPart[] = []
  for (const part of parts) {
    const last = joined.at(-1)
    if (last && !last.type && !part.type) joined[joined.length - 1] = { text: last.text + part.text }
    else if (part.text) joined.push(part)
  }
  return joined
}

/** A type name as C# writes it in a node's code; `id` is the type's own id, the link when it has a definition. */
function name(qualified: string, args: CSharpPart[][], id: string): CSharpPart[] {
  const plain = qualified.replace(/^global::/, '')
  if (args.length === 0 && Object.hasOwn(keywords, plain)) return [{ text: keywords[plain] }]
  if (plain === 'System.Nullable' && args.length === 1) return join([...args[0], { text: '?' }])
  if (plain === 'System.ValueTuple' && args.length > 1) return join([{ text: '(' }, ...args.flatMap((a, i) => (i ? [{ text: ', ' }, ...a] : a)), { text: ')' }])
  let short = plain.replace(customNamespace, '')
  const dot = short.lastIndexOf('.')
  if (dot > 0 && inScope.has(short.slice(0, dot))) short = short.slice(dot + 1)
  const head: CSharpPart = hasDefinition(plain) ? { text: short, type: id } : { text: short }
  return args.length ? join([head, { text: '<' }, ...args.flatMap((a, i) => (i ? [{ text: ', ' }, ...a] : a)), { text: '>' }]) : [head]
}

/** Reads a type id (`global::A.B<global::C?, D[]>[]?`) and writes it as C# in pieces; undefined for anything it cannot read. */
function render(id: string): CSharpPart[] | undefined {
  let at = 0
  const space = () => { while (id[at] === ' ') at++ }
  const list = (close: string): CSharpPart[][] | undefined => {
    const items: CSharpPart[][] = []
    for (;;) {
      space()
      const item = type()
      if (item === undefined) return
      items.push(item)
      space()
      if (id[at] === ',') { at++; continue }
      if (id[at] === close) { at++; return items }
      return
    }
  }
  const type = (): CSharpPart[] | undefined => {
    let written: CSharpPart[]
    if (id[at] === '(') {
      at++
      const items = list(')')
      if (!items || items.length < 2) return
      written = join([{ text: '(' }, ...items.flatMap((a, i) => (i ? [{ text: ', ' }, ...a] : a)), { text: ')' }])
    } else {
      const start = at
      const match = /^(?:global::)?@?[A-Za-z_][\w]*(?:\.@?[A-Za-z_][\w]*)*/.exec(id.slice(at))
      if (!match) return
      at += match[0].length
      let args: CSharpPart[][] = []
      if (id[at] === '<') {
        at++
        const items = list('>')
        if (!items) return
        args = items
      }
      written = name(match[0], args, id.slice(start, at))
    }
    for (;;) {
      if (id[at] === '?') { written = join([...written, { text: '?' }]); at++; continue }
      const rank = /^\[,*\]/.exec(id.slice(at))
      if (rank) { written = join([...written, { text: rank[0] }]); at += rank[0].length; continue }
      break
    }
    // A tuple element's name (`(double Average, double Max)`) is kept.
    const element = /^ (@?[A-Za-z_]\w*)(?=\s*[,)])/.exec(id.slice(at))
    if (element) { at += element[0].length; written = join([...written, { text: ` ${element[1]}` }]) }
    return written
  }
  const written = type()
  return written !== undefined && at === id.length ? written : undefined
}
