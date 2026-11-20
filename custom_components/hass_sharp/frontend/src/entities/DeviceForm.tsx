import { createEffect, createSignal, createUniqueId, For, on, Show, useContext } from 'solid-js'
import { QueryClientContext, useQuery } from '@tanstack/solid-query'
import { HassCtx } from '../context'
import { useLoadedRegistry } from '../registry'
import { SearchSelect, type SearchOption } from '../visual/widgets/SearchSelect'
import { definitionsQueryKey, entityApi } from './api'
import { withDetail, withVia } from './definitionEdits'
import { SelectField, SwitchField, TextField, type Choice } from './formFields'
import type { DefinitionDevice, DefinitionSummary, DefinitionVia } from './model'

/**
 * The details of a device Home Assistant's device page shows, by member, with their labels, hints and input types: one
 * list for a class definition's device (the class page) and a Device node's right panel.
 */
export const deviceFields: { member: Exclude<keyof DefinitionDevice, 'via'>; label: string; hint?: string; type?: string }[] = [
  { member: 'manufacturer', label: 'Manufacturer' },
  { member: 'model', label: 'Model' },
  { member: 'swVersion', label: 'Software version' },
  { member: 'hwVersion', label: 'Hardware version' },
  { member: 'configurationUrl', label: 'Configuration URL', hint: "A page of the device's own, linked from its page in Home Assistant.", type: 'url' },
  { member: 'suggestedArea', label: 'Suggested area', hint: 'The area Home Assistant puts the device in when it first adds it.' },
]

/**
 * What the class page remembers of a device turned off, so that turning it on again brings it back as it was, `via`
 * too, for as long as it edits one definition. `id` is the saved definition's (undefined before its first save); a
 * device remembered for one definition is not offered to another.
 */
export interface DeviceMemory {
  recall(id: string | undefined): DefinitionDevice | undefined
  remember(id: string | undefined, device: DefinitionDevice | undefined): void
}
export function deviceMemory(): DeviceMemory {
  let held: { id: string | undefined; device: DefinitionDevice } | undefined
  return {
    // A new definition's device stays its own once its first save gives it an id.
    recall: id => (held && (held.id === undefined || held.id === id) ? held.device : undefined),
    remember: (id, device) => { held = device ? { id, device: structuredClone(device) } : undefined },
  }
}

const deviceChoice = 'device'
const definitionChoice = (id: string) => `definition:${id}`
const byName = (a: DefinitionSummary, b: DefinitionSummary) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }) || a.id.localeCompare(b.id)

/**
 * The entity definitions, read for Part of: undefined until read, and always outside the app's providers (a bare form
 * in a test has none). Each counts the devices it makes (`devices`), its class's own included.
 */
function parentSources() {
  const hass = useContext(HassCtx)
  if (!hass || !useContext(QueryClientContext)) return { definitions: () => undefined }
  const api = entityApi(hass)
  // Under the lists' key, which every save, delete and switch makes stale: a device definition saved since is offered.
  const definitions = useQuery(() => ({ queryKey: [...definitionsQueryKey, 'parents'], queryFn: () => api.list(), staleTime: 30_000 }))
  return { definitions: (): DefinitionSummary[] | undefined => (definitions.isSuccess ? definitions.data : undefined) }
}

/** What a device is part of: another class definition's device, a Home Assistant device, or another Device node of its definition, by key. */
export interface PartOfVia { definition?: string | null; deviceId?: string | null; device?: string | null }

/**
 * The parents Part of offers between Nothing and "A Home Assistant device…". A class definition's device form offers
 * each other class definition that is a device as a choice of its own; a Device node offers one choice, "Another device
 * of this definition", whose devices are then picked from a list under it (`list`).
 */
export interface PartOfParents {
  choices(): Choice[]
  /** The choice `via` is, when it names one of these parents. */
  chosen(via: PartOfVia | null): string | undefined
  /** The parent `via` names, as a sentence says it: "Still part of {it} until you pick a device." */
  named(via: PartOfVia): string
  /** What choosing one writes; `list.choice` writes nothing until a device is picked from its list. */
  choose?(choice: string): PartOfVia
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
 * and says what the device is part of until then. One control for a class definition's device and a Device node.
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
    else if (props.parents.choose) props.onChange(props.parents.choose(choice))
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

/**
 * Part of a class definition's device: another class definition that is a device, by name (never this one), or a Home
 * Assistant device. A definition built from nodes is no device itself (its devices are its Device nodes), so it is not
 * offered. A definition named that is gone, or is not a device, is said so and can be cleared. The server takes any of
 * them and leaves out a parent Home Assistant does not have.
 */
function DeviceParent(props: { device: DefinitionDevice | null; onChange(device: DefinitionDevice): void; ownId?: string; ownEntityIds: readonly string[] }) {
  const sources = parentSources()
  const named = (via: PartOfVia | null) => via?.definition ?? undefined
  const isDevice = (d: DefinitionSummary) => d.logic.kind === 'class' && d.devices > 0
  const parents = () => (sources.definitions() ?? []).filter(d => d.id !== props.ownId && isDevice(d)).sort(byName)
  /** A named definition not offered, as a choice's label or within a sentence: its id before the list is read. */
  const unlisted = (id: string, inSentence = false) => {
    const all = sources.definitions()
    if (!all) return id
    const found = all.find(d => d.id === id)
    if (!found) return inSentence ? 'a definition that no longer exists' : 'A definition that no longer exists'
    if (id === props.ownId) return inSentence ? 'this entity definition' : 'This entity definition'
    return `${found.name} (not a device)`
  }
  const definitions: PartOfParents = {
    choices: () => {
      const id = named(props.device?.via ?? null)
      const listed = parents().map(d => ({ value: definitionChoice(d.id), label: d.name }))
      if (id !== undefined && !parents().some(d => d.id === id)) listed.push({ value: definitionChoice(id), label: unlisted(id) })
      return listed
    },
    chosen: via => { const id = named(via); return id !== undefined ? definitionChoice(id) : undefined },
    // A parent offered by its name, as it is called; one not offered as the sentence says it.
    named: via => { const id = named(via)!; return parents().find(d => d.id === id)?.name ?? unlisted(id, true) },
    choose: choice => ({ definition: choice.slice(definitionChoice('').length) }),
  }
  return (
    <PartOf via={props.device?.via ?? null} ownEntityIds={props.ownEntityIds} parents={definitions}
      onChange={via => props.onChange(withVia(props.device, via as DefinitionVia | null))} />
  )
}

/**
 * The Device section of a class definition's page: what its device's page in Home Assistant shows, and what it is part
 * of (`via`). The device is named after the entity definition. `fixed`: the device always exists (a device class's own,
 * whose details these fields override), so there is no switch, and an empty field says what it falls back to.
 * `toggle`: the switch that makes the entity a device, with its label and hint; turned on again, the device is the one
 * turned off while the page was open (`memory`), else the saved definition's (`savedDevice`), else one with no details.
 */
export function DeviceSection(props: {
  device: DefinitionDevice | null
  onChange(device: DefinitionDevice | null): void
  memory?: DeviceMemory
  /** The saved definition's id (undefined before its first save) and device. */
  savedId?: string
  savedDevice?: DefinitionDevice | null
  /** Offer Part of; `ownEntityIds` are the entity ids of the saved definition's entities, whose device is its own. */
  parent?: { ownEntityIds: readonly string[] }
} & ({ fixed: { hint: string; placeholder: string }; toggle?: never } | { toggle: { label: string; hint: string }; fixed?: never })) {
  const device = () => props.device
  const detail = (member: keyof DefinitionDevice) => { const value = device()?.[member]; return typeof value === 'string' ? value : '' }
  const toggle = (on: boolean) => {
    if (on) return props.onChange(props.memory?.recall(props.savedId) ?? props.savedDevice ?? {})
    props.memory?.remember(props.savedId, device() ?? undefined)
    props.onChange(null)
  }
  return (
    <section class="hs-def-section" aria-label="Device">
      <h2 class="hs-insp-heading">Device</h2>
      <Show when={props.fixed} fallback={
        <SwitchField name="device" label={props.toggle!.label} checked={!!device()} onChange={toggle} hint={props.toggle!.hint} />
      }>
        {fixed => <p class="hs-field-hint">{fixed().hint}</p>}
      </Show>
      <Show when={props.fixed || !!device()}>
        <div class="hs-def-device">
          <For each={deviceFields}>
            {field => (
              <TextField name={field.member} label={field.label} hint={field.hint} type={field.type} placeholder={props.fixed?.placeholder ?? 'Not set'}
                text={detail(field.member)} commit={value => props.onChange(withDetail(device(), field.member, value.trim()))} />
            )}
          </For>
          <Show when={props.parent}>
            {parent => <DeviceParent device={device()} onChange={props.onChange} ownId={props.savedId} ownEntityIds={parent().ownEntityIds} />}
          </Show>
        </div>
      </Show>
    </section>
  )
}
