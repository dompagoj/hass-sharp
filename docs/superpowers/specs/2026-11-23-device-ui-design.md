# Device pages and device data: design

Approved by the owner on 2026-11-23. The implementation plan is `docs/superpowers/plans/2026-11-23-device-ui.md`.

How it is built, as the owner asked: with `superpowers:subagent-driven-development` in a worktree on the branch
`device-ui`. Every code change, fix rounds included, is written by an Opus 5.5 subagent; the controller writes no
code and reviews each task and the whole branch. No push, tag or release without the owner's go for that item. The
working rules in `docs/superpowers/handoffs/2026-11-23-face-recognition.md` ("Working rules the owner set") stay
in force. A worktree build is live-checked in a throwaway `hass_wt` container on port 5002, since the `hass`
container mounts the main checkout.

## Context

The owner wants the People detector (hass-sharp-vision) to ask the Home Assistant user who an unknown face is,
remember the answer, and tell automations who is in frame ("Ivan is at the door", "Unknown person at the door").
HassSharp cannot support that today. Checked in the code:

- A C# device (`SharpDevice`, `Devices/SharpDevice.cs`) cannot show anything in the panel or receive anything from
  it. The websocket routes (`websocket.py`) cover authoring only.
- A device cannot keep its own data. `SharpFiles` is a download cache and `EntityStateStore` holds entity states
  only. A recompile or restart replaces the device instance, and its fields are lost.
- The panel shows no device images. The integration serves no binary data and pushes nothing to the browser; the
  panel polls.
- `SharpImage` (`Devices/Imaging/SharpImage.cs`) has no crop or resize.
- No device UI, people store or recognition work exists on any branch.

This is the first of three projects, agreed in this order:

1. **This design:** device pages and device data in HassSharp, generic for any C# device.
2. Recognition in hass-sharp-vision: SFace embeddings, grouping unknown faces, the people list, built on 1.
3. The "who" sensor for automations.

Projects 2 and 3 get their own designs. "What the next projects need from this one" at the end keeps this design
honest against them.

## Intent

Any C# device can keep a page in the HassSharp panel, made of lists of cards, and keep data that survives
recompiles and restarts. The user reads the page, fills in fields and presses buttons, and the device's code runs
with what they entered.

## Goals

- A C# device or C# entity (a `csharp_device` or C# entity node; anything deriving `SharpComponent`) describes a
  page in code: lists of cards, each with an optional image, text, fields and buttons.
- A button press runs a handler in the device's code with the card's field values, and the page shows the result.
- The page is in the definition's editor, in the bottom drawer. A count on the tab, on the device's node card and on
  the definition's card in the Entities list says that something is waiting.
- A device keeps data private to itself, and devices can share a named store, so two People detectors can use one
  people list.
- A library imported with Add external gets all of this with only its `.cs` file.
- `SharpImage` can crop and resize, so a device can cut a face out of a frame and show it.

## Non-goals (this round)

- Recognition, embeddings, grouping, the people list and the "who" sensor (projects 2 and 3).
- Firing Home Assistant events from a device.
- Notifying the user outside the panel (phone, persistent notification, Repairs). A device can already call a
  notify service or expose a sensor itself.
- Pages for non-admin users, for dashboards, or outside the HassSharp panel.
- Device pages or device data through the MCP tools.
- Free layout, custom HTML or JavaScript from a device, charts, tables, video.
- Server push to the browser. The panel polls, as it does elsewhere.
- Pages for graph-built (declarative) devices and entities.

## Anti-goals

- A refresh of the page that wipes what the user is typing into a card.
- A press that runs against the wrong card because the page changed between drawing and pressing.
- Device data lost or half-written by a recompile, a restart or a crash during a write.
- Images or stored data reaching anyone but an admin, or leaving the machine (no MCP, no log lines with content, no
  HTTP route without Home Assistant's auth).
- User-assembly objects crossing into Python or between load contexts (kept from the custom entities design): only
  JSON and bytes cross.
- A slow or hanging button handler blocking saves, previews or other definitions.
- A stopped or replaced definition leaking its page, its images or its user types.

## Decisions (owner's)

- **A page the device keeps, not one-off questions.** The device keeps lists of cards up to date. Naming an unknown
  face and managing people (rename, merge, delete) are the same mechanism.
- **Cards described in C#, drawn by the panel.** The device builds data; the panel renders it with its own widgets.
  A device does not ship JavaScript.
- **One people list for all People detectors.** So storage has a private scope and a shared scope.
- **For C# code devices only** (`SharpDevice`, `SharpEntity`), and it shows under the graph editor of that device.
- **The result for automations is a sensor** saying who is in frame (project 3).

## Approaches considered

- **Cards in C#, drawn by the panel (chosen).** Works through Add external with one `.cs` file, looks like the rest
  of the panel, testable without a browser. A device is limited to the card parts HassSharp offers; a new part is a
  HassSharp change.
- **The device ships its own panel component.** Any UI is possible, but an import would bring JavaScript that runs
  in an admin's Home Assistant session, authors would need a frontend build, and panel changes could break
  components. Rejected for the trust step and the upkeep.
- **Home Assistant's own UI (Repairs flows, helper entities).** Can ask a one-off question, but cannot show a list
  of people to manage and is not under the graph editor. Dropped.
- **One-off questions (the owner's first sketch, `SubmitUIRequest`).** Smallest, but leaves no place to rename,
  merge or delete. The owner chose the page; a question is now a card with fields and buttons.

## Design

Everything below this line is my call unless it repeats a decision above. API names are proposals; the builder may
adjust names and signatures where the code suggests better ones, keeping the behaviour.

### 1. The page API (C#)

`SharpComponent` gains `protected SharpUi Ui`, usable from `Start()` on; before that it throws with a plain
sentence, as `Every` does. One page per C# node: a device and the entities it added share the device's page.

```csharp
Ui.List("unknown", "Unknown faces", cards, attention: true, empty: "No unknown faces");
Ui.List("people", "People", people.Select(p => new UiCard(p.Id)
    .Image(p.CropJpeg)                              // SharpImage or JPEG bytes
    .Text(p.Name, $"{p.Samples} samples")           // title, optional detail line
    .Field("name", "Name", p.Name, required: true)  // text; suggestions: [...] offers known values
    .Select("into", "Merge into", otherNames)
    .Action("Rename", v => Rename(p, v["name"]))
    .Action("Delete", _ => Delete(p), confirm: $"Delete {p.Name}?", style: UiActionStyle.Danger)));
Ui.Remove("unknown");
```

- `Ui.List(id, title, cards, attention, empty)` adds the list, or replaces the one with that id. Lists show in the
  order they were first added. `attention: true` makes its cards count towards the "waiting" number.
- Card parts: one image, a title and a detail line, fields (`Field` text, `Select`, `Number` with minimum and
  maximum, `Toggle`), and actions with a label, a style (default, primary, danger) and an optional confirm text.
  An action can pass `validate: false`: its press skips the card's field checks, and its handler gets the values as
  drawn (a Dismiss beside a required Name).
- An action handler is `Func<UiValues, Task>`. `UiValues` gives the card's field values by key.
- The device replaces a list whenever its data changes. Nothing of the page is stored: the device rebuilds it from
  its data in `Start()`, which is what makes recompiles and restarts safe.
- `Ui.List` is safe to call from any thread and throws `ArgumentException` with a plain sentence for a bad page:
  duplicate card ids in a list, a bad id, too many cards, an image too large.
- Limits (defaults, the builder may tune): 20 lists per page, 200 cards per list, 8 fields and 4 actions per card,
  an image at most 1 MiB as JPEG.

**Images.** An image is turned into JPEG bytes when the card is built, kept in the host's memory under its
SHA-256, and dropped when no card of the current page uses it. The page data carries only the hash.

**Running an action.**

- The panel sends list id, card id, action id and the field values. The host looks the card up in the *current*
  page by those ids. If the card or action is gone, it answers "changed" and the panel refreshes; nothing runs.
- Required fields and number ranges are checked by the panel and again by the host.
- Handlers of one page run one at a time, off the caller's thread, and may run alongside the device's timers and
  tracks, as commands do today. They end before `Stop()` runs, like commands.
- A handler has 30 seconds. One that takes longer is answered as failed; a later press is refused until it ends.
- A handler that throws `UiRefusal("Name is taken")` shows that message on the card. Any other exception is logged
  for the definition and shown as "The action failed; see the Home Assistant log".
- An action must not hold the authoring gate (`_visualGate` in `EntityApi.cs`), so it cannot block saves or
  previews.

### 2. Device data (C#)

`SharpComponent` gains two members, usable from `Start()` on and throwing before it. The reason: on a save or a
library rebuild the new revision's `Setup()` runs while the revision it replaces is still running and writing
(`UserScriptManager.cs:301-343`), but that one has stopped before the new `Start()`. So private data has one
writer at a time, and a device never starts from a copy the old revision changed afterwards.

```csharp
protected SharpData Data { get; }          // private to this C# node of this definition
protected SharpData Shared(string name);   // shared by every component that names it, e.g. "hass-sharp-vision.people"
```

`SharpData`:

- `T? Get<T>(key)`, `Set<T>(key, value)`, `Remove(key)`, `Keys(prefix)`: JSON values.
- `GetBytes(key)`, `SetBytes(key, bytes)`: binary values, such as a JPEG crop or an embedding.
- `Update<T>(key, Func<T?, T> change)`: read, change and write one key as one step, so two devices sharing a store
  do not overwrite each other.
- `Changed(Func<Task> action)`: runs when *another* component (another C# node, in this or another definition)
  wrote to this store, so a second device can redraw its People list. Returns a handle; ends when the definition
  stops.
- `Clear()`.

Rules:

- **Where:** `.storage/hass_sharp/data/devices/<definitionId>/<nodeKey>/` and
  `.storage/hass_sharp/data/shared/<name>/`, written by C# as every other file there.
- **Names:** a shared store's name is chosen by the code and is global, so libraries prefix it with their own name.
  Any C# that names a store can read it; imported C# already runs with full access.
- **Writes** are on disk before the call returns and are never half-visible (temporary file, then rename, as
  `SharpFiles` does). A device should write when something changes, not on every frame.
- **Crossing contexts:** a value is serialised in the caller's own code; the store holds text and bytes only. The
  serialiser must not keep a replaced definition's types alive (check with the leak probe in `AGENTS.md`).
- **Limits** (defaults): a value at most 8 MiB, a store at most 512 MiB; a write over a limit throws.
- **Lifetime of private data:** kept while the definition is off, failed or recompiled. Deleted when the
  definition is deleted, or saved without that node. Deleting a C# node whose device has stored data asks first in
  the editor, through the adapter's existing `removal` hook.
- **Lifetime of shared data:** kept until someone deletes it. The Entities page gets a "Stored data" dialog that
  lists each shared store with its size and the definitions using it now, and deletes one on request. Deleting is
  refused while a running definition has the store open. This is how biometric data stays deletable after the last
  device is gone; a device can also offer its own "Delete all" action.
- The on-disk format (a file per key, or SQLite as `runs.db` uses) is the builder's choice within these rules.

This reverses a non-goal of the custom entities design ("A key-value store for user code beyond restored entity
state"); that document should say so.

### 3. `SharpImage` additions

`Crop(x, y, width, height)` and `Resize(width, height)`, each returning a new image, with the same argument
checking style as the existing drawing calls. Face alignment for recognition is not part of this; it belongs to
project 2's native code.

### 4. Between .NET, Python and the panel

One new admin-only websocket command, `hass_sharp/device_ui`, in the existing envelope pattern
(`envelope_command` in `websocket.py`, an `AuthoringService` method, a `DeviceUiRequest(string)` method on
`HassSharpManager`). Operations:

- `read {definitionId, knownRevision?}`: the pages of the definition's running revision, one per C# node that has
  one, or `unchanged` when the revision is the one the panel has. A definition that is off or failed answers that
  it is not running.
- `image {definitionId, nodeKey, hash}`: the JPEG, base64. The panel shows it as a data URL and caches it by hash.
- `act {definitionId, nodeKey, list, card, action, values}`: runs the handler. Answers done, changed, refused (with
  the message) or failed.
- `counts`: the attention count of every running definition, as `{definitionId: count}`. It reads memory only:
  no definition files, no run history and not the authoring gate, so it never waits behind a save or a compile.
- `stores` and `delete_store {name}`: for the Stored data dialog.

Page data, per C# node:

```
{ nodeKey, title, revision, attention,
  lists: [ { id, title, attention, empty,
    cards: [ { id, image: "<sha256>" | null, title, detail,
      fields:  [ { key, label, type: "text"|"select"|"number"|"toggle", value, options?, suggestions?,
                   minimum?, maximum?, required? } ],
      actions: [ { id, label, style, confirm? } ] } ] } ] }
```

No new HTTP route and no push: images travel on the admin-only websocket, and the panel polls.

- `read` every 2 seconds while the Device tab is open and the page is visible, and once right after an action.
- `counts` every 10 seconds from the Entities page and from an entity definition's editor, while the page is
  visible. This keeps the "waiting" numbers current when the Device tab is closed. The Entities list's own
  request is not used for it: that list stops polling when nothing is starting or running, and each call takes
  the authoring gate and reads every definition file.

### 5. The panel

```
[ Problems 0 ] [ Generated C# ] [ Runs ] [ Device 2 ]
--------------------------------------------------------------
Front door people

Unknown faces (2)
 [crop]  Seen 14 times        Name [__________]  [Save] [Dismiss]
 [crop]  Seen 3 times         Name [__________]  [Save] [Dismiss]

People (2)
 [crop]  Ivan   41 samples    Name [Ivan______]  [Rename] [Delete]
 [crop]  Ana    12 samples    Name [Ana_______]  [Rename] [Delete]
```

- A fourth tab, **Device**, in the editor's bottom drawer (`visual/VisualEditor.tsx`, `DrawerTab`), offered only
  by the entity adapter (`entities/adapter.ts`), so automations never show it. It lists each C# node's page under
  the node's name. Its count is the definition's attention count.
- The page is the *running* revision's, not the unsaved draft's. Off, failed or not yet saved shows one line
  saying so.
- Fields reuse the existing controls (`entities/formFields.tsx`, the widgets under `visual/widgets/`).
- What the user typed into a card stays through refreshes for as long as a card with that id exists.
- While an action runs, that card's buttons are disabled. A refusal shows under the card; a confirm text opens the
  existing `Dialog`.
- The C# device's node card on the canvas shows the attention count as a badge that opens the Device tab. The
  definition's card on the Entities list (`entities/DefinitionCard.tsx`) shows it as "2 waiting", linking to the
  editor with the tab open.
- The "Stored data" dialog is opened from the Entities page header.

### 6. Documentation

`docs/custom-entities.md` gains "A page for your device" and "Keeping data", with one worked example (a device
that shows camera snapshots to name, and keeps the names). It says that stored data is part of Home Assistant's
backups, and that face crops and embeddings are biometric data.

## Risks

- **Load-context leaks.** Handlers are closures over user types, and typed `Get<T>`/`Set<T>` touch the serialiser.
  The page, its handlers and any serialiser cache must go when the host does. The leak probe is the check.
- **Polling cost.** `read` with a known revision and `counts` must be cheap: the first runs every 2 seconds per
  open tab, the second every 10 seconds per open Entities page or editor.
- **Message size.** Images cross the websocket as base64; the 1 MiB limit keeps a message well inside Home
  Assistant's limit.
- **Two devices, one store.** `Update` and `Changed` are the whole concurrency story; a device that reads, thinks
  and writes with `Get`/`Set` can still lose an update. The docs say to use `Update`.
- **hass-sharp-vision's tests** compile `tests/Runtime/` from this repo. New helpers there are fine; renaming
  existing ones breaks them (`AGENTS.md`).

## Left to the builder

- The on-disk format of a store, and the exact limits.
- Exact C# names and overloads, the action id scheme, and how `UiValues` types its values.
- How the tab, badges and dialog look, within the panel's existing styles.
- Whether `read` answers all of a definition's pages in one call (as written) or one per node.
- The order of work and the phases.

## What the next projects need from this one

- Project 2 keeps people (name, embeddings, face crops) in `Shared("hass-sharp-vision.people")` and each device's
  unknown-face groups in `Data`; draws "Unknown faces" (attention) and "People" with `Ui.List`; cuts crops with
  `SharpImage.Crop` and `Resize`; and redraws on `Changed`.
- Project 3 adds a sensor to the People detector; it needs nothing from HassSharp beyond what devices have today.

## Verification

Tests to add, beside the suites in `AGENTS.md`:

- **Runtime** (`dotnet run --project tests/Runtime --disable-build-servers`): a test device with a page and data.
  Cover: a list replaced and removed; attention counts; an action running with values; a stale press answered as
  changed; a refusal, a throw and a timeout; actions ended before `Stop()`; data surviving a recompile and a
  restart; `Ui`, `Data` and `Shared` throwing before `Start()`; `counts` answering while a save holds the
  authoring gate; `Update` under two writers; `Changed` reaching the other device only; private data deleted with the
  definition and with the node; a shared store refused deletion while open; limits; `Crop` and `Resize`.
- **Python** (`python3 -m unittest discover -s tests/python`): the `hass_sharp/device_ui` route in the style of
  `tests/python/test_entity_authoring.py`: admin only, not ready, each operation's envelope.
- **Frontend** (`bun run test` in `frontend/`): the Device tab in the style of
  `frontend/tests/DefinitionPanel.test.tsx`: draws lists and cards, keeps typed text through a refresh, sends an
  action, shows a refusal, asks before a confirm action, shows the counts and updates them with the tab closed.
- **Packaging:** `python tests/packaging/entity_bridge_smoke.py custom_components/hass_sharp` still passes.

Live check on the local container (<http://localhost:5001>, never the `mcp__hass__*` tools):

1. Publish, build the frontend, `docker compose restart -t 60 hass`.
2. Add a definition with the docs' example device on `camera.people_clip`.
3. Name a snapshot, see it move to the named list, rename it, delete it.
4. Restart Home Assistant and recompile the library: the names are still there.
5. Add a second device sharing the store: a name given on one shows on the other within a few seconds.
6. Delete both definitions, then delete the shared store in the Stored data dialog; `data/` is empty.
7. With `custom_components.HassSharp.memory: debug`, reload several times and confirm nothing grows.
