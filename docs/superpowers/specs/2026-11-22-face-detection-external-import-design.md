# Face detection node and "Add external" import

## Intent

Custom entities and the graph camera work: the local `testcamera` definition runs front-door camera → Camera frame →
Draw text → Draw rectangle → camera entity. The custom-entities design named in-process inference as the next step
(`2026-11-17-custom-entities-design.md`). The owner wants two things, built in this order:

- **A. A face detection library node.** It takes a `SharpImage` and returns a `SharpImage` with each face's box and
  five landmarks drawn. When its definition sets up, it downloads its native face-detection library. That library is
  built in this repo and committed under `nodes/`, never shipped in the integration (the release is capped at
  100 MiB).
- **B. "Add external".** Paste a GitHub repository URL. HassSharp reads `hass_sharp.toml` at the repo's root, which
  lists the repo's node libraries and entity definitions, and a dialog lets you pick what to import. The face node
  lives in this repo and is the first thing imported this way.

Then the imported node is wired into `testcamera` on the local instance (port 5001).

## Goals

- A camera definition can place **Detect faces** between Camera frame and the camera's Image and see boxes plus eyes,
  nose tip and mouth corners at live frame rates.
- Library nodes can do one-time work when their definition starts (download, load native code) and clean it up
  when it stops.
- Any public GitHub repo with a `hass_sharp.toml` can offer node libraries and entity definitions for import from the
  panel.

## Non-goals

Dense face mesh; face *recognition* (who it is); GPU; a generic inference runtime or inference API in HassSharp;
NuGet packages for user code; tracking or updating imports; hosts other than GitHub; native inference code in the
integration release; `[NodeSetup]` in automations.

## Anti-goals

- An import that half-applies (some libraries saved, others not).
- A library save that leaks a native detector each time it recompiles.
- A face node that can silently run a different model than its decoder expects.

## Decisions (owner's)

- Output: face box + 5 key points. Not a dense mesh.
- Inference: **our own Rust library on tract** (Sonos's pure-Rust ONNX engine), specific to this node, not a generic
  runtime. Model: **YuNet** `face_detection_yunet_2023mar.onnx` (OpenCV zoo, MIT).
- Native binaries are **built and committed in this repo**, not published as GitHub releases.
- Import is a **one-time copy**: no origin tracking, no update action.
- **No pushes** during the work. The live import from this repo's URL waits until the owner has merged to main.

## Approaches considered (inference)

- **Rust + tract, face-specific (chosen).** Pure Rust, builds for musl in minutes in Docker, we own a tiny C ABI.
  The spike passed (below).
- libfacedetection (YuNet's own C++ library, weights compiled in): equally specific; was the fallback.
- ONNX Runtime built for musl: its edge is running any model, which we don't need; the musl build is the riskiest
  and slowest. Dropped.
- Pure C# forward pass: bespoke network code, slower. Dropped.

## Spike results (2026-11-22, throwaway, `tract-onnx` 0.23.10)

- YuNet loads and runs once shape facts from the file are ignored:
  `tract_onnx::onnx().with_ignore_output_shapes(true).with_ignore_value_info(true)`. Without that, fixing any input
  size other than 640×640 fails in shape inference. Any size that is a multiple of 32 works.
- Input 1×3×H×W, **BGR** float 0–255, no mean/scale, zero-padded bottom/right to a multiple of 32 (OpenCV
  `FaceDetectorYN`). Outputs selected by name: `cls_8/16/32`, `obj_8/16/32`, `bbox_8/16/32`, `kps_8/16/32`.
  Decode: score = sqrt(clamp(cls)·clamp(obj)), threshold, priors per stride, 5 landmarks, greedy NMS IoU 0.3.
- Faces: 8/8 on a 1080×720 group photo at 640×480 (including near-profile and partly hidden faces), 7/8 at 320×240;
  no false positives; identical results natively and in the `hass` container.
- Single-thread inference, median / p90 ms: Mac 4.2 / 4.7 (320×240), 16.4 / 18.6 (640×480); `hass` container (Alpine
  aarch64 musl) 7.6 / 8.5 and 29.0 / 31.7. Decode + NMS ≈ 0.02 ms. Load ≈ 40 ms.
- Size: a musl `cdylib` with the model embedded is 17 MiB stripped (model 232 KB; the rest is tract-onnx).
- Build: `rust:alpine` (native arm64) needs no extra packages. A musl `cdylib` needs
  `RUSTFLAGS="-C target-feature=-crt-static"` and then depends on `libgcc_s.so.1` (present in the HA image).
- Docker here is colima, which shares only `$HOME`: bind mounts of `/tmp` paths come up empty. `docker cp` from macOS
  needs `COPYFILE_DISABLE=1 tar --no-xattrs --no-mac-metadata` to avoid the provenance xattr error.
- Model: `https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx`,
  232,589 bytes, SHA-256 `8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4`.

## Design

### 1. Native library (`nodes/face_detection/native/`)

A Rust `cdylib` named `hs_face`, model embedded with `include_bytes!` so setup is one download and the model can't
drift from the decoder. C ABI:

- `hs_face_abi_version() -> u32`, checked by the C# side.
- `hs_face_open() -> *mut Detector` (null on failure; `hs_face_last_error() -> *const c_char`, thread-local).
- `hs_face_detect(det, rgb: *const u8, width: i32, height: i32, stride: i32, input_size: i32, min_score: f32,
  out: *mut Face, max_faces: i32) -> i32`: count, or −1 with last error. Converts RGB→BGR, letterboxes to an input
  whose long side is `input_size` (multiple of 32, aspect kept, padded), runs tract, decodes, NMS IoU 0.3, maps back
  to the input's pixels. One runnable plan per input shape, cached in the detector.
- `#[repr(C)] struct Face { x, y, w, h, score: f32, landmarks: [f32; 10] }` (right eye, left eye, nose tip, right
  mouth corner, left mouth corner).
- `hs_face_close(det)`.
- A `Detector` serialises its own runs; panics never cross the ABI (`catch_unwind` → error).

**Size.** The spike's 17 MiB × 4–5 platforms would add ~85 MB of history per rebuild. The real build aims for under
8 MiB: `opt-level = "s"` or `"z"`, `panic = "abort"` (then `catch_unwind` is moot — errors are returned, not
panicked), LTO, `codegen-units = 1`, strip, and converting the model offline to tract's NNEF format so only
`tract-nnef` (no ONNX/protobuf parser) is linked. The builder reports the size reached; if NNEF conversion breaks
correctness, ONNX stays.

### 2. Native hosting: committed in the repo

- Binaries at `nodes/face_detection/native/bin/libhs_face-<rid>.so` with `SHA256SUMS`, plain git (no LFS: raw URLs
  would serve LFS pointers). RIDs: `linux-musl-arm64`, `linux-musl-x64`, `linux-arm64`, `linux-x64`;
  `linux-musl-arm` / `linux-arm` if they build cleanly.
- `nodes/face_detection/native/build.sh` builds every RID **in Docker** (no Zig, nothing installed on the host):
  `rust:alpine` for musl, an older-glibc Debian image (bullseye, glibc 2.31) for gnu; x64 under Docker's emulation
  on an arm64 host. It streams sources in with tar (colima mounts), strips, and rewrites `SHA256SUMS`.
- CI: a new job in `.github/workflows/tests.yaml` runs `cargo test` and a build check. It does not commit binaries.
- The node downloads from
  `https://raw.githubusercontent.com/<owner>/<repo>/<commit-sha>/nodes/face_detection/native/bin/libhs_face-<rid>.so`,
  pinned to the commit that added the binaries plus each file's SHA-256. Binary commit first, then a commit pinning
  its sha in `FaceDetection.cs`.
- Binaries are rebuilt only when the Rust code changes.

### 3. Library node setup hook: `[NodeSetup]`

- A public static `Task Setup(NodeSetupContext ctx)` marked `[NodeSetup]` on a node class runs once when a
  **definition** whose graph uses any `[Node]` of that class starts: in the definition's setup phase, on the same
  `SetupClock` as C# nodes' `Setup()` (download time not counted; `Devices/DefinitionHost.cs`,
  `DefinitionHost.Graph.cs`), before the When started flow. A failure fails the definition's start, naming the class.
- **Definitions only.** Automations have no setup clock, and activation runs under `_visualGate`, so a download there
  would stall every request. Using such a node in an automation is a validation error ("only in a custom entity
  definition").
- `NodeSetupContext`: `Files` (the definition's `SharpFiles`), `Log`, `RuntimeIdentifier`
  (`RuntimeInformation.RuntimeIdentifier`), `Stopping`, and `Own(IDisposable)` — owned objects are disposed when the
  definition stops, so the native detector is closed on every restart and recompile.
- State lives in the class's statics; each graph has its own collectible load context, so setup runs per definition
  and resets on recompile; downloads are cached by checksum, so re-setup is instant.
- The compiler/validator learn the attribute: at most one per class, signature checked, diagnostics like the other
  node attributes.

### 4. `SharpImage.DrawCircle`

`DrawCircle(x, y, radius, color, filled)` — a `Drawing` record, a `Render` case and an `ImagePainter` function;
documented in `docs/custom-entities.md` "Cameras in C#". No public resize (the native library resizes).

### 5. The face node (`nodes/face_detection/FaceDetection.cs`)

- `[NodeSetup]`: if `RuntimeIdentifier` has no pinned asset, fail with a clear message naming the platform. Else
  `Files.Download` the library, `NativeLibrary.Load` it, resolve exports as `delegate* unmanaged` (pattern:
  `Devices/Imaging/TurboJpeg.cs`), check `hs_face_abi_version`, `hs_face_open`, `Own` a handle that calls
  `hs_face_close`.
- `[Node("Detect faces", Category = "Vision", Pure = true)]`
  `SharpImage DetectFaces(SharpImage image, double minScore = 0.7, int inputSize = 320)`: `CopyPixels` as RGB into a
  pooled buffer, call `hs_face_detect` under a `System.Threading.Lock`, then on a `Copy()` of the input draw a
  rectangle per face, a filled circle per landmark (distinct colours) and the score as text.
- If setup has not succeeded, the node throws a clear error.

### 6. Manifest (`hass_sharp.toml` at a repo's root)

```toml
[[library]]
name = "FaceDetection.cs"                          # library file name in the panel
path = "nodes/face_detection/FaceDetection.cs"
description = "Detect faces: box + 5 landmarks (YuNet on tract)"

[[entity]]
name = "Face camera"
path = "nodes/face_detection/face_camera.json"     # a DefinitionBody: name, settings, logic
description = "Camera that marks faces"
```

This repo gets a root `hass_sharp.toml` with the face library and an example face-camera definition (camera entity →
Camera frame → Detect faces → camera entity).

### 7. Fetching

GitHub only. Accept `https://github.com/<owner>/<repo>` with an optional `/tree/<ref>`. Resolve the ref to a commit
sha with `GET https://api.github.com/repos/{owner}/{repo}/commits/{ref}` (default branch when none), then fetch only
`hass_sharp.toml` and the files it lists from `https://raw.githubusercontent.com/<owner>/<repo>/<sha>/<path>`, in an
executor. No git process and no tarball (the repo carries native binaries). Parse with `tomllib`. Cap each file
(2 MiB), reject absolute paths, `..`, and anything but `.cs` / `.json`.

### 8. API

- Websocket route `hass_sharp/external` (admin, `envelope_command` pattern in `websocket.py`):
  - `inspect {url}` → `{owner, repo, sha, items: [{kind: "library"|"entity", name, description, path,
    existing: [{id, revision, name}]}]}`.
  - `import {owner, repo, sha, picks: [{kind, path, mode: "overwrite"|"new", target?: id}]}` re-fetches that sha, so
    what you picked is what you get.
- Libraries offer **Overwrite** or **Skip** only when one with that name exists — all library files share the
  `NodeLib` namespace, so a second copy would never compile. When several share the name, the dialog asks which to
  overwrite. Entities offer Overwrite / Add as new / Skip.
- Libraries are saved through a new C# `import` operation in `NodeLibraryApi.cs`: several library documents in one
  preview + one commit, with the same validation and errors as `save`. Atomic.
- Entities, after libraries, through the existing entity `save`: new ones with `expectedRevision: 0` and
  `enabled: false` (as Duplicate does), id by the `definitionId()` rule; overwrite with the existing revision.
- "Existing" matches by library file name and by definition name.

### 9. UI

An **Add external** button beside "Add file" in the node libraries view and beside "New entity definition" in the
entities list, opening one shared dialog (`components/Dialog.tsx`, `preventClose` while fetching): URL → Inspect →
checkbox list grouped Node libraries / Entity definitions, each with description, a link to the file on GitHub at
that sha, and the Overwrite / Add as new / Skip choice when it exists → Import → a notice of what was imported. The
dialog says plainly that imported C# runs with full access to Home Assistant.

## Left to the builder

tract version; exact build flags reaching the size target; resize filter; landmark colours and sizes; how
`[NodeSetup]` is surfaced in the catalog; dialog layout; test fixture images (license-clean, small).

## Verification

- Runtime suite: `DrawCircle` pixels; `[NodeSetup]` runs once per definition before runs, `Own` disposes on stop,
  refused in automations, counts against the setup clock but not download time, failure fails the start; face node
  end to end on a fixture image against a locally built `.so` (skipped when absent); library `import` is atomic.
- `cargo test` in `nodes/face_detection/native/`: decode/NMS on fixture tensors, ABI null/size checks, faces on the
  fixture photo.
- Editor tests for catalog/diagnostic changes.
- Python: `external` inspect/import against a faked GitHub (URL forms, ref → sha, traversal, extensions, size caps,
  bad TOML, missing files, existing items, library overwrite-only).
- Packaging tests: release package unchanged in shape.
- Frontend: dialog flow (inspect → pick → import, overwrite vs new, errors).
- Live, local instance only: before merge, paste `FaceDetection.cs` with the URL pointed at a host-served copy
  (`python3 -m http.server`, reached as `host.docker.internal`); wire `testcamera` frame → Detect faces → Draw text →
  Draw rectangle → entity; confirm one download in the logs, boxes + 5 points at a sustained frame rate, no
  re-download after reloading the integration. After the owner merges: Add external with this repo's URL.
