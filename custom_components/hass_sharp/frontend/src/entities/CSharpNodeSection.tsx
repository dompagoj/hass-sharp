import { Show } from 'solid-js'
import { DeviceKeyField, DevicePartOfField } from './DeviceNodeSection'
import { EntityDeviceField, EntityKeyField } from './EntityNodeSection'
import type { StepPickerContext } from './NodePickers'

/**
 * The right panel's section of a C# entity or a C# device, above its Edit code and its settings (its inputs, each
 * edited by its type in the Settings list under it).
 *
 * A C# entity: its key, as an Entity node's (read only once saved), and the Device node it is on, chosen as an Entity
 * node's. Its class's title is the inspector's header: its code names it.
 *
 * A C# device: its key, as a Device node's (read only once saved; the devices that are part of it follow a rename), and
 * what it is part of, as a Device node's. Its name is the inspector's header ("Device name").
 */
export function CSharpNodeSection(props: { ctx: StepPickerContext }) {
  return (
    <Show when={props.ctx.node().kind === 'csharp_device'} fallback={<><EntityKeyField ctx={props.ctx} /><EntityDeviceField ctx={props.ctx} /></>}>
      <DeviceKeyField ctx={props.ctx} />
      <DevicePartOfField ctx={props.ctx} />
    </Show>
  )
}
