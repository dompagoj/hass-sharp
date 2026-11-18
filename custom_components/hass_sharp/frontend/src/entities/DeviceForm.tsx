import { createSignal, For, Show } from 'solid-js'
import type { VisualGraph } from '../visual/graph'
import { definitionOf, setDevice, updateDevice } from './definitionEdits'
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
 * The Device section: whether the entities are grouped as one device, and what its page in Home Assistant shows. The
 * device is named after the entity definition. Its parent (`via`) has no field here and is kept as stored (ruling R2);
 * turned off and on again in this session, the device comes back as it was.
 */
export function DeviceForm(props: { graph: VisualGraph; commit(graph: VisualGraph): void }) {
  const device = () => definitionOf(props.graph).device
  const detail = (member: keyof DefinitionDevice) => { const value = device()?.[member]; return typeof value === 'string' ? value : '' }
  const [previous, setPrevious] = createSignal<DefinitionDevice>()
  const toggle = (on: boolean) => {
    if (on) return props.commit(setDevice(props.graph, previous() ?? {}))
    setPrevious(device() ?? undefined)
    props.commit(setDevice(props.graph, null))
  }
  return (
    <section class="hs-def-section" aria-label="Device">
      <h2 class="hs-insp-heading">Device</h2>
      <SwitchField name="device" label="Group these entities as a device" checked={!!device()} onChange={toggle}
        hint="Home Assistant shows them together on one device page, named after this entity definition." />
      <Show when={!!device()}>
        <div class="hs-def-device">
          <For each={fields}>
            {field => (
              <TextField name={field.member} label={field.label} hint={field.hint} type={field.type} placeholder="Not set"
                text={detail(field.member)} commit={value => props.commit(updateDevice(props.graph, field.member, value.trim()))} />
            )}
          </For>
        </div>
      </Show>
    </section>
  )
}
