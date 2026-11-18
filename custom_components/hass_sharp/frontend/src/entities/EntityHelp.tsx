import { Dialog } from '../components/Dialog'

/** Help for the entity definition editor, opened from its "⋯" menu. */
export function EntityHelp(props: { open: boolean; onClose: () => void }) {
  return (
    <Dialog open={props.open} title="Entity definition help" width="large" onClose={props.onClose}>
      <h3>What an entity definition is</h3>
      <p>An entity definition creates entities in Home Assistant, such as a binary sensor, a sensor or a switch, and works out what they do with steps. Its entities, device and settings are in the panel on the right when no step is selected; on a phone, tap "Entities, device and settings" at the bottom.</p>
      <h3>State, When commanded and triggers</h3>
      <p>A State step gives an entity its state: wire values into it, and it is worked out again whenever one of them changes. A When commanded step runs its steps when Home Assistant asks the entity to do something, such as turn a switch on; Set state then says what the entity is now. When started runs once each time the definition starts. A trigger, such as a time pattern, runs its steps each time it fires: to poll a device, for example.</p>
      <p>When commanded and When started record their runs, listed under Runs; a trigger records them only when its run history is turned on, and a State never does.</p>
      <h3>Keys</h3>
      <p>Each entity has a key: lower-case letters, digits and underscores. Together with the entity definition's id, it is the entity's identity in Home Assistant, so it cannot change once saved. Before the first save, changing a key changes it in every step that uses the entity.</p>
      <h3>Settings and secrets</h3>
      <p>A setting is a value the steps read with a Setting step, such as an address. Mark a password or a token as a secret: once saved, its value is never shown again. It is left out of this browser's drafts and of Export graph. Run traces, the Log step and the service logs show it as hidden. A true or false worked out from it is shown. A step that returns what it was given, such as a custom step or a service response, can still show it. Type a new value to replace it, or clear it.</p>
      <h3>In Home Assistant</h3>
      <p>Saving creates or updates the entities at once. Grouped as a device, they share a device page, named after the entity definition; otherwise each is an entity on its own. Find them under Settings, Devices &amp; services, Entities, and try a switch's commands there. Turned off, the definition's entities stay in Home Assistant as unavailable; deleting it removes them.</p>
      <h3>Keyboard</h3>
      <p>Ctrl+Z undoes and Ctrl+Y (or Ctrl+Shift+Z) redoes, the form's changes as well as the steps'. A text field takes its change when you leave it or press Enter; Escape puts back what it had.</p>
    </Dialog>
  )
}
