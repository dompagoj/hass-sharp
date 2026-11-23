# Handoff: face recognition and the "who is this?" panel UI

Written 2026-11-23 at the end of the session that built face/person detection and Add external.
This is the starting point for a planning (brainstorming) session. Nothing below is built yet.

## What the owner wants

The owner confirmed all four parts below. They belong in the plan; how each is built is for the
planning session.

1. **A custom UI in the HassSharp panel** where the People detector device **asks the Home
   Assistant user to annotate people it does not recognise yet**. This is the owner's own ask.
2. **A stored list of people, with their data.** The owner: "we do need to store the list of
   people along with their data, that's how annotation is going to work". Annotating a face
   adds it to this list, and recognition matches new faces against it.
   - What is kept per person: a name, the face embeddings, and face crops so the UI can show
     who it is. Probably under `/config/.storage/hass_sharp/`.
   - Embeddings and face crops are biometric data. Keep them local and deletable, and never
     send them anywhere.
3. **Recognition by face embeddings: SFace**, from the OpenCV model zoo, the same zoo as the
   YuNet detector already used.
   - It runs in the same native library on tract.
   - Faces are aligned with the five landmarks YuNet already returns (`DetectedFace` in
     `Vision.cs` carries them).
   - Not checked yet: SFace's licence, whether it runs on tract without unsupported ops, and its
     speed on the arm64 container. A spike should settle these first.
4. **Unknown faces are grouped (clustered)**, so the user names each person once, not every
   frame.
   - Each group can be named (a new person, or added to an existing one) or dismissed.
   - The user can also review, merge, rename and delete people.

Also in scope: **automations get the result**, for example a "Person recognised" trigger or a
"Last recognised" sensor. The exact form waits on the question left open when the session
ended, still unanswered: **"When the camera recognises someone, what do you want to happen?"**
It decides what the device must expose. For example: an entity per person, a "Last recognised"
sensor, an event, a graph trigger, or only a log line.

## What exists today

**hass-sharp-vision** (separate repo, `/Users/dompa/dev/hass-sharp-vision`, GitHub
`dompagoj/hass-sharp-vision`, released as v1.0.0; main is at `ae2e327`):

- `native/`: Rust cdylib on tract.
  - Models: YuNet faces (`face_detection_yunet_2023mar.onnx`) and YOLOX-nano people, both
    embedded in the library.
  - C ABI: `hs_vision_abi_version`, `hs_vision_open`, `hs_vision_faces`, `hs_vision_people`,
    `hs_vision_last_error`, `hs_vision_close`.
  - Built for linux musl/glibc x64/arm64 by `build.sh`, with binaries attached to the GitHub
    release and pinned by SHA-256.
  - A new model or ABI function means a new release and a new pin in `Vision.cs`.
- `nodes/Vision.cs`:
  - `Vision.OpenAsync(Files, rid, Stopping)` downloads and opens the library;
  - `VisionDetector.Faces(image, inputSize, minScore)` and `People(...)`;
  - drawing helpers;
  - a `[NodeSetup]` "Detect faces" graph node.
- `nodes/PeopleDetector.cs`:
  - an abstract `SharpDevice` with settings Camera, Check every, Stay on for, Face score and
    Person score;
  - entities: a camera, Face seen, Person seen, Faces, People, Last face seen and Last person
    seen;
  - `Setup()` opens the detector, `Start()` opens `SharpVideo` and checks a frame every
    *Check every* seconds, and `Stop()` closes both.
- `hass_sharp.toml` offers both files and `nodes/front_door_people.json` to Add external.

**HassSharp** (this repo; detection support and Add external are merged into main):

- **Devices** (`HassSharp/Devices/`): `SharpDevice` / `SharpComponent`.
  - Lifecycle: `Setup()` (60 s, download time not counted), `Start()` and `Stop()`.
  - Members: `Every`, `Track`, `Http`, `Files`, `State`, `CallService`, `Services`, `Log`, the
    `Stopping` token, and `[Setting]` properties.
  - `SharpFiles` (`HassSharp/Devices/SharpFiles.cs`) is only a checksum-keyed download cache
    under `.storage/hass_sharp/files/`.
- **Gaps for this feature.** A quick look found none of the following; confirm before
  planning:
  - a place for a device to keep its own changing data;
  - a way for a device to show UI in the panel or to ask the user anything;
  - a way for a device to fire Home Assistant events.

  These are probably the main HassSharp additions this feature needs, much as `[NodeSetup]`
  and `SharpFiles` were for detection.
- **Panel** (`frontend/`, SolidJS): routes in `frontend/src/App.tsx` are `/automations`,
  `/visual/:id`, `/libraries`, `/entities` and `/entities/:id` (`EntityDefinitionView`).
  - The backend talks over websocket routes registered in `websocket.py`
    (`envelope_command` pattern). The .NET side answers through the `*Api.cs` classes.
- **Add external**: a GitHub repo's `hass_sharp.toml` lists node libraries and entity
  definitions, and the panel imports them (`docs/node-libraries.md`, "Import from GitHub").
  This is how hass-sharp-vision updates reach a Home Assistant instance.
- Design docs to read: `docs/superpowers/specs/2026-11-22-face-detection-external-import-design.md`
  and `docs/superpowers/specs/2026-11-17-custom-entities-design.md`.
  - The ledgers in `.superpowers/sdd/2026-11-22-vision/` and `.superpowers/sdd/2026-11-22-external-import/`
    hold every ruling made.

**Local instance:** <http://localhost:5001> (the `hass` container; see `AGENTS.md`).

- It has People clip (`camera.people_clip`, a looping clip with people in it) and Front door
  people watching it, both enabled.
- `testcamera` runs Detect faces.
- Good for live checks. It has no annotated faces yet.

## Loose ends from the last session (not part of this feature)

- The `face-detection-and-external-import` branch is merged into main (fast-forward) and still
  exists locally and on GitHub. The two ledgers above are kept for their rulings.
- Parked Plan B nits:
  - N1: `docs/node-libraries.md` says a value that no longer fits is "refused, not dropped",
    but one for a removed setting is dropped with a warning;
  - N2: the failed-entity line can read "not imported — Not imported: 'logic'";
  - the live fix's review said "the libraries it uses" in the docs is narrower than the truth.
- hass-sharp CI on main:
  - hassfest: "Using component ffmpeg but it's not in 'dependencies' or 'after_dependencies'";
  - flaky 5 s frontend connection-index timeouts (they passed on the last run).
- The AGENTS.md container test recipe should also copy `docs/` (the docs tests need it).
- Local tag `backup/before-vision-split` is kept until the owner says otherwise. Eight local
  branches have no upstream; leave them alone.

## Working rules the owner set (still in force)

- Never use the `mcp__hass__*` tools: they reach the owner's production Home Assistant, not
  the local container.
- Code changes go to Opus subagents. Sonnet is only for simple checks and small re-reviews.
- No push, tag or release without the owner's explicit go for that specific item.
  - Commit messages: one plain sentence, no trailer.
- `git diff` here is an external side-by-side tool, so use `git diff --no-ext-diff` (or `git -c
  diff.external=`) for anything a tool or agent reads.
- Restart with `docker compose restart -t 60 hass`. A rebuilt frontend needs that restart,
  not just a hard refresh.
