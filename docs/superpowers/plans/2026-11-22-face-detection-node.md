# Face detection node (Project A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A camera definition can place **Detect faces** between Camera frame and its Image and see each face's box, five landmarks and score, from a Rust/tract library the node downloads once when its definition starts; library nodes get a `[NodeSetup]` hook for that one-time work, and `SharpImage` gets `DrawCircle`.

**Architecture:** Bottom-up, every commit green. (1) `SharpImage.DrawCircle`. (2) The compiler learns `[NodeSetup]`: it checks it, marks every `[Node]` of such a class as an entity-definition-only kind (`documents: ["entity"]`, which both validators and the palette already honour), and the generator emits a `SharpNodeSetups` class only for a graph that uses one. (3) The host runs those setups in the definition's set-up phase on the C# nodes' `SetupClock`, before the When started flow, and disposes what they `Own` when the definition stops. (4–6) A Rust `cdylib` `hs_face` (YuNet embedded, tract), slimmed, built in Docker for every Linux RID and committed. (7) CI. (8) `nodes/face_detection/FaceDetection.cs`, pinned to the binaries' commit. (9) Docs. (10) Live check on the local instance.

**Tech Stack:** C# on .NET 11 (`CC/HassSharp/`, custom runner `tests/Runtime/`, xUnit `CC/HassSharp.Editor.Tests/`), Rust 1.99.1 with `tract-onnx` / `tract-nnef` 0.23.10, Docker (colima on the owner's Mac), GitHub Actions, Python 3 unittest. `CC/` is `custom_components/hass_sharp/`, `FD/` is `nodes/face_detection/`, `NAT/` is `nodes/face_detection/native/`.

**Spec:** `docs/superpowers/specs/2026-11-22-face-detection-external-import-design.md` — sections 1–5 and the Project A verification items. The spike crate is a seed: `/private/tmp/claude-501/-Users-dompa-dev-HassSharp/95d470cd-8318-4080-8ff2-80b6f34aeccc/scratchpad/spike-tract/yunet-tract/` (`src/lib.rs`: detector, decode, NMS, BGR tensor; `src/main.rs`: benchmark); its model and photo are in `.../spike-tract/downloads/`. The scratchpad can be cleared by a reboot: Task 4 copies what it needs into the repo first.

**Execution policy:** every task that changes code (Tasks 1–9, small ones too) is implemented by an Opus subagent; reviewers may be Sonnet for small re-reviews. No task pushes, anywhere.

## Global Constraints

- **Project B is out.** No task creates `hass_sharp.toml`, `FD/face_camera.json`, the `external` websocket route, the library `import` operation or any dialog.
- **Every commit leaves these green**, from the repository root: `python3 -m unittest discover -s tests/python`; `python3 -m unittest discover -s tests`; `dotnet run --project tests/Runtime --disable-build-servers`; `dotnet test CC/HassSharp.Editor.Tests`; `(cd CC/frontend && bun run test)`; from Task 4 on, `(cd NAT && cargo test --release --locked)`. After `dotnet publish -c Release ./CC/HassSharp -o ./CC/out`, the SQLite and entity bridge smokes (AGENTS.md).
- **Nothing changes for graphs without a setup class.** `tests/fixtures/generated-source-pins.json` passes unchanged (`--generated-source-pins`); `CC/visual_node_catalog.json` and `tests/fixtures/visual-rich-catalog.json` are not edited; the release package's shape is unchanged (`tests/test_release_package.py`). Nothing under `nodes/` is packaged (`scripts/package_release.py` packages `CC/` only).
- **Locks** (AGENTS.md): a new lock is `readonly Lock gate = new();` taken with `lock`; never lock on an `object`, `this` or a collection; never pass a `Lock` as `object` or to `Monitor`.
- **Native code never ships in the integration.** Binaries live at `NAT/bin/libhs_face-<rid>.so` in plain git (no LFS), with `NAT/bin/SHA256SUMS` (`<sha256>  libhs_face-<rid>.so`, one line each, sorted by RID).
- **RIDs:** `linux-musl-arm64`, `linux-musl-x64`, `linux-arm64`, `linux-x64`; `linux-musl-arm`, `linux-arm` only if they build cleanly.
- **Model:** `face_detection_yunet_2023mar.onnx`, 232,589 bytes, SHA-256 `8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4`, from `https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx`, MIT (Copyright (c) 2020 Shiqi Yu). Committed at `NAT/model/`, embedded with `include_bytes!`.
- **Spike facts that bind** (tract 0.23.10): load with `tract_onnx::onnx().with_ignore_output_shapes(true).with_ignore_value_info(true)`; input 1×3×H×W **BGR** float 0–255, no mean/scale, zero-padded bottom/right to a multiple of 32; outputs by name `cls_8, cls_16, cls_32, obj_8, obj_16, obj_32, bbox_8, bbox_16, bbox_32, kps_8, kps_16, kps_32`; score = sqrt(clamp(cls)·clamp(obj)); NMS IoU 0.3. Musl `cdylib`: `RUSTFLAGS="-C target-feature=-crt-static"` (then needs `libgcc_s.so.1`, present in the HA image). Colima shares only `$HOME`: `/tmp` bind mounts come up empty, so sources go into containers as a tar stream; on macOS `COPYFILE_DISABLE=1 tar --no-xattrs --no-mac-metadata` (GNU tar on Linux has neither `--no-mac-metadata` nor the provenance problem).
- **Download URL:** `https://raw.githubusercontent.com/dompagoj/hass-sharp/<commit-sha>/nodes/face_detection/native/bin/libhs_face-<rid>.so`, `<commit-sha>` the commit that added the binaries (Task 6), each file pinned by its SHA-256.
- **Live checks** use the local instance only: <http://localhost:5001>, container `hass`, restart with `docker compose restart -t 60 hass`. **Never** the `mcp__hass__*` tools: they reach the production instance.
- **Commit messages** are one plain sentence saying what is now true, as the branch's history does.

## Review Focus

1. **The detector closed while a frame is being detected.** Stop drains frames for `RunDrainTimeout` and then goes on; a frame still in `hs_face_detect` must not see its detector freed under it, and a call after close must fail with the node's sentence, not crash Home Assistant. `Detector.Dispose` takes the same `Lock` as `Detect`. Test in Task 8 (`DisposeWaitsForADetectAndLaterCallsFail`).
2. **An image that already carries drawings near the 500 limit**, such as one Draw text and Draw rectangle wrote on upstream, or a crowd. Detect faces draws the faces that fit (7 drawings each) and never throws "An image carries at most 500 drawings". Test in Task 8 (`DrawsOnlyWhatFitsUnderTheDrawingLimit`).
3. **A live frame in YUV 4:2:0, or a picture of odd size** (1×1, 20×20, 8192×8). Faces are found in a YUV frame as in its JPEG; a tiny or extreme picture gives no faces and no error. Tests in Task 4 (`tiny_and_extreme_sizes_find_nothing`) and Task 8 (`AYuvFrameFindsTheSameFaces`).
4. **A library edit that adds `[NodeSetup]` to a class an automation already uses.** The library save is refused as any save that breaks a working graph is, naming the automation and `node.document`; the automation keeps running. Test in Task 2 (`AddingASetupToAClassAnAutomationUsesIsRefused`).
5. **A setup abandoned on its time limit that goes on and calls `Own` after the definition stopped.** The object is disposed at once and `Own` throws `ObjectDisposedException`; nothing is kept. Test in Task 3 (`OwnAfterStopDisposesAtOnce`).

---

### Task 1: `SharpImage.DrawCircle`

**Files:**
- Modify: `CC/HassSharp/Devices/Imaging/SharpImage.cs` (`Drawing` records at :53–55, a method after `DrawText` at :310–316, `Render` switch at :420–424, limits consts at :24–33), `CC/HassSharp/Devices/Imaging/ImagePainter.cs` (a `Circle` function beside `Rect` at :67)
- Test: `tests/Runtime/SharpImageTests.cs` (`Run` list at :27–53, `Limits` at :253, `UserCode` at :590)

**Interfaces:**
- Produces: `public SharpImage DrawCircle(int x, int y, int radius, SharpColor? color = null, bool filled = false, int thickness = 2)` — adds a drawing, returns this image. `internal static void ImagePainter.Circle(Span<byte> rgb, int width, int height, int x, int y, int radius, SharpColor color, bool filled, int thickness)`.

**Behaviour:**
- Pixel (px, py) is painted when `d² = (px−x)² + (py−y)² ≤ r²`, and, for an outline, also `d² > (r − thickness)²` (the line drawn inwards, as a rectangle's; `thickness ≥ radius` is a disc). Math in `long`; only the box `[x−r, x+r] × [y−r, y+r]` clamped to the picture is visited, so a centre at `int.MinValue`/`int.MaxValue` neither overflows nor loops.
- Refusals, `ArgumentException`: radius outside 1–4096 → `A circle's radius is 1 to 4096 pixels`; thickness outside 1–50 → `A circle's line is 1 to 50 pixels thick`; the 501st drawing → the existing `An image carries at most 500 drawings`. A refused circle is not added.
- The `SharpImage` class summary names `DrawCircle` beside `DrawRect` and `DrawText`.

- [ ] **Step 1: Write the failing tests** in `SharpImageTests`: `Circle()` — on `SharpImage.Load(SharpImage.Blank(128, 96).ToJpeg())`: `DrawCircle(64, 48, 20, SharpColor.Blue, filled: true)` returns the same image, `Drawings == 1`; decoded, `Near(64, 48, Blue)`, `Near(81, 48, Blue)`, `Near(64, 31, Blue)`, `Near(90, 48, Black, 24)`, `Near(64, 75, Black, 24)`. Outline `DrawCircle(64, 48, 20, SharpColor.Red, thickness: 4)`: `Near(82, 48, Red)` and `Near(64, 48, Black, 24)` and `Near(72, 48, Black, 24)`. Defaults: `DrawCircle(64, 48, 20)` is red and not filled (centre black). `CircleClipping()` — `DrawCircle(-10, -10, 30, filled: true)`, `(int.MaxValue, int.MaxValue, 4096)`, `(int.MinValue, int.MinValue, 4096)`, `(200, 200, 5)` on 64×48 do not throw, size stays 64×48, and the part of the first circle inside the picture is drawn (`Near(5, 5, Red)`) while `Near(40, 40, Black, 24)` is not. `Limits()` gains radius 0 and 4097 refused naming `1 to 4096`, thickness 0 and 51 refused naming `1 to 50 pixels thick`, and a circle as the refused 501st drawing. `UserCode()`'s `Mark` node also calls `.DrawCircle(5, 5, 3, SharpColor.Blue, filled: true)`.
- [ ] **Step 2: Run** `dotnet run --project tests/Runtime --disable-build-servers -- --sharp-image`: FAIL (no `DrawCircle`).
- [ ] **Step 3: Implement** the record `Circle(int X, int Y, int Radius, SharpColor Color, bool Filled, int Thickness) : Drawing`, its `Render` case and `ImagePainter.Circle` (row spans: for each row, the painted run's ends from `sqrt`, filled with `FillBox`-style writes).
- [ ] **Step 4: Run** `-- --sharp-image`, then every suite: PASS.
- [ ] **Step 5: Commit** — `SharpImage draws circles, filled or as an outline, clipped like its rectangles.`

---

### Task 2: The compiler and generator know `[NodeSetup]`

**Files:**
- Modify: `CC/HassSharp/Visual/NodeAttribute.cs` (add `NodeSetupAttribute`), `CC/HassSharp/Visual/VisualCatalogSnapshot.cs` (`NodeMethodDescriptor` at :26–31, new `NodeSetupDescriptor`), `CC/HassSharp/Visual/NodeLibraryCompiler.cs` (attribute lookup at :151, `Describe` at :257–375, library loop at :383–401, custom trees), `CC/HassSharp/Visual/VisualGenerator.cs` (after the `SharpNodes` block at :543–550)
- Create: `CC/HassSharp/Visual/NodeSetupContext.cs`
- Test: create `tests/Runtime/NodeSetupTests.cs` (suite `--node-setup`, `Order = 14`); `CC/HassSharp.Editor.Tests/NodeLibraryLanguageServicesTests.cs`

**Interfaces:**
- Produces:
  - `[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)] public sealed class NodeSetupAttribute : Attribute` — summary: runs once when an entity definition whose graph uses any `[Node]` of the class starts.
  - `public sealed class NodeSetupContext` with `public SharpFiles Files { get; }`, `public void Log(string message, PyLogLevel level = PyLogLevel.Info)`, `public string RuntimeIdentifier { get; }` (`RuntimeInformation.RuntimeIdentifier` unless the constructor is given one), `public CancellationToken Stopping { get; }`, `public T Own<T>(T disposable) where T : IDisposable` (returns its argument). Constructor `internal NodeSetupContext(SharpFiles files, Action<string, PyLogLevel> log, CancellationToken stopping, Action<IDisposable> own, string? runtimeIdentifier = null)`: no host type in it, so Task 8's tests can make one.
  - `public sealed record NodeSetupDescriptor(string Class, string InvocationType, string MethodName)` — `Class` is the containing type's name as messages say it (`FaceDetection`; a nested type `Outer.Inner`), `InvocationType` as `NodeMethodDescriptor.InvocationType` writes it.
  - `NodeMethodDescriptor.Setup : NodeSetupDescriptor? { get; init; }` — the setup of the method's own containing type (not a base type's); null for none and for every Custom C# node.
  - Generated, only for a graph using such a method: `public static class SharpNodeSetups { public static readonly (string Class, global::System.Func<global::HassSharp.NodeSetupContext, global::System.Threading.Tasks.Task> Setup)[] All = [ ... ]; }`, one entry per class, ordered by `InvocationType` ordinally, each entry a `Section` of the first node using it.

**Behaviour:**
- Signature: a `[NodeSetup]` method is `public static`, non-generic, returns `System.Threading.Tasks.Task`, takes exactly one parameter of type `HassSharp.NodeSetupContext` (not ref/out/params), in a type `ValidMethod`'s accessibility rules accept. Else error at its identifier: `A setup method is public static Task Setup(NodeSetupContext context): public, static, returning Task and taking one NodeSetupContext`.
- A second `[NodeSetup]` in one type: error at the second, `A class has at most one [NodeSetup] method; this is its second`.
- In a type with no `[Node]` method: warning (severity 2) `[NodeSetup] runs for the [Node] methods of its class, and this class has none`.
- In a Custom C#, C# entity or C# device node's code: error `[NodeSetup] works in a node library only; move this class into one`.
- Catalog: each library `[Node]` of a class with a setup gets `spec["documents"] = ["entity"]` (nothing else changes). So: the automation palette leaves it out (`FE/src/visual/graph.ts:58`), and both validators refuse it in an automation with code `node.document` and the existing `Words.WrongDocument` sentence, `Detect faces only works in the graph of an entity definition; delete it from this automation` (C# `VisualGraphValidator.cs:1107`, TS `contract.ts:1844`). No frontend change.
- Generator: `SharpNodeSetups` is emitted after `SharpNodes`, from the methods `MethodOf` finds for the graph's nodes; a graph with none has the source it has today.

- [ ] **Step 1: Write the failing tests** in `NodeSetupTests` (register `--node-setup`):
  - `CompilerChecksTheSetup`: a library `Probe` with `[NodeSetup] public static Task Setup(NodeSetupContext context) => Task.CompletedTask;` and a documented `[Node("Probe", Pure = true)] public static int Count() => 1;` previews valid; `Snapshot.Methods[kind].Setup` is `("Probe", "global::NodeLib.Probe", "Setup")`; the catalog node's `documents` is `["entity"]`; a node of a second class without a setup has no `documents`. Each of: `internal static Task Setup(NodeSetupContext c)`, `public static void Setup(NodeSetupContext c)`, `public static Task Setup()`, `public static Task Setup(NodeSetupContext c, int x)`, `public static Task Setup<T>(NodeSetupContext c)`, `public Task Setup(NodeSetupContext c)` gives exactly the signature sentence; two setups give the "second" sentence once; a setup in a class without nodes gives the warning and the preview stays valid; a `[NodeSetup]` in a `CustomNodeSource` (step, `csharp_entity`, `csharp_device`) gives the library-only sentence in that node's diagnostics.
  - `RefusedInAnAutomation`: `VisualGenerator.Preview` of an automation graph (manual trigger → `Probe` into a Log) is invalid with one diagnostic of code `node.document` and the sentence `Probe only works in the graph of an entity definition; delete it from this automation`.
  - `AddingASetupToAClassAnAutomationUsesIsRefused` (Review Focus 4): through `HassSharpManager` (as `EntityToolsTests.LibraryDevice` sets one up), save library `probe` without a setup, save and enable an automation using `Probe`; saving the library again with a `[NodeSetup]` answers the `validation` error whose `automations` lists that automation with a `node.document` diagnostic; the stored library is still revision 1.
  - `OnlyAGraphWithASetupClassGetsSharpNodeSetups`: a definition graph using `Probe` previews valid and its source contains `public static class SharpNodeSetups` and `("Probe", global::NodeLib.Probe.Setup)`; the same graph using a node of the class without a setup does not contain `SharpNodeSetups`.
  - Editor (`NodeLibraryLanguageServicesTests`): completion after `context.` inside a library `[NodeSetup]` method offers `Files`, `Log`, `RuntimeIdentifier`, `Stopping` and `Own`.
- [ ] **Step 2: Run** `-- --node-setup` and the editor test: FAIL.
- [ ] **Step 3: Implement** as Interfaces and Behaviour say. Look the attribute up beside `nodeAttribute` (:151); collect each library type's setup method in one pass over `trees` before the methods loop (:383), report there, and set `Setup` on the descriptor `Describe` builds (:373) for library kinds only.
- [ ] **Step 4: Run** every suite; `--generated-source-pins` unchanged.
- [ ] **Step 5: Commit** — `A node library class can declare a [NodeSetup] method: the compiler checks it, its nodes work only in entity definitions, and a definition using one is generated with its setup.`

---

### Task 3: The host runs node setups and disposes what they own

**Files:**
- Modify: `CC/HassSharp/Devices/DefinitionHost.cs` (`CompiledDefinition` at :13–40; `SetUpComponent`'s local `SetUp` at :213–226 becomes a shared method; `StopCoreAsync`'s `finally` at :463–500), `CC/HassSharp/Devices/DefinitionHost.Graph.cs` (`PrepareGraph` at :55–75; `SetUpComponents`' `finally` at :171), `CC/HassSharp/Devices/EntityApi.cs` (`CompileDefinition` at :96–100, beside `NodeClasses` at :108)
- Test: `tests/Runtime/NodeSetupTests.cs`

**Interfaces:**
- Consumes: `SharpNodeSetups.All`, `NodeSetupContext` (Task 2).
- Produces:
  - `internal sealed record NodeClassSetup(string Class, Func<NodeSetupContext, Task> Run);` and `CompiledDefinition.NodeSetups : IReadOnlyList<NodeClassSetup> { get; init; } = [];`, set by `EntityApi.CompileDefinition` from `internal static IReadOnlyList<NodeClassSetup> NodeSetupsOf(Assembly assembly)` (reads the `All` field; `[]` when the assembly has no `SharpNodeSetups`).
  - `DefinitionHost.Own(IDisposable disposable)` (internal): held under `gate` until the definition stops; once released, disposes it at once and throws `ObjectDisposedException("{definition.Name} has stopped: it keeps nothing more")`.
  - A bounded-run helper replacing the local `SetUp`: `Task RunSetUp(Func<Task> work, string who, string what)` — a `LongRunning` thread, `Within(task, clock)` on `setupClock`, failures `{who}: {message}`, time-out `{who}: {what} did not finish within {n} s`. Components call it with `what = "Setup()"` (their sentences unchanged).

**Behaviour:**
- `PrepareGraph` runs, in order: the graph's own entities; **each `NodeSetups` entry in order** (`who = "{definition.Name}: {Class}"`, `what = "its [NodeSetup]"`, given `new NodeSetupContext(Files, Log, Stopping, Own)`); the C# nodes (`SetUpComponents`); the When started flow. Node setups and C# nodes share one `SetupClock` (`setupClock ??= new SetupClock()`), nulled in one `finally` around both, so a download's receiving stops it and the When started flow still gets a clock of its own.
- A setup that throws fails the prepare with `{definition.Name}: {Class}: {message}`; one past the limit with `{definition.Name}: {Class}: its [NodeSetup] did not finish within {n} s`. Nothing is registered with Home Assistant; what it owned so far is disposed when the failed host is stopped (the manager already stops it, `UserScript.ClearCoreAsync`).
- `StopCoreAsync`'s `finally`, after `downloads?.Close()`: under `gate` take the owned list and set it empty (released is already true), then dispose each in reverse order outside the lock, a failure reported as `disposing what a node setup owns failed: {message}` at Warn. Runs on every stop, also one before `Start()` ran (the early `return` at `start == null` still reaches the `finally`).
- A definition without node setups prepares exactly as today.

- [ ] **Step 1: Write the failing tests** in `NodeSetupTests`, through `HassSharpManager` and `EntityTestHome` (helpers as `EntityToolsTests`/`EntityApiTests` have them). Library `Probe`'s setup logs `probe set up on {context.RuntimeIdentifier}` and owns an object whose `Dispose` logs `probe closed`; its pure node `Count()` returns how many setups ran in this load context.
  - `RunsOncePerDefinitionBeforeWhenStarted`: a definition whose graph uses `Count` twice and whose When started flow sets sensor `count` from it: running, the sensor's value is `1`; the info log has `{Name}: probe set up on {RuntimeInformation.RuntimeIdentifier}` exactly once, before the When started flow's own Log line. A second definition using it logs its own line: two setups, one per definition.
  - `OwnDisposesOnStop`: after a resave, `{Name}: probe closed` is logged once (the replaced revision's) and the new revision logged its own set-up; after `set_enabled` false, twice; after `api.UnloadAsync()` for a running one, once more.
  - `OwnAfterStopDisposesAtOnce` (Review Focus 5): a setup starts a task that awaits `context.Stopping`, waits 100 ms, then calls `context.Own(x)`: after the definition is turned off, the log has `late own refused: ObjectDisposedException` and `x` was disposed (`probe closed` logged by it).
  - `SetupClockExcludesDownloadTime`: `DefinitionHost.SetupTimeout = 1 s`; a setup that `Files.Download`s a body served in 9 pieces 200 ms apart (`LoopbackServer`, as `EntityToolsTests.SetupLimit`) starts running; a setup awaiting `Task.Delay(3 s, context.Stopping)` fails with exactly `{Name}: Probe: its [NodeSetup] did not finish within 1 s`; a setup of 600 ms plus a C# entity whose `Setup()` takes 600 ms fails on the shared clock with `{Name}: {entity}: Setup() did not finish within 1 s`. Restore 60 s in `finally`.
  - `FailureFailsTheStart`: a setup that owns an object and then throws `InvalidOperationException("no model")`: as `EntityApiTests.Scenario`'s failing-setup case (:492–500), the save answers `saved` true, `active` false, `activationError == "{Name}: Probe: no model"`; `home.Syncs(id)` is empty; the document reads status `failed`; `probe closed` is logged.
- [ ] **Step 2: Run** `-- --node-setup`: the new tests FAIL.
- [ ] **Step 3: Implement** as Interfaces and Behaviour say.
- [ ] **Step 4: Run** every suite (`--entity-host`, `--entity-tools`, `--entity-startup` unchanged).
- [ ] **Step 5: Commit** — `An entity definition runs the [NodeSetup] of each node class its graph uses before its When started flow, on its C# nodes' set-up clock, and disposes what they own when it stops.`

---

### Task 4: The `hs_face` crate

**Files:**
- Create: `NAT/Cargo.toml`, `NAT/Cargo.lock`, `NAT/rust-toolchain.toml` (`channel = "1.99.1"`), `NAT/src/lib.rs` (C ABI), `NAT/src/detector.rs` (model, plan cache, letterbox, tensor), `NAT/src/decode.rs` (decode, NMS), `NAT/tests/abi.rs`, `NAT/model/face_detection_yunet_2023mar.onnx`, `NAT/model/LICENSE`, `NAT/tests/fixtures/group.jpg`, `NAT/NOTICE`
- Modify: `.gitignore` (`nodes/face_detection/native/target/`)

**Interfaces:**
- Produces, C ABI (`#[no_mangle] extern "C"`), version **1**:
  - `hs_face_abi_version() -> u32` → `1`
  - `hs_face_open() -> *mut Detector` (null on failure)
  - `hs_face_last_error() -> *const c_char` — thread-local, NUL-terminated, valid until the thread's next call; null when the thread's last call succeeded
  - `hs_face_detect(det: *mut Detector, rgb: *const u8, width: i32, height: i32, stride: i32, input_size: i32, min_score: f32, out: *mut Face, max_faces: i32) -> i32` — faces written (≤ `max_faces`, the best first), or −1 with the error set
  - `hs_face_close(det: *mut Detector)` — null is a no-op
  - `#[repr(C)] pub struct Face { pub x: f32, pub y: f32, pub w: f32, pub h: f32, pub score: f32, pub landmarks: [f32; 10] }` — 60 bytes; landmarks are right eye, left eye, nose tip, right mouth corner, left mouth corner, each (x, y), in the input picture's pixels
- Produces, Rust (for tests): `decode::decode(outs: &[&[f32]; 12], pw: usize, ph: usize, min_score: f32) -> Vec<Face>`, `decode::nms(faces: Vec<Face>, iou: f32) -> Vec<Face>`, `detector::letterbox(width: usize, height: usize, input_size: usize) -> Letterbox { dw, dh, pw, ph, scale }`.

**Behaviour:**
- Refusals (−1, error text): null detector `no detector`; null `rgb` `no pixels`; `width`/`height` outside 1–8192 `the picture is 1 to 8192 pixels a side`; `stride < width*3` `the stride is less than a row`; `input_size` not a multiple of 32 in 64–1280 `the input size is a multiple of 32 from 64 to 1280`; `min_score` NaN or outside 0–1 `the minimum score is from 0 to 1`; `max_faces < 0` or (`max_faces > 0` and null `out`) `no room for faces`.
- Letterbox: `scale = input_size / max(width, height)`; `dw = max(1, round(width·scale))`, `dh = max(1, round(height·scale))`; `pw = pad32(dw)`, `ph = pad32(dh)`. Resize RGB→BGR into the top-left `dw × dh`, zero elsewhere: box filter (mean of the source pixels each target pixel covers) when shrinking, bilinear when enlarging. Results are divided by `scale` back into the input's pixels.
- Plans: the ONNX model parsed once at `open`; one optimised runnable plan per `(pw, ph)`, at most 4 kept (oldest dropped). A `Detector` holds a `Mutex` around its state: concurrent `detect`s run one at a time.
- Panics: every export is wrapped in `std::panic::catch_unwind`; a panic becomes −1 / null with `internal error: {message}`. `panic = "unwind"` in this task.
- `NAT/NOTICE`: the model (MIT, Shiqi Yu, with `model/LICENSE` copied verbatim from `https://huggingface.co/opencv/face_detection_yunet/raw/main/LICENSE`); the fixture's attribution (below); that the library statically links tract and its dependencies, under the licences `cargo tree -e normal --format '{p} {l}'` lists (MIT or Apache-2.0).
- Fixture: the spike's `Smiling_Bangladeshi_men.jpg` (Wikimedia Commons, by Steve Evans, **CC BY 2.0**, <https://commons.wikimedia.org/wiki/File:Smiling_Bangladeshi_men.jpg>) downscaled to 640×427 (`sips -Z 640 -s formatOptions 85`), with that attribution and "downscaled from 1080×720" in `NOTICE`.

- [ ] **Step 1:** Copy the model (verify its SHA-256 with `shasum -a 256`), the photo and the spike's `lib.rs` as a starting point into `NAT/`; `Cargo.toml`: package `hs_face`, `crate-type = ["cdylib", "rlib"]`, `tract-onnx = "=0.23.10"`, dev-dependency `image = { version = "0.25", default-features = false, features = ["jpeg"] }`; `[profile.release]` as the spike's for now.
- [ ] **Step 2: Write the failing tests.** Unit (`decode.rs`): `decodes_one_anchor` — a 64×64 padded input, all-zero tensors except stride 8 anchor row 2 col 3 with cls 0.81, obj 1.0, bbox `[0.5, 0.5, ln 2, ln 3]`, kps all 0.25: one face, score 0.9 (±1e-6), box `[20, 8, 16, 24]`, first landmark `(26, 18)`; with `min_score` 0.95 none. `nms_keeps_the_better_of_two_overlapping` (IoU 0.5 → 1 kept, the higher score), `nms_keeps_both_at_iou_0_3_or_less`. `letterbox_keeps_the_aspect`: `(1080, 720, 320)` → dw 320, dh 213, pw 320, ph 224; `(720, 1080, 320)` → 213, 320, 224, 320; `(100, 50, 640)` → 640, 320, 640, 320; `(1, 1, 64)` → 64, 64, 64, 64. ABI (`tests/abi.rs`): `abi_version_is_1`; `face_is_60_bytes` (`size_of::<Face>() == 60`, `align_of == 4`); `close_null_is_a_no_op`; each refusal above returns −1 with its text in `hs_face_last_error`, and a following valid call clears it (null); `finds_faces_in_the_group_photo` — decode `group.jpg`, detect at 640 and at 320 with `min_score` 0.7 and `max_faces` 64: at 640 at least 7 faces, at 320 at least 6; every box within the picture ±5 %, every landmark within its box grown by 20 %, every score ≥ 0.7, sorted by score; record the counts found (the builder pins them as `== N640` / `== N320` once measured and writes N320 into the report: Task 8 uses it); `plans_are_cached_per_shape` — 640, 320, 640 again give identical faces; `stride_padding_changes_nothing` — the rows copied 16 bytes apart give identical faces; `max_faces_caps_and_keeps_the_best` — `max_faces` 2 writes the 2 best; `blank_finds_nothing` — all-black 640×480 → 0; `tiny_and_extreme_sizes_find_nothing` (Review Focus 3) — 1×1, 20×20 and 8192×8 → 0, no error; `concurrent_detects_agree` — 4 threads × 10 detects on one detector all equal the single-thread result.
- [ ] **Step 3: Run** `(cd NAT && cargo test --release)`: FAIL to compile.
- [ ] **Step 4: Implement** the modules; then `cargo test --release` PASS and `cargo build --release --lib` builds `target/release/libhs_face.dylib` on the Mac.
- [ ] **Step 5: Commit** — `hs_face, a Rust library on tract with YuNet embedded, finds faces with five landmarks through a small C ABI.`

---

### Task 5: The library is slimmed towards 8 MiB

**Files:**
- Modify: `NAT/Cargo.toml` (profile, features), `NAT/src/detector.rs` (model loading)
- Create, only if NNEF holds: `NAT/src/bin/convert.rs` (behind a `convert` feature), `NAT/model/yunet.nnef.tgz`
- Test: `NAT/tests/abi.rs`, `NAT/tests/nnef.rs` (if NNEF is attempted)

**Interfaces:**
- Consumes/keeps: the C ABI of Task 4 exactly.

**Behaviour:**
- Measure a stripped `linux-musl-arm64` `cdylib` (built as Task 6's `build.sh` will: `rust:1.99.1-alpine`, `--platform linux/arm64`, sources tar-streamed, `RUSTFLAGS="-C target-feature=-crt-static"`) after each lever, in a table in the task report: (a) baseline as Task 4; (b) `opt-level = "s"`, then `"z"`; (c) `lto = "fat"`, `codegen-units = 1`, `strip = true`; (d) `default-features = false` on tract crates where it still builds; (e) NNEF: convert the ONNX offline to tract NNEF with symbolic H and W (`convert` binary, `tract-onnx` only there), embed `yunet.nnef.tgz`, link only `tract-nnef` in the library, and per shape concretize H/W and optimise. (f) `panic = "abort"` only if still above 8 MiB after (a)–(e), and then each export checks its inputs so nothing it controls panics; say so in the report.
- NNEF holds only if `nnef_matches_onnx` passes: on `group.jpg` at 320 and 640, the same number of faces as the ONNX path, each box and landmark within 0.5 px and each score within 1e-3 of it (the ONNX path kept behind a dev feature for this test). If symbolic H/W does not convert, or the test fails, ONNX stays and the NNEF files are not committed.
- Report the final size. Below 8 MiB: done. Above: report the table and go on; the owner decides later (Ruling 4).
- Every Task 4 test still passes, with its pinned counts unchanged.

- [ ] **Step 1:** Measure (a)–(d), keeping each change that does not break `cargo test`. **Step 2:** Attempt (e) with `nnef_matches_onnx` written first; keep or drop it by the test. **Step 3:** (f) only if needed. **Step 4: Run** `cargo test --release --locked`: PASS. **Step 5: Commit** — `hs_face builds at <size> MiB: <the levers kept>.`

---

### Task 6: `build.sh` builds every RID in Docker, and the binaries are committed

**Files:**
- Create: `NAT/build.sh` (executable), `NAT/bin/libhs_face-<rid>.so` for each RID built, `NAT/bin/SHA256SUMS`

**Interfaces:**
- Produces: `NAT/build.sh [--check] [RID ...]` — no RIDs: every RID of Global Constraints; `--check` builds into a temporary directory and leaves `bin/` alone. Exit status non-zero on any failure. This commit's sha is the `<commit-sha>` Task 8 pins.

**Behaviour:**
- Images: musl `rust:1.99.1-alpine`; gnu `rust:1.99.1-bullseye` (glibc 2.31) — if that tag does not exist, `debian:bullseye` with rustup installing 1.99.1 (minimal profile); x64 with `--platform linux/amd64` (Docker's emulation on the arm64 host), arm64 `linux/arm64`, arm `linux/arm/v7`. Before building x64 it checks `docker run --rm --platform linux/amd64 alpine:3.22 uname -m` prints `x86_64`, else stops with `enable amd64 emulation (colima: colima start --vz-rosetta, or install binfmt)`.
- Sources go in as a tar stream on stdin (`Cargo.toml Cargo.lock rust-toolchain.toml src model`), with `COPYFILE_DISABLE=1` and `--no-xattrs --no-mac-metadata` when `tar --version` says bsdtar; the `.so` comes back on stdout. No bind mounts.
- In the container after `cargo build --release --locked --lib`: gnu — the highest `GLIBC_x.y` in `objdump -T` is ≤ 2.31, else fail; musl — `readelf -d` NEEDED is only `libc.musl-*.so.1` and `libgcc_s.so.1`, else fail (`apk add binutils` for the tools).
- Smoke each musl library in the image it runs in: stream it into `homeassistant/home-assistant:2026.11.1` (`--platform` of the RID) and `python3 -c "import ctypes,sys; print(ctypes.CDLL(sys.argv[1]).hs_face_abi_version())"` prints `1`.
- Prints each file's size; rewrites `SHA256SUMS` (`sha256sum`, or `shasum -a 256` on macOS) for the files built. An arm RID that fails is reported as skipped, not an error, and is left out of `SHA256SUMS`.
- Header comment: what it builds, the prerequisites (a Docker engine, amd64 emulation for the x64 RIDs; nothing installed on the host), and that binaries are rebuilt only when the Rust code changes.

- [ ] **Step 1:** Write `build.sh`; run `./build.sh --check linux-musl-arm64`: builds, checks, smokes, prints the size.
- [ ] **Step 2:** Run `./build.sh` (all RIDs; emulated x64 builds take long: run it in the background). Expected: four or six `.so` files, `SHA256SUMS` lines for each, every check and smoke passing.
- [ ] **Step 3:** `(cd NAT/bin && shasum -a 256 -c SHA256SUMS)`: all OK. `git status` shows only `NAT/build.sh` and `NAT/bin/*`.
- [ ] **Step 4: Commit** (binaries commit) — `The face detection library is built in Docker for every Linux runtime and committed with its SHA-256s.` Record `git rev-parse HEAD` in the report: Task 8 pins it.

---

### Task 7: CI builds and tests the library

**Files:**
- Modify: `.github/workflows/tests.yaml` (a fourth job, `native`)

**Behaviour:**
- Job `native`, name `Native face detection (cargo test, musl build check)`, `runs-on: ubuntu-latest`, `actions/checkout@v4` with `fetch-depth: 0` (Task 8's pin check needs the binaries' commit). Steps: install the toolchain `rust-toolchain.toml` names (`rustup show`); `cargo test --release --locked` in `nodes/face_detection/native`; `./build.sh --check linux-musl-x64` (Docker on the runner, native x64); `(cd nodes/face_detection/native/bin && sha256sum -c SHA256SUMS)`. It never commits or uploads binaries.
- A comment above the job says it checks the Rust code builds and its tests pass, and that binaries are built by hand with `build.sh` and committed.
- The workflow still runs on push, pull request, dispatch and call; `release.yaml` needs nothing new.

- [ ] **Step 1:** Add the job. **Step 2:** Check it locally as far as possible: `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/tests.yaml'))"`, and the job's commands run from the repository root on the Mac (with `--check linux-musl-x64` under emulation). **Step 3: Commit** — `CI tests the face detection library and checks that it builds for musl.`

---

### Task 8: The face node, `FaceDetection.cs`, pinned to the binaries

**Files:**
- Create: `FD/FaceDetection.cs` (library source: namespace contents, wrapped in `NodeLib` when saved), `tests/Runtime/FaceDetectionTests.cs` (suite `--face-detection`, `Order = 41`), `tests/test_face_detection_pins.py`
- Modify: `.github/workflows/tests.yaml` (`native` job: run the pin check)

**Interfaces:**
- Consumes: `NodeSetupAttribute`, `NodeSetupContext` (Task 2), `DrawCircle` (Task 1), the C ABI v1 (Task 4), the binaries' commit sha and `SHA256SUMS` (Task 6).
- Produces, in `FD/FaceDetection.cs`:
  - `public static unsafe class FaceDetection`
  - `const string Source = "https://raw.githubusercontent.com/dompagoj/hass-sharp/<commit-sha>/nodes/face_detection/native/bin/";` on one line, exactly that form
  - `static readonly Dictionary<string, string> Sha256 = new(StringComparer.Ordinal)` whose entries (`["<rid>"] = "<sha256>",`, one per line, the same as `SHA256SUMS`) sit between the lines `// pinned:begin` and `// pinned:end` (the tests rewrite between the markers)
  - `[NodeSetup] public static async Task Setup(NodeSetupContext context)`
  - `[Node("Detect faces", Category = "Vision", Pure = true)] [return: Port("Image")] public static SharpImage DetectFaces([Port("Image")] SharpImage image, [Port("Minimum score")] double minScore = 0.7, [Port("Input size")] int inputSize = 320)`, documented (`<summary>`, each `<param>`, `<returns>`)
  - a private `sealed class Detector : IDisposable` holding the native pointer and the resolved exports, with `readonly Lock gate = new();`

**Behaviour:**
- Setup: no entry for `context.RuntimeIdentifier` → `PlatformNotSupportedException($"Detect faces has no native library for {rid}; it runs on {rids joined as 'a, b and c'}")`. Else `context.Files.Download(Source + $"libhs_face-{rid}.so", sha, $"libhs_face-{rid}.so", context.Stopping)`, `NativeLibrary.Load(path)` (never freed: the same path loads once per process, and every definition shares it), the five exports by `NativeLibrary.GetExport` as `delegate* unmanaged` (pattern `CC/HassSharp/Devices/Imaging/TurboJpeg.cs:33–52`); `hs_face_abi_version() != 1` → `InvalidOperationException($"The face detection library is version {got}, not 1: the download does not match this code")`; `hs_face_open()` null → `$"The face detection library could not start: {last error}"`. Then `context.Own(detector)`, store it in a static, and log `Face detection ready ({rid})`. `Dispose` logs `Face detection closed` at Debug.
- DetectFaces: `minScore` outside 0–1 → `ArgumentException("Minimum score is from 0 to 1")`; `inputSize` not a multiple of 32 in 64–1280 → `ArgumentException("Input size is a multiple of 32 from 64 to 1280")`; no detector → `InvalidOperationException("Detect faces is not set up: it works in an entity definition, once the definition has started")`; a closed one → `InvalidOperationException("Detect faces has stopped with its definition")`. Reads the pixels with `image.CopyPixels` as RGB into an `ArrayPool<byte>.Shared` buffer (returned in `finally`), calls `hs_face_detect` (stride `width * 3`, `max_faces` 64) under the detector's lock; −1 → `InvalidOperationException($"Face detection failed: {last error}")`. Draws on `image.Copy()`; the input is left as it was.
- Drawing per face, at most `(500 − copy.Drawings) / 7` faces, the best first (Review Focus 2): `DrawRect` green, thickness `max(2, round(min(w, h) / 50))`; a filled circle per landmark, radius `max(2, round(w / 40))`, right eye `SharpColor.Blue`, left eye `SharpColor.Red`, nose tip `SharpColor.Yellow`, right mouth corner `SharpColor.FromRgb(255, 0, 255)`, left mouth corner `SharpColor.Orange`; the score as `score.ToString("0.00", CultureInfo.InvariantCulture)` with `DrawText` white, size 16, background, at `(x, max(0, y − 20))`.
- `Detector.Dispose` takes `gate`, closes, zeroes the pointer and clears the static if it is still this one; `Detect` checks the pointer under `gate` (Review Focus 1).

- [ ] **Step 1: Pin.** Fill `Source` with Task 6's sha and the table from `NAT/bin/SHA256SUMS`.
- [ ] **Step 2: Write the failing tests.**
  - `tests/test_face_detection_pins.py`: the table between the markers equals `SHA256SUMS` line for line; each committed `.so` hashes to its entry; the URL's sha is 40 hex digits and, when `git cat-file -e <sha>` finds it, `git show <sha>:nodes/face_detection/native/bin/libhs_face-<rid>.so` hashes to the same (skipped with a message when the commit is absent, as in a shallow checkout).
  - `FaceDetectionTests`, direct: compile `FD/FaceDetection.cs` (wrapped with `NodeLibraryCompiler.WrapSource`, with `DiagnosticsProvider.GlobalUsings`, `FrameworkReferences()` and `allowUnsafe`, as the library compiler builds it) into a collectible `ScriptLoadContext`, and call `Setup` with a `NodeSetupContext` over a `SharpFiles` on a `LoopbackServer` and a list that collects `Own`. The local library: `NAT/bin/libhs_face-<RuntimeInformation.RuntimeIdentifier>.so` when committed (CI's `linux-x64`), else `NAT/target/release/libhs_face.dylib` / `.so` (a host `cargo build`); none → print `SKIP face detection: no hs_face library for <rid>` and skip only the tests that need it. The source is rewritten: `Source` to the loopback URL, the table to `["<rid>"] = "<sha of the local file>"`.
    - `UnsupportedPlatformFailsClearly` (always): table `["linux-riscv64"] = <any sha>` → `Detect faces has no native library for {rid}; it runs on linux-riscv64`, no request made.
    - `AWrongChecksumNeverLoads` (always): served bytes not matching → `InvalidDataException` from `Download`, nothing loaded, no file at the path.
    - `NotSetUpThrows` (always): `DetectFaces` before `Setup` throws the not-set-up sentence; `minScore` 1.5 and `inputSize` 100 throw their sentences.
    - `FindsTheFacesOfTheFixture` (library): `group.jpg` at 320 → result `Drawings == 7 * N320` (N320 from Task 4), the input's `Drawings` still 0, and the result's JPEG has pixels near green where the input has none in the first box's top edge.
    - `AYuvFrameFindsTheSameFaces` (library, Review Focus 3): the fixture read as YUV 4:2:0 (`CopyPixels(..., SharpPixelFormat.Yuv420)` → `SharpImage.FromPixels`) gives the same `Drawings` count.
    - `DrawsOnlyWhatFitsUnderTheDrawingLimit` (library, Review Focus 2): the fixture with 495 drawings already → no exception, result `Drawings ≤ 500`.
    - `DisposeWaitsForADetectAndLaterCallsFail` (library, Review Focus 1): 4 threads detecting in a loop while the owned detector is disposed: no crash, each later call throws `Detect faces has stopped with its definition`.
  - `FaceDetectionTests`, end to end (library): through `HassSharpManager`, save the rewritten `FaceDetection.cs` as library `face_detection` and a second library `fixture` with `[Node("Fixture photo", Pure = true)] SharpImage Photo()` (loads `group.jpg`) and `[Node("Drawings", Pure = true)] int Drawings(SharpImage image)`; a definition whose When started flow sets sensor `drawn` from Drawings(Detect faces(Fixture photo)): running, sensor `drawn` == `7 * N320`; the log has `Downloading libhs_face-<rid>.so from 127.0.0.1` once and `Face detection ready (<rid>)`; a resave runs again with the server's count still 1 and `Face detection closed` logged once at Debug.
- [ ] **Step 3: Run** `-- --face-detection` and `python3 -m unittest tests.test_face_detection_pins -v`: FAIL (no source yet).
- [ ] **Step 4: Implement** `FD/FaceDetection.cs`; add `python3 -m unittest tests.test_face_detection_pins -v` to the `native` CI job.
- [ ] **Step 5: Run** every suite, on the Mac with `(cd NAT && cargo build --release --lib)` done first so the library tests run, not skip; report which ran.
- [ ] **Step 6: Commit** (pin commit) — `Detect faces, a node library for entity definitions, downloads the committed face detection library pinned by commit and checksum and draws each face's box, landmarks and score.`

---

### Task 9: Documentation

**Files:**
- Modify: `docs/node-libraries.md` (a section `## Setting up a node class` between "Node libraries" and "Entity classes"), `docs/custom-entities.md` ("Cameras in C#" at :932–944), `docs/backend/entity-definitions.md` (the set-up order at :254–261), `AGENTS.md` (the suites list and a line on `build.sh`)
- Test: `tests/test_docs_examples.py`

**Behaviour:**
- `node-libraries.md`: `[NodeSetup]` on a `public static Task Setup(NodeSetupContext context)`; runs once when an entity definition whose graph uses any `[Node]` of the class starts, before its When started flow, again on every restart, resave and library save (each definition has its own copy of the class's statics); shares the 60 seconds of the C# nodes' `Setup()`, not counting a download's receiving; a failure fails the definition's start with `{definition}: {class}: {message}`; the class's nodes work only in entity definitions (the editor leaves them out of automations); `NodeSetupContext`'s five members, `Own` disposing on stop; at most one per class. A short example (download a file with `Files.Download`, keep a handle with `Own`), and a pointer to `nodes/face_detection/FaceDetection.cs` as the complete one.
- `custom-entities.md` "Cameras in C#": `DrawCircle(x, y, radius, color, filled, thickness)` beside `DrawRect` and `DrawText`, with its limits (radius 1–4096, line 1–50, inward, outline by default).
- `entity-definitions.md`: the order becomes the graph's own entities, the node classes' `[NodeSetup]`s, every C# node with its `Setup()`, the When started flow; setups and C# nodes share the set-up time.
- `AGENTS.md`: add `(cd nodes/face_detection/native && cargo test --release --locked)` to the suites, and one sentence: binaries are rebuilt with `nodes/face_detection/native/build.sh` (Docker) only when the Rust code changes, committed, and then pinned in `FaceDetection.cs` in a second commit.

- [ ] **Step 1:** Write. **Step 2: Run** `python3 -m unittest tests.test_docs_examples -v` and every suite. **Step 3: Commit** — `The docs describe [NodeSetup], DrawCircle and how the face detection library is built.`

---

### Task 10: Live check on the local instance

No product code. A failure found here goes back to its task as a fix with a test, then this task is run again. The spec's later live step, Add external with this repository's URL after the owner merges, is Project B's.

- [ ] **Step 1: Build and restart.** `dotnet publish -c Release ./CC/HassSharp -o ./CC/out`; `(cd CC/frontend && bun run build)`; `docker compose restart -t 60 hass`; `docker compose logs --tail=200 hass` shows HassSharp started without errors.
- [ ] **Step 2: Serve the binaries.** `python3 -m http.server 8765 --directory nodes/face_detection/native/bin` in the background. From the container: `docker exec hass wget -qO- http://host.docker.internal:8765/SHA256SUMS`. If the name does not resolve, try `host.lima.internal`; if neither, `docker cp` the `bin/` files into `/tmp/hs_face_bin` in the container and serve them there with `docker exec -d hass python3 -m http.server 8765 --directory /tmp/hs_face_bin`, using `http://127.0.0.1:8765/`. Use whichever answered as `<base>`.
- [ ] **Step 3: Paste a local copy.** In the panel (<http://localhost:5001>, HassSharp → Node libraries → Add file, name `FaceDetection.cs`), paste `FD/FaceDetection.cs` with only `Source` changed to `<base>/`; the table stays as committed. Save: valid, and Detect faces is listed under Vision in an entity definition's step library and not in an automation's.
- [ ] **Step 4: Wire `testcamera`.** Open the `testcamera` definition: front-door camera → Camera frame → Draw text → Draw rectangle → camera entity. Put Detect faces between Camera frame and Draw text (Camera frame's image → Detect faces' Image; its Image → Draw text's image). Save: running. If the front-door camera shows no faces, replace Camera frame with Video frame on a clip with faces: `docker cp nodes/face_detection/native/tests/fixtures/group.jpg hass:/media/group.jpg`, `docker exec hass ffmpeg -loop 1 -i /media/group.jpg -t 30 -r 10 -pix_fmt yuv420p /media/faces.mp4`, Source `/media/faces.mp4` (add `/media` to `media_dirs` or `allowlist_external_dirs` as `docs/custom-entities.md` "Videos" says, merging into `configuration.yaml` as AGENTS.md shows, if the frame says the folder is not allowed).
- [ ] **Step 5: Watch.** In Home Assistant, open the camera entity's more-info for at least 60 s: boxes, five coloured points and a score on each face, at a sustained frame rate (the badge reads Live; no "making a frame failed" lines in `docker compose logs --tail=200 hass`). Record the frame rate seen.
- [ ] **Step 6: One download.** `docker compose logs hass | grep -c "Downloading libhs_face-linux-musl-arm64.so from"` is `1`; `Face detection ready (linux-musl-arm64)` is logged.
- [ ] **Step 7: Reload, no re-download.** Settings → Devices & services → HassSharp → Reload (or `docker compose restart -t 60 hass`). After it: a new `Face detection ready` line, still exactly one `Downloading` line, the camera marks faces again. Optionally set `custom_components.HassSharp.memory: debug` (AGENTS.md "Leak probe") for one reload and save the library once more: resident memory and assemblies do not grow per reload beyond what other definitions do.
- [ ] **Step 8: Tidy.** Stop the HTTP server(s) (and remove `/tmp/hs_face_bin` from the container if used). `testcamera` stays wired; its library file stays cached under `.storage/hass_sharp/files/`. Report: frame rate, log lines, anything odd. No push.

---

## Rulings

1. **`[NodeSetup]` classes are entity-only through the catalog's `documents`.** Both validators, the palette and the clipboard already read it, so "only in a custom entity definition" needs no new code path or message: the existing `node.document` sentence ("… only works in the graph of an entity definition; delete it from this automation") is used. Cost if wrong: a dedicated code and sentence later.
2. **The generated `SharpNodeSetups` lists only the classes the graph uses**, after `SharpNodes`, and only when there is one: every existing graph's source stays byte-identical. Cost if wrong: none for pins; a different emitted shape is one method to rewrite.
3. **Setups run before the C# nodes, on their clock.** The spec says "on the same `SetupClock` as C# nodes' `Setup()`"; running them first lets a C# node use what a setup loaded. Cost if wrong: an order swap.
4. **`panic = "unwind"` with `catch_unwind` by default; `abort` only if needed for size**, contrary to the spec's size list. A tract panic under `abort` kills Home Assistant; the size gain is small. Over 8 MiB after every other lever, Task 5 reports and the build goes on rather than stopping. Cost if wrong: a larger binary in history.
5. **The ONNX model is committed** (232 KB, MIT, with its licence), not downloaded by the build: offline, reproducible Docker builds. NNEF, if it holds, is derived from it and committed beside it.
6. **The fixture photo is the spike's Wikimedia image (CC BY 2.0, Steve Evans), downscaled to 640×427**, attributed in `NAT/NOTICE`. Commons carries a personality-rights notice for the people shown; it is a test fixture, not shown in the product. Owner to confirm, or swap for a public-domain photo (only the pinned counts change).
7. **`DrawCircle(x, y, radius, color, filled = false, thickness = 2)`.** The spec's order with an outline's thickness added last, as `DrawRect` has it; an outline by default, as a rectangle is. Radius 1–4096.
8. **Error sentences and landmark colours** are this plan's (Tasks 2, 3, 8); the spec left them to the builder.
9. **`input_size` is 64–1280, a multiple of 32; at most 64 faces a call; at most 4 cached plans.** Bounds memory and time per frame. Cost if wrong: constants.
10. **`NativeLibrary.Free` is never called.** The library stays loaded for the process; each definition's detector is closed on stop. Unloading a Rust `cdylib` with thread-locals is the riskier path.
11. **A face-detection library test compiles `FaceDetection.cs` directly** (a collectible context, a hand-made `NodeSetupContext`) besides the end-to-end one, so concurrency and drawing limits can be tested without a camera.
12. **AGENTS.md gains the cargo suite and the two-commit rebuild rule.** It is the project's list of suites. Cost if wrong: two lines to drop.
