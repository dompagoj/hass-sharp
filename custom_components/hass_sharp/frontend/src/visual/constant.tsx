import { createEffect, For, Show } from 'solid-js'
import { validLiteral, type VisualCatalogSnapshot } from './contract'
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

/** The types a Constant can hold, as the panel's Type select lists them: every type of the catalog. */
export const constantTypes = (catalog: VisualCatalogSnapshot) => Object.keys(catalog.types)

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
        <option value="" selected>{current() === undefined ? 'Not set' : current()}</option>
      </Show>
      <For each={options()}>{option => <option value={option} selected={current() === option}>{typeLabel(option, props.catalog)}</option>}</For>
    </select>
  )
}
