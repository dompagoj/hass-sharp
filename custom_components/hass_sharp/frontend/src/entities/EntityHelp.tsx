import { Dialog } from '../components/Dialog'

/** Help for the entity definition editor, opened from its "⋯" menu. */
export function EntityHelp(props: { open: boolean; onClose: () => void }) {
  return (
    <Dialog open={props.open} title="Entity definition help" width="large" onClose={props.onClose}>
      <h3>What an entity definition is</h3>
      <p>An entity definition creates entities in Home Assistant, each a binary sensor, a sensor, a switch, a button, a number, a select, a text, a light, a cover or a camera, and works out what they do with steps. Its entities, device and settings are in the panel on the right when no step is selected; on a phone, tap "Entities, device and settings" at the bottom.</p>
      <h3>State, When commanded and triggers</h3>
      <p>A State step gives an entity its state: wire values into it, and it is worked out again whenever an entity chosen in a Has state or Current state step it reads changes (not one read by a step such as Read entity). A When commanded step runs its steps when Home Assistant asks the entity to do something, such as turn a switch on; Set state then says what the entity is now. When started runs once each time the definition starts. A trigger, such as a time pattern, runs its steps each time it fires: to poll a device, for example. Run now in the top bar starts a Trigger step by hand while the saved definition is on and running, with no unsaved changes.</p>
      <p>Set state sets the values you give it and keeps the others as they are, such as a light's brightness when only On is given. When commanded gives the values of its command, such as the brightness asked for, as its outputs. A button has no state: its When commanded runs each time it is pressed.</p>
      <p>When commanded and When started record their runs, listed under Runs; a trigger or a State records them only when its run history is turned on, and a trigger also when it is started with Run now.</p>
      <h3>Keys</h3>
      <p>Each entity has a key: lower-case letters, digits and underscores. Together with the entity definition's id, it is the entity's identity in Home Assistant, so it cannot change once saved. Before the first save, changing a key changes it in every step that uses the entity.</p>
      <h3>Settings and secrets</h3>
      <p>A setting is a value the steps read with a Setting step, such as an address. Mark a password or a token as a secret: once saved, its value is never shown again. It is left out of this browser's drafts and of Export graph. Run traces, the Log step and the service logs show it as hidden. A true or false worked out from it is shown. A step that returns what it was given, such as a custom step or a service response, can still show it. Type a new value to replace it, or clear it.</p>
      <h3>Cameras</h3>
      <p>A camera shows the picture its last Show frame step gave it, such as one taken with Take a picture and drawn on. With a When a frame is needed step, its steps make a new picture when Home Assistant asks for one, which it does only while something is watching the camera or asks for its picture: nothing runs otherwise.</p>
      <h3>HTTP requests, downloads and restored state</h3>
      <p>HTTP request asks a device or a web service at an http:// or https:// address. It checks the server's certificate, waits 10 seconds unless you give it another timeout, reads answers of up to 4 MiB, and fails on an error status unless you turn that off. It follows redirects; one to a different host fails the step when it has headers, so they are never sent there. Turn on Keep the response out of run history when an answer can hold a password or a token.</p>
      <p>Download file fetches a file once and checks it against its SHA-256 checksum, of the file as the server sends it. The file is kept under .storage/hass_sharp/files/ in the configuration folder, and is not downloaded again.</p>
      <p>Entities that nothing works out again when the definition starts, such as switches, numbers, selects, texts, lights and covers set by commands, come back with the state they had after Home Assistant restarts or the definition is saved. A light turned on without a brightness keeps its last one, or comes on at full brightness the first time.</p>
      <h3>In Home Assistant</h3>
      <p>Saving creates or updates the entities at once. Grouped as a device, they share a device page, named after the entity definition; otherwise each is an entity on its own. With Part of, that device is shown as connected through another device, such as a hub: another entity definition's device, or one Home Assistant already has. Find them under Settings, Devices &amp; services, Entities, and try their commands there. Turned off, the definition's entities stay in Home Assistant as unavailable; deleting it removes them.</p>
      <h3>Keyboard</h3>
      <p>Ctrl+Z undoes and Ctrl+Y (or Ctrl+Shift+Z) redoes, the form's changes as well as the steps'. A text field takes its change when you leave it or press Enter; Escape puts back what it had.</p>
    </Dialog>
  )
}
