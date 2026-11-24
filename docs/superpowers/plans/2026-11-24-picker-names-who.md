# Pick field, first and last names, Recognised sensor: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan
> task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A device page's card can hold a pick field (a dialog over the device's options, with pictures, or a form
for a new one); people have a first and a last name; the People detector's page uses the pick field and the
detector has a "Recognised" sensor.

**Architecture:** HassSharp gains one field type end to end: C# API, page data, the host's check of a press, the
panel's control. hass-sharp-vision then changes only its node files (`nodes/People.cs`, `nodes/PeopleDetector.cs`)
and their tests; its native library does not change.

**Tech stack:** C# on .NET 11, SolidJS and TypeScript (Vitest, Bun), Python for Home Assistant's side.

**Spec:** `docs/superpowers/specs/2026-11-24-picker-names-who-design.md` (this repository). Read it with this plan.

**Repositories:**
- A: HassSharp, worktree `/Users/dompa/dev/HassSharp/.claude/worktrees/person-picker`, branch `person-picker`.
- B: hass-sharp-vision, `/Users/dompa/dev/hass-sharp-vision`, branch `names-picker-who`.

## Global Constraints

- Never push, tag, merge or release. Never commit on `main`. Never use an `mcp__hass__*` tool. Never touch the
  containers `hass`, `hass-dummy-camera` or `hass_wt_stale_entity_nodes`, or the main checkout `/Users/dompa/dev/HassSharp`
  outside `.claude/worktrees/person-picker`.
- Commit messages are one plain sentence saying what the code now does: no prefix, no trailer.
- Read diffs with `git --no-pager diff --no-ext-diff`. Run git as plain, separate commands.
- A lock in C# is a `System.Threading.Lock` field taken with `lock`, never across an `await`.
- `tests/Runtime/` of A is compiled by B's tests as `HassSharp.RuntimeTests`: add helpers there, never rename one;
  leave `RuntimeTestAccess.cs`'s `InternalsVisibleTo`.
- Messages a user can see are plain sentences.
- Face crops, embeddings and names are personal data: never in a log line, never sent anywhere, never committed.
- B: nothing under `native/` changes, nor the pins in `nodes/Vision.cs` and `native/SHA256SUMS`. hs_vision stays
  1.1.0, ABI 2. Limits stay: 200 people, 20 named and 20 learned samples a person, 50 unknown groups, 4 faces
  embedded a check.
- Tests come first: write the failing test, see it fail, implement, see it pass.
- Limits of the pick field: 8 option sets on a page, 500 options in a set, 4 fields in a new form; ids as a card's
  (`DevicePage.CheckId`), keys as a field's.

## Review Focus

- The option a card has chosen goes (its person deleted on another detector) while its dialog is open or before
  Save: the dialog's list loses it, the field shows nothing chosen, and a press that still names it answers
  `changed`, never the wrong person. (Tasks 1, 2, 3.)
- 200 people and 50 unknown cards: the page data holds the 200 options once, the dialog shows at most 50 rows and
  asks only for their pictures. (Tasks 1, 2.)
- A keyboard only: the field opens with Enter or Space, the search has the focus, arrows and Enter choose, Escape
  closes the dialog and nothing behind it, and the focus returns to the field. (Task 2.)
- A people store in the old one-name form, with a name that has spaces: every person is there afterwards with that
  name whole as the first name, their samples untouched, also when the first write after reading is another
  detector's. (Task 3.)
- The face model not open (downloading, or failed to open): the Recognised sensor is unavailable, never `Unknown`;
  and with the person renamed, merged or deleted on another detector while in frame here, the state follows
  without a restart. (Task 4.)

---

### Task 1 (A): the pick field in C#

**Files:**
- Modify: `custom_components/hass_sharp/HassSharp/Devices/SharpUi.cs`, `DevicePage.cs`, `DefinitionHost.cs` (where
  `UiAct` answers a press), `DeviceUiApi.cs` only if it must.
- Modify: `docs/custom-entities.md` ("A page for your device"), `docs/backend/` page-data reference if one lists the
  field types.
- Test: `tests/Runtime/` (the device page tests' file and suite flag; find them), `tests/python/` (the
  `hass_sharp/device_ui` route's tests).

**Interfaces — produces (tasks 2 and 3 rely on these exactly):**

```csharp
public sealed class UiOption(string id, string title, string? detail = null)
{
    public UiOption Image(SharpImage image);   // as UiCard.Image
    public UiOption Image(byte[] jpeg);
}
public sealed class UiForm(string title)       // Field, Select, Number, Toggle with UiCard's signatures; at most 4
public void SharpUi.Options(string id, IEnumerable<UiOption> options);   // draws or replaces a set
public bool SharpUi.RemoveOptions(string id);
public UiCard UiCard.Pick(string key, string label, string options, string? value = null, bool required = false,
    UiForm? add = null, IEnumerable<string>? except = null);
public string? UiValues.Picked(string key);    // KeyNotFoundException when the card has no pick field with the key
public UiValues? UiValues.New(string key);     // the form's values; never non-null together with Picked
```

Page data (`DevicePage.Snapshot`): the page gains `"options": [{ "id", "items": [{ "id", "image": sha256|null,
"title", "detail" }] }]` (always present, empty when none). A pick field is
`{ "key", "label", "type": "pick", "value": id|null, "set": "<set id>", "except"?: [ids], "add"?: { "title",
"fields": [field…] }, "required" }`. A press sends for it `{ "picked": "<id>" }`, `{ "new": { key: value } }` or
`null`; a key the press does not send reads as drawn.

**Behaviour the tests pin:**
- `Options` checks the set whole before drawing it (ids, texts, images as a card's, no id twice, at most 500, at
  most 8 sets) and throws `ArgumentException` leaving the page as it was. Replacing a set never throws because a
  drawn card's `value` or `except` names an option that is gone: the page data then gives that field `value: null`.
- `SharpUi.List` throws `ArgumentException` for a pick field whose set is not drawn, whose `value` is not an
  option of the set as it is then or is in `except`, whose form has more than 4 fields or two with one key, or
  whose key is another field's.
- `RemoveOptions` throws `ArgumentException` while a drawn card has a pick field on the set.
- Option images live in the page's image store with the cards' (`DevicePage.Image(hash)` gives them), each once,
  dropped when no card and no option shows them. `Changed` is raised when a set's data changes, so the page's
  revision changes.
- A validating press: a picked id that is not an option of the set **as it is when the press is found**, or is in
  `except`, answers `changed` (as a card that is gone does), and the handler does not run. A `new` when the field
  has no form, both `picked` and `new`, or a value of the wrong kind is refused with a sentence. A new form's
  values are checked as a card's (required, range, length, option), the refusal naming the form's field. A required
  pick field with neither is refused: "{Label} is required."
- A press that does not validate: `Picked` is the drawn value when it is still an option, else null; `New` is null.
- `UiValues`' indexer, `Number` and `Toggle` keep throwing `KeyNotFoundException` for a pick field's key.

- [ ] **Step 1:** Write the failing runtime tests for every bullet above, in the device page tests' style.
- [ ] **Step 2:** Run that suite; see them fail for the missing API.
- [ ] **Step 3:** Implement. `UiPress` takes, under the page's lock in `Find`, the option ids of the sets its
  card's pick fields use; a `changed` outcome leaves `UiPress.Values` as its own internal exception or result that
  `DefinitionHost.UiAct` answers as it answers a missing card.
- [ ] **Step 4:** Python: a test that `act` with a pick field's value (`{"picked": …}` and `{"new": {…}}`) reaches
  the backend unchanged; change the route only if it refuses a nested value.
- [ ] **Step 5:** Docs: the pick field, option sets and the new form in `docs/custom-entities.md`, extending its
  example; the page-data reference.
- [ ] **Step 6:** Run `dotnet run --project tests/Runtime --disable-build-servers` whole,
  `python3 -m unittest discover -s tests/python`, and `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests`.
- [ ] **Step 7:** Commit.

### Task 2 (A): the pick field in the panel

**Files:**
- Modify: `custom_components/hass_sharp/frontend/src/entities/deviceUi.ts`, `DevicePanel.tsx`, the styles file the
  device cards' classes live in.
- Create: `custom_components/hass_sharp/frontend/src/entities/PickField.tsx`.
- Test: `custom_components/hass_sharp/frontend/tests/` (beside the Device tab's tests; find them).
- Modify: `docs/custom-entities.md` only where it describes what the user sees.

**Interfaces — consumes:** task 1's page data and press values, exactly as written there.

**Produces:** `PickField` (the control on the card and its dialog); in `deviceUi.ts` the types `DeviceOption`,
`DeviceOptionSet`, `DevicePageData.options` (an older backend's missing `options` reads as none), and
`DeviceField` with `type: 'pick'`, `set`, `except`, `add`.

**Behaviour the tests pin:**
- The field on the card is a button with the field's label: the chosen option's picture, title and detail;
  "Choose…" when nothing is chosen; a new one's filled text fields joined by a space with a "New" mark. A required
  one is marked as the card's other fields are, and a press of a validating action with nothing chosen marks it
  and moves the focus to it.
- Enter, Space or a click opens the dialog (the panel's `Dialog`), titled with the field's label, its search box
  focused. Typing filters by title and detail without regard to case. Options in `except` are not listed. At most
  50 matching rows are rendered, with a line "N more: type to narrow" when there are more. Arrow keys move, Enter
  chooses the active row, a click chooses; choosing closes the dialog, fills the field, and returns the focus to
  the field's button. Escape or Cancel closes with no change and closes nothing behind the dialog.
- With `add`: a row "+ {form title}" under the list (and reachable by the arrow keys) shows the form in place of
  the list, its first text field holding what was typed in the search box. **Use** checks the form's required
  fields and number ranges as a card's and closes with the new one chosen; **Back** returns to the list.
- Pictures: the chosen option's picture for each pick field on the page, and the rendered rows' while a dialog is
  open, are asked for with `image` by hash, each once; the existing image cache keeps them while a card or a shown
  option needs them.
- The choice, the open dialog, its typed text and its scroll survive the 2-second refresh. An option that goes
  from the set goes from the open list; a chosen option that goes leaves the field with nothing chosen. A press
  that answers `done` clears the choice as it clears typed text.
- A press sends `{ picked: id }`, `{ new: { key: value } }` or `null` for the field; an action that does not
  validate sends nothing, as today.
- Roles and labels: the list is a listbox with options, the search a combobox as `visual/widgets/SearchSelect.tsx`
  has it; every picture has an empty `alt`.

- [ ] **Step 1:** Write the failing tests for the bullets above.
- [ ] **Step 2:** Run them (`bun run test` in `frontend/`, the file alone); see them fail.
- [ ] **Step 3:** Implement `PickField.tsx`, the types, and the wiring in `DevicePanel.tsx` (`CardField`,
  `sentValue`, `emptyValue`, `keepImages`).
- [ ] **Step 4:** `bun run test` whole, `bun run build`, and the type check the project uses.
- [ ] **Step 5:** Commit.

### Task 3 (B): first and last names, and the page's pick fields

**Files:**
- Modify: `nodes/People.cs`, `nodes/PeopleDetector.cs`, `README.md`.
- Test: `tests/Vision.Tests/PeopleTests.cs`, `PeopleDetectorTests.cs`, and the manifest tests if the node's
  description changes.

**Interfaces — consumes:** task 1's C# API, from the HassSharp checkout the dispatch names
(`-p:HassSharpRoot=…`, `HASS_SHARP_ROOT=…`).

**Produces (task 4 relies on these):**

```csharp
public sealed record Person(string Id, string FirstName, string? LastName, DateTimeOffset Added,
    IReadOnlyList<FaceSample> Samples, DateTimeOffset? LastSeen, string? LastSeenAt, DateTimeOffset? LearnedChanged)
{ public string FullName { get; } }             // "First Last", or "First"
public Person PeopleList.Add(string firstName, string? lastName, IEnumerable<NewSample> samples);
public void PeopleList.Rename(string personId, string firstName, string? lastName);
public static string PeopleList.Label(Person person, IReadOnlyList<Person> everyone);  // the camera's label
```

`Person.Name` goes: every reader says which name it means.

**Behaviour the tests pin:**
- Names: first name trimmed, 1 to 64 characters; last name trimmed, at most 64, empty or null is none. Two people
  may not have one full name without regard to case; the refusal is "There is already a person named {full name}:
  choose them from the list." A rename to the person's own name in another case is theirs.
- The store: a store written in the old form (one name a person) is read with that name whole as the first name;
  the next write of the names is in the new form; a store in the new form is read back the same. Uniqueness across
  two lists on one store holds as it does today (one `Update` of the names).
- `Label`: the first name; with another person in the list having the same first name without regard to case, the
  first name, a space, the last name's first letter and a full stop; the first name alone when the person has no
  last name.
- The page: the option set `people` (id the person's, title the full name, detail and picture as the People card
  has them) is drawn before the lists and again whenever the people list changes, also from another detector.
  Unknown card: a required pick field `person`, label "Person", its value the suggested person's id, with the form
  "New person" of `first_name` "First name" (required) and `last_name` "Last name"; Save adds the group's samples
  to the picked person by id, or makes the new person. People card: text fields `first_name` and `last_name` and
  Save; a pick field `into`, label "Merge into", over the same set except the card's person, with no form, and
  Merge by id. A card whose person is the only one has no Merge.
- Refusals stay sentences (`UiRefusal`): the group gone, the person full, the duplicate name.
- Names on the camera use `Label`.
- README: first and last names, the picker in "naming a face" and "merging", with what the user presses.

- [ ] **Step 1:** Failing tests in `--people` for names, the store's two forms and `Label`.
- [ ] **Step 2:** Implement in `People.cs`; pass.
- [ ] **Step 3:** Failing tests in `--people-detector` for the option set, both cards' fields and presses (picked,
  new, a person gone before the press answering `changed`, merge by id), and the camera's labels.
- [ ] **Step 4:** Implement in `PeopleDetector.cs` (the presses stay under the `lists` lock as they are); pass.
- [ ] **Step 5:** README.
- [ ] **Step 6:** The four suites with `HS_VISION_REQUIRE_LIBRARY=1`, and `python3 -m unittest discover -s tests`
  with `HASS_SHARP_ROOT`.
- [ ] **Step 7:** Commit.

### Task 4 (B): the Recognised sensor

**Files:**
- Modify: `nodes/PeopleDetector.cs`, `README.md`, `nodes/front_door_people.json` and the manifest's descriptions
  only if they list the entities.
- Test: `tests/Vision.Tests/PeopleDetectorTests.cs`.

**Interfaces — consumes:** task 3's `Person.FullName`, `FirstName`.

**Produces:** the entity `SharpSensor("recognised", "Recognised")`, icon `mdi:account-eye`, not restored; its
state and attributes exactly as the design's "The Recognised sensor" section says.

**Behaviour the tests pin (a fake clock and the fake recogniser, no sleeps as proof):**
- `Nobody` at start; a recognised person's full name after the check that gives them a face; two people in
  alphabetical order joined by ", "; `Unknown` with only an unknown face; the name stays through checks without the
  face for Stay on for seconds and goes after; a failed check clears nothing early.
- What counts as an unknown face: embedded, compared and given to no one (no match, a maybe, a second face of a
  person who got another face of the frame). A face not compared does not count.
- Attributes `people`, `first_names`, `unknown_faces` (the most in one check within the hold), `last_recognised`,
  `last_recognised_at` (the start of the visit).
- Published only on a change: a person standing in frame for 20 checks is one publish; count the publishes.
- A rename shows in the state; a person deleted or merged away, here or on another detector, goes from the state
  and `last_recognised` (the merged one's visit does not become the target's).
- Unavailable with Recognise faces off, and while the recogniser is not open; available once it opens.
- `last_recognised` and its time survive a restart of the definition, from the detector's own `Data`, written once
  for each visit; a stored id that is no one's reads as none.
- The state is cut at 255 characters at a whole name with ", …".
- No name in a log line (`NoNameIsLogged`).
- The sensor's values are computed under the `lists` lock with the matching; setting the entity's `Value`,
  `Attributes` and `Available` happens after the lock is let go.

- [ ] **Step 1:** Failing tests for the bullets above.
- [ ] **Step 2:** Implement; `MatchAndNote` gives back what the sensor needs (who was given a face, how many
  unknown faces) beside the names it gives today.
- [ ] **Step 3:** README: the sensor, its attributes, two automations (a notification on a change from `Nobody`;
  a condition on a name in `people`), the privacy section's sentence on names in states and history; "It does not
  tell automations who is there" goes.
- [ ] **Step 4:** The four suites three times over with `HS_VISION_REQUIRE_LIBRARY=1`, and the Python tests.
- [ ] **Step 5:** Commit.

### Task 5: live check

In the throwaway `hass_wt` container on port 5002 only, built from A's worktree and B's node files, with evidence
files: the pick field's dialog by mouse and by keyboard; naming an unknown face by picking and by a new person with
both names; merge by picking; a person deleted on one detector while the other's dialog is open; the sensor in
Home Assistant's states through an arrival and a leaving on the dummy clip, with the number of state changes; an
automation on it firing once; the people stored by 1.1.0's earlier form read with their names; no name in the log.
