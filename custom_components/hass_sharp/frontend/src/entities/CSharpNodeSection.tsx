import { Show } from 'solid-js'
import { DeviceKeyField, DevicePartOfField } from './DeviceNodeSection'
import { EntityDeviceField, EntityKeyField } from './EntityNodeSection'
import type { StepPickerContext } from './NodePickers'

/**
 * The right panel's section of a class node (a C# entity or C# device, a node library's entity or device class), above
 * its Edit code (a C# node's) or the node library it comes from, and its settings (its inputs, each edited by its type
 * in the Settings list under it).
 *
 * An entity class: its key, as an Entity node's (read only once saved), and the Device node it is on, chosen as an
 * Entity node's. Its class's title is the inspector's header: its class names it.
 *
 * A device class: its key, as a Device node's (read only once saved; the devices that are part of it follow a rename),
 * and what it is part of, as a Device node's. Its name is the inspector's header ("Device name").
 */
export function CSharpNodeSection(props: { ctx: StepPickerContext }) {
  /** What the node's kind declares, by its catalog entry. */
  const declares = () => { const kind = props.ctx.node().kind, nodes = props.ctx.catalog().nodes; return Object.hasOwn(nodes, kind) ? nodes[kind].declares : undefined }
  return (
    <Show when={declares() === 'device'} fallback={<><EntityKeyField ctx={props.ctx} /><EntityDeviceField ctx={props.ctx} /></>}>
      <DeviceKeyField ctx={props.ctx} />
      <DevicePartOfField ctx={props.ctx} />
    </Show>
  )
}
