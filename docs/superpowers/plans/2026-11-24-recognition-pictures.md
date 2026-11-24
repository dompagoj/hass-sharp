# Recognition pictures Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan
> task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A C# device can keep files in Home Assistant's media folder, and the People detector keeps the face of each
arrival there and names it on the Recognised sensor.

**Architecture:** HassSharp gives every C# node a private folder under Home Assistant's media folder through
`SharpComponent.Media`, beside `Data`; Python hands Home Assistant's `media_dirs` to .NET. hass-sharp-vision's People
detector saves an arrival's face crop there before it publishes the state that names it, and keeps an index of its
pictures in its own data to prune them and to drop a removed person's.

**Tech Stack:** C# (.NET 11), Python (Home Assistant integration, Python.NET), the repositories' own test runners.

**Spec:** `docs/superpowers/specs/2026-11-24-recognition-pictures-design.md` (this branch). Read it first; it decides
the behaviour, this plan decides the order and the interfaces.

## Global Constraints

- Two repositories, two worktrees: HassSharp `/Users/dompa/dev/HassSharp/.claude/worktrees/recognition-pictures` and
  hass-sharp-vision
  `/private/tmp/claude-501/-Users-dompa-dev-HassSharp/92c2fcca-d7d3-4576-819b-a3af01129b90/scratchpad/vision-pictures`,
  both on a branch `recognition-pictures`. Nothing is pushed, tagged or released.
- Commit messages are one plain sentence, no prefix, no trailer.
- Locks are `System.Threading.Lock` fields taken with `lock`, never across an `await` (`AGENTS.md`).
- `tests/Runtime/` is compiled by hass-sharp-vision as `HassSharp.RuntimeTests`: add helpers, never rename one; leave
  `RuntimeTestAccess.cs` alone.
- Face crops, embeddings and names are biometric data: never logged, never sent anywhere, no name or person id in a
  file name.
- A phrase of `llm.py` or of the docs may be pinned in `tests/python/test_mcp_adapter.py`; `tests/python` is run in the
  `hass` container before the task is handed back (the recipe is in `AGENTS.md`), since 88 of its tests skip on the Mac.
- A file is at most 10 MiB; a node keeps at most 200 files; a name is 1 to 100 of `A–Z a–z 0–9 . _ -`, not starting
  with a dot, with an extension.
- The detector keeps at most 20 pictures and none older than 24 hours.

## Review Focus

Each line has its test in the task that owns the code.

1. The media folder Home Assistant names does not exist on disk yet (a core install's `config/media`): the first `Save`
   makes it. (Task 1)
2. The media folder's entry is not called `local`, or there are several: the served path uses the entry's own name, and
   `local` is preferred. (Task 1)
3. The media folder cannot be written (read-only, full): `Save` throws, and the detector goes on recognising, publishes
   the arrival with a null picture and logs the failure once, without a name. (Tasks 1 and 2)
4. Two saves of one name at once, and a `Save` during a `Clear`: each file on disk is whole, and the file count never
   passes the limit. (Task 1)
5. After a restart of Home Assistant, `last_recognised_picture` comes back with `last_recognised` when its file is still
   there, and is null when it is not. (Task 2)

---

### Task 1: `SharpComponent.Media` in HassSharp

**Files:**
- Create: `custom_components/hass_sharp/HassSharp/Devices/SharpMedia.cs`
- Modify: `custom_components/hass_sharp/HassSharp/Devices/SharpComponent.cs` (the `Media` member beside `Data`, and the
  host interface it reads from), the host that implements that interface (`DefinitionHost.cs`), wherever private data
  is deleted with a definition or a node (`DataStores.cs` and its callers), `custom_components/hass_sharp/__init__.py`
  or `entity_bridge.py` (the media folders reach .NET)
- Test: `tests/Runtime/SharpMediaTests.cs` (new, registered as the other runtime suites are),
  `tests/python/test_entity_bridge.py`
- Docs: `docs/custom-entities.md`

**Interfaces:**
- Produces, for Task 2:
  - `protected SharpMedia Media` on `SharpComponent`
  - `public sealed class SharpMedia` with `string Save(string name, byte[] content)`, `string? Path(string name)`,
    `bool Remove(string name)`, `IReadOnlyList<SharpMediaFile> Files()`, `void Clear()`
  - `public sealed record SharpMediaFile(string Name, string Path, long Size, DateTimeOffset Written)`
  - A way for a runtime test to give the host a media folder (a temporary directory and its entry name), which Task 2's
    tests use through the existing rig; say in the hand-back what it is.

- [ ] **Step 1:** Read how `Data` reaches a component (`SharpComponent.Started`, the host interface, `DataStores`), how
  private data is deleted with a definition and with a node, and how `_allowed_folders` hands folders to .NET.
- [ ] **Step 2:** Write the failing runtime tests: save gives `/media/<entry>/hass_sharp/<definitionId>/<nodeKey>/<name>`
  and the bytes are on disk; a second save of the name replaces it; `Path`, `Files` (newest first), `Remove`, `Clear`;
  bad names, an 11 MiB file and a 201st file refused in a sentence; `Media` before `Start()` and after stop; kept across
  a recompile; deleted with the definition and with the node; no media folder; and Review Focus 1, 2, 3 (a folder made
  read-only) and 4.
- [ ] **Step 3:** Run them and see each fail for the reason it tests.
- [ ] **Step 4:** Implement `SharpMedia` and the wiring.
- [ ] **Step 5:** The Python side hands `hass.config.media_dirs` to .NET; a test in `tests/python/test_entity_bridge.py`.
- [ ] **Step 6:** `docs/custom-entities.md`: a section "Files in Home Assistant's media folder", with the notification
  example and who can see the files.
- [ ] **Step 7:** Run every suite of `AGENTS.md` (the runtime suite whole, the editor tests, both Python suites, the
  frontend tests if a frontend file changed) and `tests/python` in the `hass` container; commit.

### Task 2: the People detector keeps and names its pictures

**Files:**
- Modify: `nodes/PeopleDetector.cs`, `nodes/front_door_people.json` only if a setting must be listed there,
  `README.md`, `.github/workflows/ci.yaml` (`HASS_SHARP_REF`, to the commit the controller names once Task 1 is pushed;
  leave it until then and say so)
- Test: `tests/Vision.Tests/PeopleDetectorTests.cs`, and the manifest test's count of settings

**Interfaces:**
- Consumes: Task 1's `Media`, `SharpMedia`, `SharpMediaFile` and its test media folder.
- Produces: the setting `KeepPictures` ("Keep recognition pictures", off by default) and Recognised's attributes
  `pictures`, `last_recognised_picture`, `unknown_picture`.

- [ ] **Step 1:** Read how Recognised is worked out and published (`who.Attributes`, the visit kept in the detector's
  data, the hold), how the page's crops are cut, and how the detector hears of a person deleted or merged.
- [ ] **Step 2:** Write the failing tests of the spec's Verification list for the detector, and Review Focus 3 and 5.
- [ ] **Step 3:** Run them and see each fail for the reason it tests.
- [ ] **Step 4:** Implement.
- [ ] **Step 5:** README: the setting, the attributes, who can see the pictures, the automation with the notification,
  and the HassSharp release it needs (the controller gives the number).
- [ ] **Step 6:** Run the four C# suites against the HassSharp worktree and the Python suite; commit.
