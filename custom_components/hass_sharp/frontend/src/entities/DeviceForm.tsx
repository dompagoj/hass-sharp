import { createEffect, createSignal, createUniqueId, For, on, Show, useContext } from 'solid-js'
import { QueryClientContext, useQuery } from '@tanstack/solid-query'
import { HassCtx } from '../context'
import { useLoadedRegistry } from '../registry'
import type { VisualGraph } from '../visual/graph'
import { SearchSelect } from '../visual/widgets/SearchSelect'
import { entityApi, entityTypesQueryKey } from './api'
import { definitionOf, setDevice, withDetail, withVia } from './definitionEdits'
import { SelectField, SwitchField, TextField } from './formFields'
import type { DefinitionDevice, DefinitionSummary, EntityType } from './model'

const fields: { member: keyof DefinitionDevice; label: string; hint?: string; type?: string }[] = [
  { member: 'manufacturer', label: 'Manufacturer' },
  { member: 'model', label: 'Model' },
  { member: 'swVersion', label: 'Software version' },
  { member: 'hwVersion', label: 'Hardware version' },
  { member: 'configurationUrl', label: 'Configuration URL', hint: "A page of the device's own, linked from its page in Home Assistant.", type: 'url' },
  { member: 'suggestedArea', label: 'Suggested area', hint: 'The area Home Assistant puts the device in when it first adds it.' },
]

/**
 * What the editor remembers of a device turned off, so that turning it on again brings it back as it was, `via` too.
 * It outlives the form (which goes whenever a step is selected) for as long as the editor edits one definition: the
 * entity adapter keeps one per editor. `id` is the saved definition's (undefined before its first save); a device
 * remembered for one definition is not offered to another.
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

/**
 * The Device section: whether the entities are grouped as one device, what its page in Home Assistant shows, and what
 * it is part of (`via`). The device is named after the entity definition. Turned on again, the device is the one
 * turned off while this editor was open (`memory`), else the saved definition's (`saved`), else a device with no
 * details.
 */
export function DeviceForm(props: {
  graph: VisualGraph
  commit(graph: VisualGraph): void
  memory: DeviceMemory
  /** The saved definition's id (undefined before its first save) and device. */
  savedId?: string
  savedDevice?: DefinitionDevice | null
  /** The entity ids Home Assistant gave the saved definition's entities: their device is its own. */
  ownEntityIds?: readonly string[]
}) {
  return (
    <DeviceSection device={definitionOf(props.graph).device} onChange={device => props.commit(setDevice(props.graph, device))} memory={props.memory}
      savedId={props.savedId} savedDevice={props.savedDevice} parent={{ ownEntityIds: props.ownEntityIds ?? [] }} />
  )
}

const deviceChoice = 'device'
const definitionChoice = (id: string) => `definition:${id}`
const byName = (a: DefinitionSummary, b: DefinitionSummary) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }) || a.id.localeCompare(b.id)

/**
 * The entity definitions and the classes, read for Part of: undefined until read, and always outside the app's
 * providers (a bare form in a test has none).
 */
function parentSources() {
  const hass = useContext(HassCtx)
  if (!hass || !useContext(QueryClientContext)) return { definitions: () => undefined, types: () => undefined }
  const api = entityApi(hass)
  // Not under the lists' key: a save or a delete here does not change which others are devices.
  const definitions = useQuery(() => ({ queryKey: ['entity-definition-parents'], queryFn: () => api.list(), staleTime: 30_000 }))
  const classes = () => (definitions.data ?? []).some(d => d.logic.kind === 'class')
  // Only for a definition built from a class: whether it is a device is its class's.
  const types = useQuery(() => ({ queryKey: [...entityTypesQueryKey], queryFn: () => api.types(), staleTime: 0, enabled: classes() }))
  return {
    definitions: (): DefinitionSummary[] | undefined => (definitions.isSuccess && (!classes() || !types.isPending) ? definitions.data : undefined),
    types: (): EntityType[] => types.data?.types ?? [],
  }
}

/**
 * Part of: the device this definition's device is connected through, such as a hub. Nothing; another entity
 * definition that is a device, by name (never this one); or a Home Assistant device, chosen with the device picker,
 * by name, without this definition's own device. A definition named that is gone, or no longer a device, is said so
 * and can be cleared. The server takes any of them and leaves out a parent Home Assistant does not have.
 */
function DeviceParent(props: { device: DefinitionDevice | null; onChange(device: DefinitionDevice): void; ownId?: string; ownEntityIds: readonly string[] }) {
  const sources = parentSources()
  const registry = useLoadedRegistry()
  const via = () => props.device?.via ?? null
  const named = () => via()?.definition ?? undefined
  const deviceId = () => (named() ? undefined : via()?.deviceId ?? undefined)
  // "A Home Assistant device…" chosen, the picker open before a device is: the choice is not made until one is picked.
  const [picking, setPicking] = createSignal(false)
  createEffect(on(() => JSON.stringify(via()), () => setPicking(false), { defer: true }))
  const isDevice = (d: DefinitionSummary) =>
    d.device != null || (d.logic.kind === 'class' && sources.types()?.find(t => t.id === d.logic.type)?.kind === 'device')
  const parents = () => (sources.definitions() ?? []).filter(d => d.id !== props.ownId && isDevice(d)).sort(byName)
  /** A named definition not offered: as the list says it, or its id before the list is read. */
  const unlisted = (id: string) => {
    const all = sources.definitions()
    if (!all) return id
    const found = all.find(d => d.id === id)
    return !found ? 'A definition that no longer exists' : id === props.ownId ? 'This entity definition' : `${found.name} (not a device)`
  }
  const choices = () => {
    const id = named()
    const listed = parents().map(d => ({ value: definitionChoice(d.id), label: d.name }))
    if (id !== undefined && !parents().some(d => d.id === id)) listed.push({ value: definitionChoice(id), label: unlisted(id) })
    return [{ value: '', label: 'Nothing' }, ...listed, { value: deviceChoice, label: 'A Home Assistant device…' }]
  }
  const value = () => (picking() || deviceId() !== undefined ? deviceChoice : named() !== undefined ? definitionChoice(named()!) : '')
  const ownDevices = () => new Set((registry()?.entities ?? []).filter(e => props.ownEntityIds.includes(e.entity_id) && e.device_id).map(e => e.device_id))
  const devices = () => (registry()?.devices ?? []).filter(d => !ownDevices().has(d.id)).map(d => ({ value: d.id, label: d.name ?? d.id }))
    .sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: 'base' }) || a.value.localeCompare(b.value))
  const pickerId = createUniqueId(), pendingId = `${pickerId}-pending`
  /** While no device is picked yet, what the device is still part of (it is saved so until one is). */
  const pending = () => {
    if (!picking() || deviceId() !== undefined) return
    const id = named()
    if (id === undefined) return 'Part of nothing until you pick a device.'
    const label = choices().find(c => c.value === definitionChoice(id))?.label ?? id
    return `Still part of ${label.startsWith('A ') ? `a ${label.slice(2)}` : label} until you pick a device.`
  }
  // The picker follows the choice in the tab order; the focus stays on Part of, which arrow keys move through.
  const choose = (choice: string) => {
    if (choice === deviceChoice) setPicking(true)
    else props.onChange(withVia(props.device, choice ? { definition: choice.slice(definitionChoice('').length) } : null))
  }
  return (
    <>
      <SelectField name="via" label="Part of" value={value()} choices={choices()} onChange={choose}
        hint="The device it is connected through, such as a hub." />
      <Show when={value() === deviceChoice}>
        <div class="hs-insp-field hs-def-field" data-field="viaDevice">
          <label class="hs-insp-field__label" for={pickerId}>Home Assistant device</label>
          <SearchSelect id={pickerId} label="Home Assistant device" options={devices()} value={deviceId()} placeholder="Choose a device"
            describedBy={pending() ? pendingId : undefined} onSelect={id => props.onChange(withVia(props.device, { deviceId: id }))} />
          <Show when={pending()}>{text => <small id={pendingId} class="hs-field-hint">{text()}</small>}</Show>
        </div>
      </Show>
    </>
  )
}

/**
 * {@link DeviceForm} over a device itself, for a form that does not edit a graph (a class definition's page).
 * `fixed`: the device always exists (a device class's own, whose details these fields override), so there is no
 * switch, and an empty field says what it falls back to. `toggle`: the switch's label and hint (default: a graph's
 * entities).
 */
export function DeviceSection(props: {
  device: DefinitionDevice | null
  onChange(device: DefinitionDevice | null): void
  memory?: DeviceMemory
  savedId?: string
  savedDevice?: DefinitionDevice | null
  fixed?: { hint: string; placeholder: string }
  toggle?: { label: string; hint: string }
  /** Offer Part of; `ownEntityIds` are the entity ids of the saved definition's entities, whose device is its own. */
  parent?: { ownEntityIds: readonly string[] }
}) {
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
        <SwitchField name="device" label={props.toggle?.label ?? 'Group these entities as a device'} checked={!!device()} onChange={toggle}
          hint={props.toggle?.hint ?? 'Home Assistant shows them together on one device page, named after this entity definition.'} />
      }>
        {fixed => <p class="hs-field-hint">{fixed().hint}</p>}
      </Show>
      <Show when={props.fixed || !!device()}>
        <div class="hs-def-device">
          <For each={fields}>
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
