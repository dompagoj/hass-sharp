import { createEffect, createSignal, createUniqueId, on, Show } from 'solid-js'
import { useLoadedRegistry } from '../registry'
import { SearchSelect, type SearchOption } from '../visual/widgets/SearchSelect'
import { SelectField, type Choice } from './formFields'
import type { DefinitionDevice } from './model'

/**
 * The details of a device Home Assistant's device page shows, by member, with their labels, hints and input types, as a
 * Device node's right panel lists them.
 */
export const deviceFields: { member: Exclude<keyof DefinitionDevice, 'via'>; label: string; hint?: string; type?: string }[] = [
  { member: 'manufacturer', label: 'Manufacturer' },
  { member: 'model', label: 'Model' },
  { member: 'swVersion', label: 'Software version' },
  { member: 'hwVersion', label: 'Hardware version' },
  { member: 'configurationUrl', label: 'Configuration URL', hint: "A page of the device's own, linked from its page in Home Assistant.", type: 'url' },
  { member: 'suggestedArea', label: 'Suggested area', hint: 'The area Home Assistant puts the device in when it first adds it.' },
]

const deviceChoice = 'device'

/** What a device is part of: a Home Assistant device, or another device of its definition, by key. */
export interface PartOfVia { deviceId?: string | null; device?: string | null }

/**
 * The parents Part of offers between Nothing and "A Home Assistant device…": a Device node offers one choice, "Another
 * device of this definition", whose devices are then picked from a list under it (`list`).
 */
export interface PartOfParents {
  choices(): Choice[]
  /** The choice `via` is, when it names one of these parents. */
  chosen(via: PartOfVia | null): string | undefined
  /** The parent `via` names, as a sentence says it: "Still part of {it} until you pick a device." */
  named(via: PartOfVia): string
  /** A choice with a list of its own: it writes nothing until a device is picked from its list. */
  list?: {
    choice: string
    /** The list's `data-field` and label. */
    field: string
    label: string
    options(): SearchOption[]
    /** The option `via` is, when it is one of the list's. */
    value(via: PartOfVia | null): string | undefined
    via(option: string): PartOfVia
  }
}

/**
 * Part of: the device a device is connected through, such as a hub. Nothing; one of `parents`; or a Home Assistant
 * device, chosen with the device picker, by name, without the devices this definition's own entities are on
 * (`ownEntityIds`). A choice with a list of its own does not change Part of until something is picked from that list,
 * and says what the device is part of until then. One control for a Device node and a C# device.
 */
export function PartOf(props: { via: PartOfVia | null; onChange(via: PartOfVia | null): void; ownEntityIds: readonly string[]; parents: PartOfParents; errors?: string[] }) {
  const registry = useLoadedRegistry()
  const via = () => props.via
  const chosen = () => props.parents.chosen(via())
  const deviceId = () => (chosen() !== undefined ? undefined : via()?.deviceId ?? undefined)
  // A choice with a list of its own chosen, the list open before anything is picked from it: the choice is not made until it is.
  const [picking, setPicking] = createSignal<string>()
  createEffect(on(() => JSON.stringify(via()), () => setPicking(undefined), { defer: true }))
  const choices = () => [{ value: '', label: 'Nothing' }, ...props.parents.choices(), { value: deviceChoice, label: 'A Home Assistant device…' }]
  const value = () => picking() ?? (deviceId() !== undefined ? deviceChoice : chosen() ?? '')
  const ownDevices = () => new Set((registry()?.entities ?? []).filter(e => props.ownEntityIds.includes(e.entity_id) && e.device_id).map(e => e.device_id))
  // Each with its area, as a step's device picker shows it.
  const devices = () => (registry()?.devices ?? []).filter(d => !ownDevices().has(d.id))
    .map(d => ({ value: d.id, label: d.name ?? d.id, detail: d.area_id ? registry()?.area(d.area_id)?.name : undefined }))
    .sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: 'base' }) || a.value.localeCompare(b.value))
  /** While nothing is picked from the list chosen, what the device is still part of (it is saved so until something is). */
  const pending = () => {
    if (picking() === undefined) return
    const current = via()
    const parent = !current ? undefined : chosen() !== undefined ? props.parents.named(current)
      : deviceId() !== undefined ? registry()?.device(deviceId()!)?.name ?? deviceId() : undefined
    return parent === undefined ? 'Part of nothing until you pick a device.' : `Still part of ${parent} until you pick a device.`
  }
  // The picker follows the choice in the tab order; the focus stays on Part of, which arrow keys move through.
  const choose = (choice: string) => {
    if (choice === deviceChoice || choice === props.parents.list?.choice) setPicking(choice)
    else if (!choice) props.onChange(null)
  }
  /** A list under Part of, with what the device is still part of while nothing is picked from it. */
  const Picker = (picker: { field: string; label: string; options: SearchOption[]; value: string | undefined; onSelect(value: string): void }) => {
    const id = createUniqueId(), pendingId = `${id}-pending`
    return (
      <div class="hs-insp-field hs-def-field" data-field={picker.field}>
        <label class="hs-insp-field__label" for={id}>{picker.label}</label>
        <SearchSelect id={id} label={picker.label} options={picker.options} value={picker.value} placeholder="Choose a device"
          describedBy={pending() ? pendingId : undefined} onSelect={picker.onSelect} />
        <Show when={pending()}>{text => <small id={pendingId} class="hs-field-hint">{text()}</small>}</Show>
      </div>
    )
  }
  return (
    <>
      <SelectField name="via" label="Part of" value={value()} choices={choices()} onChange={choose} errors={props.errors}
        hint="The device it is connected through, such as a hub." />
      <Show when={props.parents.list && value() === props.parents.list.choice ? props.parents.list : undefined}>
        {list => <Picker field={list().field} label={list().label} options={list().options()} value={list().value(via())} onSelect={option => props.onChange(list().via(option))} />}
      </Show>
      <Show when={value() === deviceChoice}>
        <Picker field="viaDevice" label="Home Assistant device" options={devices()} value={deviceId()} onSelect={id => props.onChange({ deviceId: id })} />
      </Show>
    </>
  )
}
