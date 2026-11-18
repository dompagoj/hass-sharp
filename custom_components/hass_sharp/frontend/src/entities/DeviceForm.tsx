import { For, Show } from 'solid-js'
import type { VisualGraph } from '../visual/graph'
import { definitionOf, setDevice, withDetail } from './definitionEdits'
import { SwitchField, TextField } from './formFields'
import type { DefinitionDevice } from './model'

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
 * The Device section: whether the entities are grouped as one device, and what its page in Home Assistant shows. The
 * device is named after the entity definition. Its parent (`via`) has no field here and is kept as stored (ruling R2).
 * Turned on again, the device is the one turned off while this editor was open (`memory`), else the saved
 * definition's (`saved`), else a device with no details.
 */
export function DeviceForm(props: {
  graph: VisualGraph
  commit(graph: VisualGraph): void
  memory: DeviceMemory
  /** The saved definition's id (undefined before its first save) and device. */
  savedId?: string
  savedDevice?: DefinitionDevice | null
}) {
  return (
    <DeviceSection device={definitionOf(props.graph).device} onChange={device => props.commit(setDevice(props.graph, device))} memory={props.memory}
      savedId={props.savedId} savedDevice={props.savedDevice} />
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
        </div>
      </Show>
    </section>
  )
}
