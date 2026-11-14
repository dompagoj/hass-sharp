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
  const spec = resolveTypeSpec(typeId, catalog)
  const source = typeId.startsWith('global::') || typeId.startsWith('(') ? typeId : spec?.clrType
  if (!source) return
  const written = render(source)
  if (written === undefined) return
  return spec?.nullable === true && !written.endsWith('?') ? `${written}?` : written
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

/** A type name as C# writes it in a node's code. */
function name(qualified: string, args: string[]): string | undefined {
  const plain = qualified.replace(/^global::/, '')
  if (args.length === 0 && Object.hasOwn(keywords, plain)) return keywords[plain]
  if (plain === 'System.Nullable' && args.length === 1) return `${args[0]}?`
  if (plain === 'System.ValueTuple' && args.length > 1) return `(${args.join(', ')})`
  let short = plain.replace(customNamespace, '')
  const dot = short.lastIndexOf('.')
  if (dot > 0 && inScope.has(short.slice(0, dot))) short = short.slice(dot + 1)
  return args.length ? `${short}<${args.join(', ')}>` : short
}

/** Reads a type id (`global::A.B<global::C?, D[]>[]?`) and writes it as C#; undefined for anything it cannot read. */
function render(id: string): string | undefined {
  let at = 0
  const space = () => { while (id[at] === ' ') at++ }
  const list = (close: string): string[] | undefined => {
    const items: string[] = []
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
  const type = (): string | undefined => {
    let written: string | undefined
    if (id[at] === '(') {
      at++
      const items = list(')')
      if (!items || items.length < 2) return
      written = `(${items.join(', ')})`
    } else {
      const match = /^(?:global::)?@?[A-Za-z_][\w]*(?:\.@?[A-Za-z_][\w]*)*/.exec(id.slice(at))
      if (!match) return
      at += match[0].length
      let args: string[] = []
      if (id[at] === '<') {
        at++
        const items = list('>')
        if (!items) return
        args = items
      }
      written = name(match[0], args)
      if (written === undefined) return
    }
    for (;;) {
      if (id[at] === '?') { written += '?'; at++; continue }
      const rank = /^\[,*\]/.exec(id.slice(at))
      if (rank) { written += rank[0]; at += rank[0].length; continue }
      break
    }
    // A tuple element's name (`(double Average, double Max)`) is kept.
    const element = /^ (@?[A-Za-z_]\w*)(?=\s*[,)])/.exec(id.slice(at))
    if (element) { at += element[0].length; written = `${written} ${element[1]}` }
    return written
  }
  const written = type()
  return written !== undefined && at === id.length ? written : undefined
}
