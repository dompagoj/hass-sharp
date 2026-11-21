import { createResource, createSignal, createUniqueId, For, Match, Show, Switch } from 'solid-js'
import { Icon } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { LoadFailed } from '../components/LazyLoad'
import { DefinitionCode } from '../visual/typeDefinition'
import { builtinEntities, sharedClasses, type BuiltinClass } from './builtinEntities'

// The classes' source: a chunk of its own, loaded when the first class is opened, never with the page or the dialog.
let sourceModule: Promise<typeof import('./builtinSource')> | undefined
const loadSource = () => sourceModule ??= import('./builtinSource').catch((error: unknown) => { sourceModule = undefined; throw error })

/** A class's source, read only, once its module has loaded. */
function ClassCode(props: { item: BuiltinClass }) {
  const [attempt, setAttempt] = createSignal(1)
  const [text] = createResource(attempt, async () => (await loadSource()).classSource(props.item.className, props.item.files) ?? '')
  return (
    <Switch fallback={<p class="hs-muted" role="status">Loading the source…</p>}>
      <Match when={text.state === 'errored'}><LoadFailed what="source" error={text.error} onRetry={() => setAttempt(n => n + 1)} /></Match>
      <Match when={text.state === 'ready'}><DefinitionCode text={text()!} label={`C# source of ${props.item.className}`} /></Match>
    </Switch>
  )
}

/** One class of the list: its name, a button that shows and hides its source below it. */
function ClassItem(props: { item: BuiltinClass; label?: string }) {
  const [open, setOpen] = createSignal(false)
  const id = createUniqueId()
  return (
    <li class="hs-entity-source__item">
      <button type="button" class="hs-entity-source__toggle" aria-expanded={open()} aria-controls={open() ? id : undefined} onClick={() => setOpen(!open())}>
        <Icon name="chevron" />
        <Show when={props.label} fallback={<code class="hs-entity-source__name">{props.item.className}</code>}>
          <span class="hs-entity-source__name">{props.label}</span>
          <code>{props.item.className}</code>
        </Show>
      </button>
      <Show when={open()}><div id={id} class="hs-entity-source__code"><ClassCode item={props.item} /></div></Show>
    </li>
  )
}

/** Help for the entity definition editor, opened from its "⋯" menu. */
export function EntityHelp(props: { open: boolean; onClose: () => void }) {
  return (
    <Dialog open={props.open} title="Entity definition help" width="large" onClose={props.onClose}>
      <h3>What an entity definition is</h3>
      <p>An entity definition creates entities in Home Assistant, each a binary sensor, a sensor, a switch, a button, a number, a select, a text, a light, a cover or a camera, and works out what they do with steps. Each entity is an Entity node on the canvas: add one from the step library, and select it to edit its key, type and options in the panel on the right. To group entities as a device, add a Device node and connect its Entities pin to each entity's Device pin. The definition's settings are in that panel when no step is selected; on a phone, tap "Settings" at the bottom.</p>
      <h3>State, commands and triggers</h3>
      <p>An Entity node's inputs are its state: wire values into them, and the state is worked out again whenever an entity chosen in a step it reads, such as Has state or Current state, changes (not one read by a step such as Read entity). Each of its commands, such as Turn on, is an output: the steps connected to it run when Home Assistant asks the entity to do that, and Set state then says what the entity is now. When started runs once each time the definition starts. A trigger, such as a time pattern, runs its steps each time it fires: to poll a device, for example. Run now in the top bar starts a Trigger step by hand while the saved definition is on and running, with no unsaved changes.</p>
      <p>Set state sets the values you give it and keeps the others as they are, such as a light's brightness when only On is given. A command gives its values, such as the brightness asked for, as outputs of the Entity node. A button has no state: the steps on its Press run each time it is pressed.</p>
      <p>An entity's commands and When started record their runs, listed under Runs, unless their run history is turned off; a trigger, an entity's state and its frames record them only when it is turned on, and a trigger also when it is started with Run now.</p>
      <h3>Keys</h3>
      <p>Each entity has a key: lower-case letters, digits and underscores. Together with the entity definition's id, it is the entity's identity in Home Assistant, so it cannot change once saved. Before the first save, changing a key changes it in every step that uses the entity.</p>
      <h3>Settings and secrets</h3>
      <p>A setting is a value the steps read with a Setting step, such as an address. Mark a password or a token as a secret: once saved, its value is never shown again. It is left out of this browser's drafts and of Export graph. Run traces, the Log step and the service logs show it as hidden. A true or false worked out from it is shown. A step that returns what it was given, such as a custom step or a service response, can still show it. Type a new value to replace it, or clear it.</p>
      <h3>Cameras</h3>
      <p>A camera shows the picture connected to its Entity node's Image input, made from values alone: an Entity of another camera, such as camera.front_door, into Camera frame, then Draw rectangle or Draw text, then the Image input. No step runs: the picture is made again each time Home Assistant asks for a frame, which it does only while something is watching the camera or asks for its picture.</p>
      <p>Camera frame says on its card how it takes its frames. Live: while it makes a camera's frames, connected into that camera's Image directly or through value nodes, it gives raw frames from one shared ffmpeg feed of the source camera, at its full frame rate. Snapshot: read anywhere else, such as by a step that sends a doorbell notification, it takes one picture from Home Assistant and passes it on unchanged.</p>
      <h3>HTTP requests, downloads and restored state</h3>
      <p>HTTP request asks a device or a web service at an http:// or https:// address. It checks the server's certificate, waits 10 seconds unless you give it another timeout, reads answers of up to 4 MiB, and fails on an error status unless you turn that off. It follows redirects; one to a different host fails the step when it has headers, so they are never sent there. Turn on Keep the response out of run history when an answer can hold a password or a token.</p>
      <p>Download file fetches a file once and checks it against its SHA-256 checksum, of the file as the server sends it. The file is kept under .storage/hass_sharp/files/ in the configuration folder, and is not downloaded again.</p>
      <p>Entities that nothing works out again when the definition starts, such as switches, numbers, selects, texts, lights and covers set by commands, come back with the state they had after Home Assistant restarts or the definition is saved. A light turned on without a brightness keeps its last one, or comes on at full brightness the first time.</p>
      <h3>In Home Assistant</h3>
      <p>Saving creates or updates the entities at once. The entities wired to a Device node share its device page, named as the node is; any other is an entity on its own. With Part of, a device is shown as connected through another device, such as a hub: another Device node of this definition, or one Home Assistant already has. Find them under Settings, Devices &amp; services, Entities, and try their commands there. Turned off, the definition's entities stay in Home Assistant as unavailable; deleting it removes them. A definition saved off, such as a copy, creates its entities the first time it is turned on.</p>
      <h3>Built-in entities</h3>
      <p>This is the code HassSharp runs for each type of entity. To make an entity of your own, add a C# entity node, derive its class from one of these and override its methods, such as a switch's TurnOn. Setup, Start, Stop, Every, Track and Services come from the classes under What they share. A member marked internal alone, not protected internal, is the library's own: a node's class cannot override or call it. Select a name to show its code, and select it again to hide it.</p>
      <div class="hs-entity-source">
        <ul class="hs-entity-source__list">
          <For each={builtinEntities}>{entity => <ClassItem item={entity} label={entity.label} />}</For>
        </ul>
        <h4 class="hs-entity-source__shared">What they share</h4>
        <ul class="hs-entity-source__list">
          <For each={sharedClasses}>{shared => <ClassItem item={shared} />}</For>
        </ul>
      </div>
      <h3>Keyboard</h3>
      <p>Ctrl+Z undoes and Ctrl+Y (or Ctrl+Shift+Z) redoes, the form's changes as well as the steps'. A text field takes its change when you leave it or press Enter; Escape puts back what it had.</p>
    </Dialog>
  )
}
