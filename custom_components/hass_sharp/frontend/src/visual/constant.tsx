import { createEffect, For, Show } from 'solid-js'
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
/** The types a Constant can hold, as its card and its panel list them (a stored Type left out stays shown). */
export const constantTypes = (catalog: VisualCatalogSnapshot) => Object.keys(catalog.types).filter(id => constantType(catalog.types[id]))

/** The card's Type select of a Constant, beside its value editor (the panel has its own, with the same options). */
export function ConstantType(props: { type: unknown; catalog: VisualCatalogSnapshot; disabled?: boolean; onChange(type: string | undefined): void }) {
  const current = () => (typeof props.type === 'string' ? props.type : undefined)
  const options = () => constantTypes(props.catalog)
  let select!: HTMLSelectElement
  // The chosen option follows the Type once the options are in place (and again when the Type changes elsewhere: undo).
  createEffect(() => { options(); select.value = current() ?? '' })
  return (
    <select ref={select} class="hs-input hs-kind-switch nodrag nopan" aria-label="Type" disabled={props.disabled}
      onChange={event => props.onChange(event.currentTarget.value === '' ? undefined : event.currentTarget.value)}>
      <Show when={current() === undefined || !options().includes(current()!)}>
        <option value={current() ?? ''} selected>{current() === undefined ? 'Not set' : typeLabel(current()!, props.catalog)}</option>
      </Show>
      <For each={options()}>{option => <option value={option} selected={current() === option}>{typeLabel(option, props.catalog)}</option>}</For>
    </select>
  )
}
