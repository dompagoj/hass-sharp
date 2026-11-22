# Face and person detection, and "Add external" import

## Intent

Custom entities and the graph camera work: the local `testcamera` definition runs front-door camera → Camera frame →
Draw text → Draw rectangle → camera entity. The custom-entities design named in-process inference as the next step
(`2026-11-17-custom-entities-design.md`). The owner wants, built in this order:

- **A. Vision: face and person detection.**
  - A **People detector** device (a C# device class in a node library) that watches a camera on an interval, even
    when nobody is looking at it, and gives Home Assistant entities: an annotated camera, Face seen, Faces, Person
    seen, People, and when each was last seen.
  - A **Detect faces** library node that takes a `SharpImage` and returns it with each face's box and five landmarks
    drawn, for graph cameras (Camera frame → Detect faces → Image).
  - Both run on one native library (Rust + tract, YuNet for faces, YOLOX-nano for people) that lives in its own
    repository, `https://github.com/dompagoj/hass-sharp-vision`, is published there as GitHub release assets, and is
    downloaded when a definition sets up. It is never shipped in the integration (the release is capped at 100 MiB)
    and never committed to any git history.
  - The node code (`Vision.cs`, `PeopleDetector.cs`), an example definition and the manifest live in
    hass-sharp-vision too, published as any third-party author would publish node libraries. HassSharp keeps the
    platform features any author can use: `[NodeSetup]`, `DrawCircle` and "Add external".
- **B. "Add external".** Paste a GitHub repository URL. HassSharp reads `hass_sharp.toml` at the repo's root, which
  lists the repo's node libraries and entity definitions, and a dialog lets you pick what to import. The vision
  libraries in hass-sharp-vision are the first thing imported this way.

Then the Detect faces node is wired into `testcamera`, and a People detector is set up on the local instance (port
5001).

## Goals

- A definition can add a People detector for a camera and get, without anyone watching the camera, whether a face or
  a person is in view, how many, and when one was last seen, as Home Assistant entities automations can use.
- A graph camera can place Detect faces between Camera frame and its Image and see boxes plus eyes, nose tip and
  mouth corners at live frame rates.
- Library nodes can do one-time work when their definition starts (download, load native code) and clean it up when
  it stops (`[NodeSetup]`).
- Any public GitHub repo with a `hass_sharp.toml` can offer node libraries and entity definitions for import.

## Non-goals (this round)

Face *recognition* (who it is), a people database, custom UI for devices in the panel, person data in automations
(all the next project, below); dense face mesh; GPU; a generic inference runtime or API in HassSharp; NuGet packages
for user code; tracking or updating imports; hosts other than GitHub; native code in the integration release;
`[NodeSetup]` in automations; a Detect people node; finding people smaller than ~20 px in the network input.

## Anti-goals

- An import that half-applies (some libraries saved, others not).
- A library save or definition restart that leaks a native detector.
- A detector that can silently run a different model than its decoder expects.
- Face seen / Person seen flickering off between two checks while someone stands there.

## Decisions (owner's)

- Faces: box + 5 key points (YuNet). People: boxes (YOLOX-nano), so someone seen from behind or the side counts.
- Packaging: a **People detector device** for entities **and** a **Detect faces node** for graph pictures.
- Inference: **our own Rust library on tract** (Sonos's pure-Rust ONNX engine), specific to these two models, not a
  generic runtime.
- Native binaries are **GitHub Release assets of hass-sharp-vision**, built by that repository's own CI from its
  tagged source; never committed to any git history. Builds run in Docker (no Zig, nothing installed on the host).
- Import is a **one-time copy**: no origin tracking, no update action.
- **No pushes** to either repository, and no tag or release, without the owner's go. The live import from
  hass-sharp-vision's URL waits for that go, not for a hass-sharp merge.
- hass-sharp-vision is **MIT**, the same holder as HassSharp ("Copyright (c) 2026 Domagoj Zivanovic").
- **2026-11-22, changed by the owner:** the vision work moves to its own repository, `dompagoj/hass-sharp-vision`
  (the Rust crate with its history, `Vision.cs`, `PeopleDetector.cs`, the example definition and `hass_sharp.toml`),
  and its binaries become that repository's release assets. Until then they were to be built and committed under
  `nodes/vision/native/bin/` in this repo, but every rebuild would have added about 50–85 MiB to hass-sharp's git
  history. The full list is `.superpowers/sdd/2026-11-22-vision/split-decisions.md`. The same day the owner dropped
  NNEF conversion and `panic = "abort"` from the size work.

## Approaches considered

Inference:
- **Rust + tract, model-specific (chosen).** Pure Rust, builds for musl in minutes in Docker, a tiny C ABI we own.
  Both spikes passed (below).
- libfacedetection (YuNet's C++ library): faces only. ONNX Runtime built for musl: its edge is any model, which we
  don't need, and its musl build is the riskiest. Pure C# networks: bespoke and slower. All dropped.

Packaging (owner chose both A and B):
- A. Node between Camera frame and the camera: composable with any picture, but runs only while a camera's frame is
  being made, so it cannot drive "seen" entities on its own.
- B. C# device: runs its own loop on an interval with existing `Setup`/`Every`/`Stop`, and carries entities.
- C. Graph-only (node returning image + count, entities built with an interval trigger): more wiring per camera.

Person model (spike 2): YOLOX-nano (chosen: fastest, no false positive at 0.45, one output tensor, any input that is
a multiple of 32 including non-square); YOLOX-tiny (more accurate, 3× slower, 20 MB); NanoDet-Plus-m (missed a person
at 416); PP-PicoDet-S (fire hydrant as a person at 0.48). Ultralytics YOLOv5/v8/11 (AGPL), YOLO-NAS (non-commercial)
and YOLOv7 (GPL) were excluded by licence.

## Spike results (2026-11-22, throwaway, `tract-onnx` 0.23.10)

Both models:
- Load with `tract_onnx::onnx().with_ignore_output_shapes(true).with_ignore_value_info(true)`, then fix the input
  fact; without the flags, any size other than the file's stored one fails in shape inference. Any size that is a
  multiple of 32 works, non-square too.
- tract 0.23 API: `into_runnable()` returns an `Arc`; outputs via `TValue.try_as_plain_ram()?.as_slice::<f32>()`;
  `select_outputs_by_name`.
- Identical detections natively (M2 Pro) and in the `hass` container (Alpine 3.24 aarch64 musl, colima, ~1.7×
  slower).

YuNet (`face_detection_yunet_2023mar.onnx`):
- Input 1×3×H×W **BGR** float 0–255, no mean/scale, zero-padded bottom/right. Outputs by name `cls_8/16/32`,
  `obj_8/16/32`, `bbox_8/16/32`, `kps_8/16/32`. Score = sqrt(clamp(cls)·clamp(obj)); OpenCV `FaceDetectorYN` decode;
  NMS IoU 0.3.
- 8/8 faces on a 1080×720 group photo at 640×480, 7/8 at 320×240; no false positives.
- Single thread, median / p90 ms: Mac 4.2 / 4.7 (320×240), 16.4 / 18.6 (640×480); container 7.6 / 8.5 and
  29.0 / 31.7.
- `https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx`, 232,589
  bytes, SHA-256 `8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4`, MIT (Shiqi Yu).

YOLOX-nano (`yolox_nano.onnx`):
- Input 1×3×H×W **BGR** float 0–255, no mean/std, letterboxed top-left with pad value **114**. One output
  `[1, N, 85]`: per anchor `(dx, dy, lw, lh, obj, 80 class scores)`; strides 8/16/32, grid index with no +0.5 offset:
  `cx = (dx + gx)·s`, `w = exp(lw)·s`; score = obj · cls[0] (person). Threshold 0.45, NMS IoU 0.45.
- People seen from behind: 4/4 on a street photo; 8/8 on the group photo at 416×256; none smaller than ~15–20 px in
  the network input.
- Single thread, inference + decode, median / p90 ms, container: 22.0 / 23.0 (320), 35.0 / 36.0 (416),
  21.4 / 22.3 (416×256), 85.8 / 91.1 (640). Mac roughly 1.7× faster. Load + optimise 140–200 ms.
- `https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_nano.onnx`, 3,659,407 bytes,
  SHA-256 `c789161ed43c8269fcd4e67c67eeeb4e80c622da2eb296a20bc6007bd18a0b7d`, Apache-2.0 (Megvii), COCO-trained.

Builds:
- `rust:alpine` (native arm64) needs no extra packages; cold build ~6.5–7.7 min under colima. A musl `cdylib` needs
  `RUSTFLAGS="-C target-feature=-crt-static"` and then depends on `libgcc_s.so.1` (present in the HA image). A
  stripped musl `cdylib` with tract-onnx was 17 MiB (the models are not most of it).
- Colima shares only `$HOME`: bind mounts of `/tmp` paths come up empty; sources go in as a tar stream. `docker cp`
  or tar from macOS needs `COPYFILE_DISABLE=1 tar --no-xattrs --no-mac-metadata`.
- Docker here runs `linux/amd64` containers under emulation (`uname -m` → `x86_64`), so x64 RIDs build locally.
- Resizing a 1–2 MP frame with the `image` crate's Triangle filter costs 4–16 ms, close to inference; the real
  path resizes itself (box filter) or reads frames already scaled (`VideoOptions.MaxWidth`).
- Test photos: `Smiling_Bangladeshi_men.jpg` (Wikimedia Commons, Steve Evans, CC BY 2.0) and "Pedestrians Walking
  South on Eighth Avenue Near West 31st Street, New York City.jpg" (Wikimedia Commons, CC0).

## Design

### 1. Native library (hass-sharp-vision `native/`, crate `hs_vision`)

A Rust `cdylib` with both models embedded (`include_bytes!`), so setup is one download and a model can't drift from
its decoder. C ABI, version 1:

- `hs_vision_abi_version() -> u32`, checked by the C# side.
- `hs_vision_open() -> *mut Detector` (null on failure; `hs_vision_last_error() -> *const c_char`, thread-local).
  Parses YuNet at open; YOLOX on its first use, so a face-only user never pays for it.
- `hs_vision_faces(det, rgb: *const u8, width, height, stride, input_size, min_score: f32, out: *mut Face,
  max_faces) -> i32` — faces written, best first, or −1 with last error. The picture is letterboxed so its long side
  is `input_size` (multiple of 32), padded with 0.
- `hs_vision_people(det, rgb, width, height, stride, input_size, min_score: f32, out: *mut Person, max_people) ->
  i32` — the same for people, padded with 114.
- `#[repr(C)] struct Face { x, y, w, h, score: f32, landmarks: [f32; 10] }` (right eye, left eye, nose tip, right
  mouth corner, left mouth corner); `#[repr(C)] struct Person { x, y, w, h, score: f32 }`; all in the input
  picture's pixels.
- `hs_vision_close(det)`.
- One runnable plan per (model, padded shape), a few kept. A `Detector` serialises its own runs; panics never cross
  the ABI.
- Room to grow: face recognition (next project) adds an export that takes a face's landmarks and returns its
  embedding; nothing in version 1 has to change for it.

**Size.** Two models plus tract. The build aims for under 8 MiB per platform (opt-level `s`/`z`, LTO,
`codegen-units = 1`, strip; tract features trimmed). The builder reports the size reached; correctness wins over size.

### 2. Native hosting: release assets of hass-sharp-vision

- Binaries are `libhs_vision-<rid>.so` assets of a GitHub release of hass-sharp-vision, with `SHA256SUMS` beside
  them; none is ever committed. RIDs: `linux-musl-arm64`, `linux-musl-x64`, `linux-arm64`, `linux-x64`;
  `linux-musl-arm` / `linux-arm` if they build cleanly.
- `native/build.sh` builds every RID in Docker: `rust:alpine` for musl, an older-glibc Debian image (bullseye, glibc
  2.31) for gnu; a RID of another architecture than the host's under emulation. Sources stream in with tar; it
  strips, writes the `.so` files into an ignored directory and writes their `SHA256SUMS` there.
- The release is built by hass-sharp-vision's own CI from a `v<version>` tag, with the built-in `GITHUB_TOKEN` (no
  cross-repository secret), and attaches the `.so` files and `SHA256SUMS` (and the licence texts the binaries need).
  A rebuild is a new version tag, never a re-upload under an old one. `native/SHA256SUMS` is committed (it is small)
  and equals the release's.
- Downloads come from
  `https://github.com/dompagoj/hass-sharp-vision/releases/download/v<version>/libhs_vision-<rid>.so`, pinned by the
  version in the URL and each file's SHA-256 in `Vision.cs`. Binaries are rebuilt only when the Rust code changes.

### 3. Library node setup hook: `[NodeSetup]`

- A public static `Task Setup(NodeSetupContext context)` marked `[NodeSetup]` on a node class runs once when a
  **definition** whose graph uses any `[Node]` of that class starts: in the definition's setup phase, on the same
  `SetupClock` as C# nodes' `Setup()` (download time not counted), before the C# nodes and the When started flow. A
  failure fails the definition's start, naming the class.
- **Definitions only.** Automations have no setup clock, and activation runs under `_visualGate`. A node of such a
  class is entity-only (`documents: ["entity"]`), so automations refuse it with the existing `node.document` message.
- `NodeSetupContext`: `Files`, `Log`, `RuntimeIdentifier`, `Stopping`, and `Own(IDisposable)` — owned objects are
  disposed when the definition stops.
- State lives in the class's statics; each graph has its own collectible load context, so setup runs per definition
  and resets on recompile; downloads are cached by checksum.

### 4. `SharpImage.DrawCircle`

`DrawCircle(x, y, radius, color, filled = false, thickness = 2)`, documented in `docs/custom-entities.md` "Cameras in
C#".

### 5. The vision library files (hass-sharp-vision `nodes/`)

Two node library files, imported together:

- **`Vision.cs`**
  - `public sealed class VisionDetector : IDisposable` — wraps one native `Detector`: `Faces(SharpImage, inputSize,
    minScore)` and `People(SharpImage, inputSize, minScore)` return `IReadOnlyList<DetectedFace>` /
    `IReadOnlyList<DetectedPerson>` (records in input pixels), under a `System.Threading.Lock`; after `Dispose`,
    calls throw.
  - `public static class Vision` — `Task<VisionDetector> OpenAsync(SharpFiles files, string runtimeIdentifier,
    CancellationToken cancellation)`: unsupported platform → clear error naming it; else downloads the pinned
    `libhs_vision-<rid>.so`, loads it once per process (`NativeLibrary.Load`, never freed), resolves the exports as
    `delegate* unmanaged` (pattern: `Devices/Imaging/TurboJpeg.cs`), checks the ABI version, opens a detector.
    `DrawFaces(SharpImage, faces)` and `DrawPeople(SharpImage, people)` draw on a picture within its 500-drawing
    limit. The pinned URL and SHA-256 table live here.
  - `public static class FaceDetection` — `[NodeSetup] Setup` opens a detector with `context.Files` and `Own`s it;
    `[Node("Detect faces", Category = "Vision", Pure = true)] SharpImage DetectFaces(SharpImage image, double
    minScore = 0.7, int inputSize = 320)` draws on a `Copy()`.
- **`PeopleDetector.cs`**
  - `public abstract class PeopleDetector : SharpDevice`, used by deriving from it in a C# device node
    (`public class FrontDoor : PeopleDetector { }`).
  - Settings: **Camera** (`CameraEntity`, required); **Check every** (seconds, default 0.5, 0.1–60); **Stay on for**
    (seconds a "seen" entity stays on after the last detection, default 10, 0–3600); **Face score** (default 0.7);
    **Person score** (default 0.45).
  - Entities:
    - `camera` — the newest analysed frame with people's boxes, faces' boxes and landmarks drawn
    - `face_seen` / `person_seen` — binary sensors, device class `occupancy`
    - `faces` / `people` — sensors, the counts in the newest analysed frame, state class `measurement`
    - `last_face_seen` / `last_person_seen` — sensors, device class `timestamp`
  - Life cycle: `Setup()` opens its own detector with `Vision.OpenAsync(Files, …)`; `Start()` opens
    `SharpVideo.Open(Camera, new VideoOptions { MaxWidth = 1280 })` and runs `Every(Check every)`: next frame →
    faces (input 640) and people (input 416) → draw → show on `camera` → update the entities; `Stop()` disposes the
    video and the detector.
  - A missed frame or a failing check is logged once per new reason (as `Every` does); the counts go unavailable
    until a check works again, and each seen entity still turns off once its hold time has passed, so a camera that
    drops out never leaves Person seen stuck on.
  - The device's camera is a `SharpCamera` subclass in the same file, added by the device.

### 6. Manifest (`hass_sharp.toml` at a repo's root)

The format is HassSharp's; this is hass-sharp-vision's own manifest, at its root:

```toml
[[library]]
id = "vision"                                     # stored under this id when free
name = "Vision.cs"                                # library file name in the panel
path = "nodes/Vision.cs"
description = "Face and person detection on a native library (YuNet and YOLOX-nano on tract), and the Detect faces node"

[[library]]
id = "people_detector"
name = "PeopleDetector.cs"
path = "nodes/PeopleDetector.cs"
description = "People detector: a device that watches a camera and reports faces and people seen"

[[entity]]
name = "Front door people"
path = "nodes/front_door_people.json"             # a DefinitionBody: name, settings, logic
description = "A People detector on camera.front_door"
```

Library node kinds name their library's id, so import rewrites the `library:` kinds in imported definitions to the
id actually used.

### 7. Fetching

GitHub only. Accept `https://github.com/<owner>/<repo>` with an optional `/tree/<ref>`. Resolve the ref to a commit
sha with `GET https://api.github.com/repos/{owner}/{repo}/commits/{ref}` (`HEAD` when none), then fetch only
`hass_sharp.toml` and the files it lists from `https://raw.githubusercontent.com/<owner>/<repo>/<sha>/<path>`. No git
process and no tarball (a repo can carry far more than the files listed, such as models and test photos). Parse with `tomllib`. Cap each file (2 MiB) and the item
count, reject absolute paths, `..`, and anything but `.cs` / `.json`.

### 8. API

- Websocket route `hass_sharp/external` (admin): `inspect {url}` → owner, repo, sha and items (kind, id, name,
  description, path, existing matches, any problem with the file); `import {owner, repo, sha, picks}` re-fetches that
  sha.
- Libraries offer **Overwrite** or **Skip** when one with that name exists — all library files share the `NodeLib`
  namespace. Entities offer Overwrite / Add as new / Skip.
- Libraries are saved through a new C# `import` operation: several documents in one preview + one commit. Atomic.
- Entities, after libraries, through the existing entity `save`: new ones turned off (`enabled: false`), overwrites
  with the revision inspect saw, keeping setting values whose key and type still match.

### 9. UI

An **Add external** button beside "Add file" in the node libraries view and beside "New entity definition" in the
entities list, opening one dialog: URL → Inspect → checkbox list grouped Node libraries / Entity definitions, with
descriptions, links to the files on GitHub at that sha, and the Overwrite / Add as new / Skip choice → Import → a
notice of what was imported. The dialog says plainly that imported C# runs with full access to Home Assistant.

### 10. Two repositories

- **hass-sharp** (this repo) keeps the platform: `DrawCircle`, `[NodeSetup]` and its host side, "Add external" and
  the `hass_sharp.toml` format, their docs, and any generic test-helper change (such as the fake ffmpeg's `still=`).
- **hass-sharp-vision** (`https://github.com/dompagoj/hass-sharp-vision`, MIT) holds the rest, as a third-party
  author would publish it:

  ```
  LICENSE  README.md  hass_sharp.toml  .gitignore
  native/                 the hs_vision crate (Cargo.toml, src, model, tests, NOTICE, build.sh, SHA256SUMS, licence texts)
  nodes/                  Vision.cs, PeopleDetector.cs, front_door_people.json
  tests/                  the C# test project and the Python pin check
  .github/workflows/      ci.yaml, release.yaml
  ```

  The crate's history comes along: its commits on hass-sharp's branch (they touch only `nodes/vision/native/` and
  one `.gitignore` line) are carried with `git subtree split --prefix=nodes/vision/native`, and the branch then drops
  them (asked of the owner first, with a backup tag). The README replaces the vision part of HassSharp's docs.
- **Its CI** runs `cargo test`, a Docker build check and the pin check on every push; a C# job runs the node code's
  tests against a hass-sharp checkout at a ref pinned in the workflow, and stays switched off until that ref (one with
  `[NodeSetup]`) is on GitHub. **Its release workflow** runs on `v*` tags: it builds every RID, refuses a tag that
  already has a release, and attaches the `.so` files and `SHA256SUMS`.
- **Its C# tests.** HassSharp has no kit for third-party authors yet, so hass-sharp-vision has a C# test project that
  builds against a hass-sharp checkout named by an MSBuild property (`HassSharpRoot`: the developer's checkout
  locally, `actions/checkout` of `dompagoj/hass-sharp` in CI) and links hass-sharp's runtime-test sources for their
  helpers.

## Follow-up in hass-sharp (not this round)

A **test kit for library authors**, so a repository like hass-sharp-vision can test its node libraries and devices
against a released HassSharp without a checkout or linked test sources.

## Next project (not this round): face recognition and device UI

Recorded so this round does not paint it into a corner.

- **Recognition.** OpenCV's SFace (Apache-2.0) turns a face, straightened with YuNet's five landmarks, into an
  embedding. Faces whose embeddings are close are the same person. It runs in tract like the detectors and becomes
  one more export of `hs_vision`. InsightFace/ArcFace weights are non-commercial and are avoided.
- **People database.** The People detector keeps known people (first name, last name, and more) with several face
  samples each, and a queue of unknown faces with their crops. Local only, in `.storage`; deleting a person deletes
  their samples. A face database is biometric data: a camera covering the street may fall outside the household
  exemption of EU data-protection rules.
- **Device UI in the panel**, where Problems, generated C# and Runs are: for the People detector, the unknown faces,
  each with its picture and a small form (who is this, or dismiss), and the known people. How a device describes its
  UI (declarative, rendered by the panel, or its own components) is that project's main design question.
- **Person data in automations.** For example a "Person recognized" trigger whose data carries the person, typed so
  steps can read `FirstName`, plus entities such as a "Last recognized" sensor.
- A person seen from behind is detected but never identified: "seen" and "who" stay separate signals.

## Left to the builder

tract version; build flags reaching the size target; resize filter; drawing colours and sizes; how `[NodeSetup]` is
surfaced in the catalog; dialog layout; test fixtures (small, licence-clean).

## Verification

- hass-sharp runtime suite: `DrawCircle` pixels; `[NodeSetup]` runs once per definition before runs, `Own` disposes
  on stop, refused in automations, counts against the setup clock but not download time, failure fails the start;
  library `import` is atomic.
- hass-sharp-vision C# tests (against a hass-sharp checkout): the vision library end to end against a locally built
  library (skipped when absent, required in CI): Detect faces on the fixture, a People detector on a video fixture
  setting its entities and holding "seen" for its time; the repository's manifest imports.
- `cargo test` in hass-sharp-vision `native/`: decode/NMS on fixture tensors for both models, ABI checks, faces and
  people on the fixture photos. A pin check: `Vision.cs`'s table equals `native/SHA256SUMS`, and the release's.
- Editor tests for catalog/diagnostic changes.
- Python: `external` inspect/import against a faked GitHub.
- Packaging tests: release package unchanged in shape.
- Frontend: the dialog flow.
- Live, local instance only: before a release exists, paste the libraries with the URL pointed at a host-served copy
  of the built `.so`; wire `testcamera` frame → Detect faces → Draw text → Draw rectangle → entity, and add a People
  detector on the dummy camera (or a Video frame of the fixtures); confirm one download, live boxes, the entities
  turning on and off with the hold time, and no re-download after a reload. Once the owner has given the go to push
  and release hass-sharp-vision: Add external with `https://github.com/dompagoj/hass-sharp-vision` on the local
  instance running this branch, downloading from the release; no hass-sharp merge is waited for.
