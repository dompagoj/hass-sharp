import { createEffect, createMemo, For, Show } from 'solid-js'
import { validLiteral, type TypeSpec, type VisualCatalogSnapshot } from './contract'
import { typeLabel } from './typeLabels'

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)

/**
 * A Constant's config after its Type changes, as one change: the value stays (now a value of the new type) when it is
 * a valid value of that type, and is cleared otherwise. The same config when the Type is unchanged.
 */
export function retypeConstant(config: Record<string, unknown>, type: string | undefined, catalog: VisualCatalogSnapshot): Record<string, unknown> {
  if (type === config.type) return config
  const next: Record<string, unknown> = { ...config }
  if (type === undefined) delete next.type
  else next.type = type
  const value = config.value
  next.value = type !== undefined && record(value) && value.kind === 'literal' && validLiteral(type, value.value, catalog)
    ? { kind: 'literal', type, value: value.value }
    : { kind: 'unset' }
  return next
}

/** The literal kinds the value editors can type in (a select for an enum, the JSON editor for objects and lists). */
const typedIn = new Set(['boolean', 'number', 'integer', 'string', 'enum', 'object', 'array'])
/**
 * Whether a Constant offers this type: a value of it can be typed in. The catalog says so with `literalKind` (the C#
 * catalog sets it for the built-in value types, entity ids and targets, JSON objects and arrays, the CLR number, text
 * and boolean types and enums; an interface, an abstract or a record type has none). A nullable type is left out: a
 * Constant always has a value, and its type without the `?` is offered.
 */
export const constantType = (spec: TypeSpec | undefined) => !!spec?.literalKind && typedIn.has(spec.literalKind) && spec.nullable !== true
/** A type of the editor's own (Number, Text, Entity ID…), as against one a C# library brings (`global::System.Int32`). */
const builtIn = (id: string) => !id.startsWith('global::')
/**
 * Two ids of one type: the catalog lists each as assignable to the other (`number` and C#'s `double`). A number a wire
 * may narrow (Number into a C# `int`, checked when it runs) is not listed so, and stays a type of its own.
 */
const same = (a: string, b: string, catalog: VisualCatalogSnapshot) =>
  !!catalog.types[a]?.assignableTo?.includes(b) && !!catalog.types[b]?.assignableTo?.includes(a)
/**
 * The types a Constant can hold, as its card and its panel list them (a stored Type left out stays shown). A type that
 * is the same as an offered type of the editor's own (C#'s `double` is Number) is offered once, as that one.
 */
export function constantTypes(catalog: VisualCatalogSnapshot): string[] {
  const all = Object.keys(catalog.types).filter(id => constantType(catalog.types[id]))
  const own = all.filter(builtIn)
  return all.filter(id => builtIn(id) || !own.some(b => same(id, b, catalog)))
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
 * The names a C# type can go by after its label, shortest first: its own short name (`int`, `JsonObject`: the last
 * part of its catalog label), then its full name less `global::` from the last part outwards (`Mode`, `A.Mode`,
 * `NodeLib.A.Mode`), then its id.
 */
function csharpNames(id: string, catalog: VisualCatalogSnapshot): string[] {
  const full = id.replace(/^global::/, '')
  const parts = nameParts(full)
  const names = [nameParts(catalog.types[id]?.label ?? full).pop()!]
  for (let k = 1; k <= parts.length; k++) names.push(parts.slice(-k).join('.'))
  names.push(id)
  return [...new Set(names)]
}
/**
 * How the Type list names each type: no two entries read the same. A type of the editor's own keeps its plain label;
 * another type with the same label shows a C# name after it ("Number (int)", "Number (float)"), the shortest one that
 * tells it apart from the others with that label ("Mode (A.Mode)", "Mode (B.Mode)"), and never the label again
 * ("Mode (Mode)"). `extra` is a stored Type that is not offered, named by the same rule.
 */
export function constantTypeLabels(catalog: VisualCatalogSnapshot, extra?: string): Map<string, string> {
  const ids = [...new Set([...constantTypes(catalog), ...(extra ? [extra] : [])])]
  const plain = new Map(ids.map(id => [id, typeLabel(id, catalog)] as const))
  const labels = new Map(plain)
  for (const label of new Set(plain.values())) {
    const group = ids.filter(id => plain.get(id) === label)
    const named = group.filter(id => !builtIn(id))
    if (group.length < 2 || !named.length) continue
    const choices = named.map(id => csharpNames(id, catalog).filter(name => name !== label))
    // Each takes its first name that no other type of the group can go by; the id, last, is its own.
    named.forEach((id, i) => {
      const own = choices[i].find(name => choices.every((others, j) => j === i || !others.includes(name))) ?? id
      labels.set(id, `${label} (${own})`)
    })
  }
  return labels
}

/** The card's Type select of a Constant, beside its value editor (the panel has its own, with the same options). */
export function ConstantType(props: { type: unknown; catalog: VisualCatalogSnapshot; disabled?: boolean; onChange(type: string | undefined): void }) {
  const current = () => (typeof props.type === 'string' ? props.type : undefined)
  const options = () => constantTypes(props.catalog)
  const labels = createMemo(() => constantTypeLabels(props.catalog, current()))
  let select!: HTMLSelectElement
  // The chosen option follows the Type once the options are in place (and again when the Type changes elsewhere: undo).
  createEffect(() => { options(); select.value = current() ?? '' })
  return (
    <select ref={select} class="hs-input hs-kind-switch hs-constant-type nodrag nopan" aria-label="Type" disabled={props.disabled}
      title={current() === undefined ? undefined : labels().get(current()!)}
      onChange={event => props.onChange(event.currentTarget.value === '' ? undefined : event.currentTarget.value)}>
      <Show when={current() === undefined || !options().includes(current()!)}>
        <option value={current() ?? ''} selected>{current() === undefined ? 'Not set' : labels().get(current()!)}</option>
      </Show>
      <For each={options()}>{option => <option value={option} selected={current() === option}>{labels().get(option)}</option>}</For>
    </select>
  )
}
