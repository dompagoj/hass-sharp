# A picker for device pages, first and last names, and the "who" sensor: design

Status: the owner chose the approach on 2026-11-24 ("do it") and left the how to the builder. Everything not marked
as the owner's is my call.

Two repositories:

- **HassSharp** (this one, branch `person-picker`): a new part of a device page's card, a *pick field*.
- **hass-sharp-vision** (branch `names-picker-who`, from its `main` at 67ba07a): people get a first and a last
  name, the People detector's page uses the pick field, and the detector gets a "Recognised" sensor. This is
  project 3 of three (device pages, recognition, the "who" sensor), with the picker and the names added.

hass-sharp-vision's 1.1.0 is not tagged or released yet, so all of this goes into 1.1.0. The native library
(`hs_vision` 1.1.0, ABI 2) does not change: no file under `native/` and no pin changes.

## Context

- Naming an unknown face on the People detector's page is a text field. It offers the existing names as the
  browser's plain suggestion list, without pictures (`nodes/PeopleDetector.cs`, `.Field("name", …, suggestions:
  names)`). *Merge into* is a plain select of names.
- A device page (`Devices/SharpUi.cs`, `Devices/DevicePage.cs`, `frontend/src/entities/DevicePanel.tsx`) has text,
  select, number and toggle fields, and no dialog but an action's confirm.
- A person is `Person(Id, Name, …)` in the shared store `hass-sharp-vision.people` (`nodes/People.cs`); a name is
  unique without regard to case.
- The detector publishes Face seen, Person seen, Faces, People and two timestamps. Nothing an automation can read
  carries a recognised name: names are only drawn on the camera picture and shown on the page.
- A device's entities are fixed in its constructor or `Setup()` (`SharpDevice.Add` throws afterwards), so they
  cannot follow a people list that changes while the device runs.
- The detector knows in each check which person it gave a face to (`MatchAndNote` in `nodes/PeopleDetector.cs`),
  but does not follow a face from frame to frame.

## Intent

With many people named, the owner cannot remember how each was spelled: choosing a person must be a search among
the people that exist, with their pictures, or the making of a new one. People have a first and a last name. And an
automation can read who the detector sees.

## Goals

- Any C# device can put a pick field on a card: the user chooses one of the device's options, each with a
  picture, a title and a detail line, in a dialog with a search box; or, when the device allows it, fills in a small
  form for a new one.
- On the People detector's page, naming an unknown face and choosing whom to merge into use it.
- A person has a first name and an optional last name.
- Each People detector has a sensor whose state says who is in frame, and which holds a name while the person
  looks away for a moment.

## Non-goals

- An entity for each person, or a Home Assistant event for each recognition (see Approaches).
- Following a face from frame to frame.
- A picker for several choices at once, or options in groups.
- The pick field in the graph editor's own forms (settings, arguments): device pages only.
- Device pages or the sensor's people through the MCP tools.
- Splitting an existing name into first and last by guessing.

## Anti-goals

- A page whose size grows with cards × options: the options of a pick field are sent once, not once for each card.
- A press that names a face as the wrong person because the people list changed between drawing and pressing.
- A name, a picture or an embedding in a log line, or anywhere outside the local instance.
- A sensor that flickers between a name and "Nobody" while the person stands in front of the camera.
- People named before this change lost, or matched worse, after it.

## Approaches

**The picker.** One sensible way: a new field type in HassSharp's device pages, drawn by the panel as a button that
opens a dialog. The others do not fit:

- An inline combobox with thumbnails, free text for a new person: smaller, but a new person needs two fields
  (first and last name), and the owner asked for a dialog.
- A people dialog written into the panel: the panel must not know about faces; a device describes data and the
  panel draws it (the device pages design's decision).

**The sensor.** The owner chose A.

- **A. One "Recognised" sensor for each detector (chosen).** Its state names who is in frame; attributes carry the
  rest. Works with HassSharp as it is.
- **B. A binary sensor for each person.** Simpler automations, but HassSharp cannot add or remove a device's
  entities while it runs, and a rename or delete would leave entities behind.
- **C. A Home Assistant event for each recognition.** Good for "notify once on arrival", but a device cannot fire
  events (a non-goal of the device pages design).

## Design: HassSharp, the pick field

Names and signatures are proposals: the builder may improve them where the code suggests better, keeping the
behaviour.

### C# API

```csharp
// Once for the page: the options several cards choose from.
Ui.Options("people", people.Select(p => new UiOption(p.Id, p.FullName, $"{p.Samples.Count} samples")
    .Image(p.CropJpeg)));

// On a card: the user picks one of them, or fills in the form of a new one.
card.Pick("person", "Person", options: "people", value: suggested?.Id, required: true,
    add: new UiForm("New person")
        .Field("first_name", "First name", required: true)
        .Field("last_name", "Last name"));

// In the action's handler:
if (values.Picked("person") is { } id) AddTo(id);
else if (values.New("person") is { } form) Create(form["first_name"], form["last_name"]);
```

- **`Ui.Options(id, options)`** draws a set of options, or replaces the set with that id; `Ui.RemoveOptions(id)`
  removes it. A `UiOption` has an id (as a card's), a title, an optional detail line and an optional image
  (`SharpImage` or JPEG bytes, as a card's). Sets are sent to the panel once for each page, whatever the number of
  cards that use them.
- **`card.Pick(key, label, options, value, required, add, except)`** adds the field. `options` is a set's id. `value`
  is the option chosen until the user changes it. `add` allows a new one: a `UiForm` has a title and text, select,
  number and toggle fields, as a card has, at most 4. `except` is option ids this card does not offer (a person
  cannot be merged into themselves).
- **`UiValues.Picked(key)`** is the chosen option's id, or null when the user filled in the form or chose nothing;
  **`UiValues.New(key)`** is the form's values, or null. Never both.
- A pick field naming a set that does not exist when the list is drawn is an `ArgumentException`; the device draws
  the set first. Removing a set that a drawn card still uses is refused the same way.
- Limits (defaults; the builder may tune): 8 sets on a page, 500 options in a set, an option's image at most 1 MiB,
  kept in the page's image store by its hash with the cards' images and dropped when nothing shows it.
- Checked by the host on a press, as the other fields are: a chosen id must be an option of the set *as it is now*
  and not in `except`, else the press answers `changed` and the panel refreshes; a new form's values are checked
  against its fields (required, number ranges, text length); a required pick field needs one or the other.

### Page data

A page gains `options: [{ id, items: [{ id, image: "<sha256>" | null, title, detail }] }]`. A field gains the type
`"pick"` with `options: "<set id>"`, `value: "<option id>" | null`, `except?: [ids]` and
`add?: { title, fields: [...] }`. A press sends, for a pick field, `{ "picked": "<id>" }` or `{ "new": { key:
value } }` or null. An older panel bundle against a newer backend, or the reverse, is not supported beyond not
crashing: the frontend and the backend ship together.

### The panel

On the card, the field is a button: the chosen option's picture, title and detail, or "Choose…" when nothing is
chosen, or the new one's summary (its form's filled text fields, joined by a space) with a "New" mark.

```
 Person
 ┌─────────────────────────────────────────┐
 │ [pic] Ivan Horvat   41 samples       ▾  │      a press opens:
 └─────────────────────────────────────────┘

 ┌ Person ─────────────────────────────── ✕ ┐
 │ [ Search…                              ] │
 │ [pic] Ana Horvat     12 samples · seen today      │
 │ [pic] Ivan Horvat    41 samples · seen 2 days ago │   ← chosen: marked
 │ [pic] Ivan Kovač      6 samples · seen last week  │
 │──────────────────────────────────────────│
 │ + New person                              │   a press shows the form in place of the list:
 │   First name [__________] Last name [___] │
 │                        [Cancel]  [Use]    │
 └───────────────────────────────────────────┘
```

- The search box has the focus when the dialog opens; typing filters by title and detail, without regard to case.
  Arrow keys move, Enter chooses, Escape closes without a change. Choosing an option closes the dialog and fills the
  field; the card's own button (Save, Merge) then presses as before.
- "New person" (the form's title) shows the form, with what was typed in the search box put into the form's first
  text field. **Use** checks the form (required fields) and closes the dialog with the new one chosen.
- Pictures are fetched as cards' are (`image` by hash) and only for rows that are shown, so 500 options do not
  mean 500 requests when the dialog opens.
- The dialog keeps its place and its typed text through the page's 2-second refresh; an option that goes while it
  is open goes from the list; the field's choice is kept through refreshes for as long as its card and that option
  exist, as typed text is today.
- It follows the panel's existing dialog, focus and keyboard conventions (`frontend/src/components` Dialog,
  `visual/widgets/SearchSelect.tsx`), and is usable with a keyboard alone and with a screen reader (labels, roles).

### Documentation

`docs/custom-entities.md`, "A page for your device": the pick field, options sets and the new form, with the
example extended.

## Design: hass-sharp-vision

### First and last name

- `Person` has `FirstName` (required, 1 to 64 characters, trimmed) and `LastName` (optional, at most 64). Its
  **full name** is the first name, a space and the last name, or the first name alone.
- **Stored people:** a person stored with one name keeps it whole as the first name, with no last name. The store
  is read in both forms and written in the new one; the builder chooses how, without a second copy of the people.
  hass-sharp-vision 1.0.x never stored people and 1.1.0 is not released, so only development instances hold the
  old form.
- **Unique:** two people may not have the same full name without regard to case. A new person or a rename that
  would make a second one is refused: "There is already a person named Ana Horvat: choose them from the list."
- **Where which name shows:** the page, the picker and the sensor use the full name. The camera picture draws the
  first name, and with it the last name's first letter and a full stop ("Ivan K.") when another person in the list
  has the same first name.
- The People card's rename becomes two text fields, First name and Last name, and one Save.

### The page

- **Unknown faces:** the Name text field becomes a pick field "Person" over the options set of all people (full
  name; detail: the sample count and last seen, as the People card says it; picture: the crop the People card
  shows), required, with a new-person form of First name (required) and Last name. A "Might be …" suggestion is the
  field's value, so one press of Save still accepts it. Save adds the group's samples to the picked person, or makes
  the new one: by id, never by matching a typed name.
- **Merge into:** the select becomes a pick field over the same set, except the card's own person, with no new
  form. The confirm question names the person the card belongs to; naming the chosen one is not possible in a
  confirm text today and is not part of this.
- The set is drawn before the lists, and again whenever the people list changes (also from another detector).
- A person deleted or merged away between drawing and pressing: the press answers `changed` (HassSharp checks the
  id against the set), and the page is redrawn.

### The "Recognised" sensor

One `SharpSensor("recognised", "Recognised")` for each detector, added with the others in the constructor.

- **State:** the full names of the people in frame, in alphabetical order, joined by ", ": `Ivan Horvat` or
  `Ana Horvat, Ivan Horvat`. `Unknown` when no one is recognised but an unknown face is in frame. `Nobody`
  otherwise. Cut to Home Assistant's 255 characters at a whole name, with ", …" (four faces are embedded in a
  check, so only a long hold with many passers-by gets near it).
- **In frame** means given a face in a check within the last **Stay on for** seconds (the existing setting, 10 by
  default, which holds Face seen on the same way): a name holds while the person looks away, and goes that long
  after the last recognition. The same for an unknown face. A check that fails does not clear anything early.
- **An unknown face** is one that was embedded, compared with the people list, and given to no one: no match, a
  maybe, or a face matched to someone who got another face of the same frame. A face that was not compared (beyond
  the four largest, fitting too badly to embed, or too small or turned away to be usable) is not one.
- **Attributes:**
  - `people`: the full names, as a list; `first_names`: a list in the same order.
  - `unknown_faces`: the most unknown faces any one check had within the last Stay on for seconds, so it holds as
    the state does and does not flicker.
  - `last_recognised`: the full name of the person whose visit began most recently, kept after they leave;
    `last_recognised_at`: when that visit began. A **visit** begins when a person who was not in frame is given a
    face, so these change once for each arrival, not in every check.
- **Published** when the state or an attribute changes, which by the rules above is when someone arrives or
  leaves, never in every check: each publish is a row in the recorder and fires a state trigger that has no `to`.
- A rename shows at the next publish: the sensor holds person ids and writes names when it publishes. A person
  deleted or merged away goes from the state and from `last_recognised` at once.
- **Unavailable** with **Recognise faces** off, as Person seen is with Detect people off, and while the face model
  is not open (it is still downloading, or could not be opened and is retried): a face nobody could compare must
  not read as `Unknown`, which would fire a "stranger at the door" automation for the family.
- `last_recognised` and `last_recognised_at` after a restart: each detector keeps its own, by person id, in its own
  `Data`, written once for each visit. The people list's "last seen at" is not used for it: it names only the
  camera that saw the person last, and is written at most once a minute.
- Not restored (`RestoreState = false`): after a restart it is unavailable until a check has looked with the face
  model open, then `Nobody` until someone is recognised; so an automation on a change from `Nobody` does not fire
  for a restart.

## As built

- A pick field's page data names its set with `set` (the design proposed `options`, which a select field already
  uses for its list).
- **Merge into** starts empty and is not required: a required field would stop the card's Save (a rename). Merge
  with nothing chosen is refused: "Choose a person in Merge into first."
- `PeopleList.Named(name)` went: every lookup is by id. `PeopleList.Named(id, at, camera)` is new and means
  something else: naming an unknown face is a sighting of its person at the face's last sighting, written at once,
  so a person just made does not read "not seen yet".
- The People detector has `protected virtual DateTimeOffset Now`, the checks' clock, which the tests replace.
- The panel draws the pages inside an error boundary and counts a read only once its pages are drawn: found by the
  live check, where a card going froze the Device tab (a clean-up read a property through a part of the page that
  was gone).
- In the dialog the mouse moves the active row, so one row is highlighted; on the card's button the chosen one's
  title and detail are a line each.
- The unknown faces' cards are drawn in the order they were first shown, not the newest sighting first: a card
  keeps its place for as long as it is there and a new one is last. Found by the live check, where a new card at
  the head moved the others under the pointer and a Save landed on the next card. A group stores when it was
  first shown (optional; absent in stores written before, which read as the group's first sighting).

### Documentation

README: the sensor with two example automations (a notification when the state changes from `Nobody`, and a
condition on one name in `people`); first and last names; the page's picker. "It does not tell automations who is
there" goes. The privacy section says the sensor's state and attributes hold names, which every Home Assistant user
and the recorder's history see.

## Order and releases

1. HassSharp's pick field, on `person-picker`.
2. hass-sharp-vision against that branch (`-p:HassSharpRoot`, as before), on `names-picker-who`.
3. Live check in the throwaway `hass_wt` container on port 5002, never the owner's instances.
4. Nothing is pushed, tagged or released without the owner's go for that item. `HASS_SHARP_REF` in the vision
   repository's `ci.yaml` moves to the HassSharp commit once that is pushed.

## Risks

- **Load-context leaks:** a form's fields and an option's image are data only; no handler is added. The leak probe
  in `AGENTS.md` is still the check.
- **Polling cost:** the options sets ride on the page's revision; `read` with a known revision stays as cheap as
  it is.
- **hass-sharp-vision's tests** compile `tests/Runtime/` of this repository: helpers there are added, never
  renamed.
- **The lock** in the People detector (`lists`) now also guards what the sensor publishes from; a publish is not
  made under it if it calls into Python (as logging is not).

## Left to the builder

- Exact names, overloads and limits; how `UiValues` carries a pick field's value; the store's on-disk form for
  the two names.
- How the dialog and the field look, within the panel's styles.
- The order of tasks.

## Verification

- **HassSharp runtime tests:** a set drawn, replaced and removed; a pick field with and without a form; a press
  with a picked id, with a new form, with neither when required; an id gone from the set answered `changed`; an id
  in `except`; a form's required field and number range; limits; a set removed while in use; images kept and
  dropped with the options.
- **HassSharp frontend tests:** the field's three looks; the dialog opens, filters, chooses by mouse and by
  keyboard; the new form takes the search text and checks a required field; the choice and the open dialog live
  through a refresh; pictures asked for only for shown rows; the press's values.
- **HassSharp Python tests:** the `act` envelope with a pick field's values.
- **hass-sharp-vision:** a store in the old form read and written in the new; full-name uniqueness; the camera's
  label with and without a shared first name; Save by picked id and by new form; a person gone before the press;
  Merge by picked id; the sensor's state through recognised, unknown, nobody, the hold, two people, a rename, a
  delete, Recognise faces off, and the 255-character cut; no name in the log.
- **Live:** name a face by picking, by new person; merge by picking; the sensor in Home Assistant's states through
  a person entering and leaving the dummy clip; an automation on it firing once.
