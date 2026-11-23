# Face Recognition in the People Detector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The People detector recognises people: it embeds clear faces with SFace, matches them against one shared people list, groups the faces it does not know for the user to name on its page, and learns further looks of the people it knows.

**Architecture:** The native library gains a recogniser (align a face from its five landmarks, run SFace, return a unit vector); the model is a pinned download, shared per process. Everything else is C# in the node libraries: `Vision.cs` opens the recogniser behind an interface, a new `People.cs` holds the shared people list, the matcher, the unknown-face groups and the learning rule, and `PeopleDetector.cs` runs them in its check and draws the page. The rules take their thresholds as one record, so tests use made-up embeddings and the defaults come from a calibration run on LFW.

**Tech Stack:** Rust (tract-onnx 0.23.10) in `native/`; C# node libraries in `nodes/`, tested by `tests/Vision.Tests` against a hass-sharp checkout; Python pin and manifest tests in `tests/`.

**Spec:** `docs/superpowers/specs/2026-11-23-face-recognition-design.md` (in hass-sharp). The work is in `/Users/dompa/dev/hass-sharp-vision`, on a branch `face-recognition`; this plan and its ledger live in the hass-sharp worktree.

## Global Constraints

- Work in `/Users/dompa/dev/hass-sharp-vision` on branch `face-recognition`. Never commit on `main`, never push, tag or release.
- The C# tests build against the hass-sharp worktree `/Users/dompa/dev/HassSharp/.claude/worktrees/library-class-nodes` (`-p:HassSharpRoot=…`); never edit that checkout from this plan.
- The model: `face_recognition_sface_2021dec.onnx`, 38,696,353 bytes, SHA-256 `0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79`. It is never committed and never embedded in the library.
- Model input: 1×3×112×112 f32, NCHW, RGB planes, values 0–255 (the model normalises itself). Output: 128 floats; the recogniser returns them normalised to length 1.
- Alignment: a similarity transform (no reflection) from the five landmarks, in the order hs_vision returns them (right eye, left eye, nose tip, right mouth corner, left mouth corner), onto (38.2946, 51.6963), (73.5318, 51.5014), (56.0252, 71.7366), (41.5493, 92.3655), (70.7299, 92.2041); bilinear warp to 112×112, black outside the picture, from the full picture. The spike's `align.rs` (`/private/tmp/claude-501/-Users-dompa-dev-HassSharp/92c2fcca-d7d3-4576-819b-a3af01129b90/scratchpad/sface-spike/spike/src/align.rs`) is the reference.
- ABI version 2. Every export of version 1 keeps its name, signature and behaviour.
- The shared store is `Shared("hass-sharp-vision.people")`. Limits: 20 named and 20 learned samples per person, 200 people, 50 unknown groups per detector, 5 samples per group, 4 faces embedded per check.
- No face crop, embedding or name is written to a log line, sent anywhere, or committed. Datasets, footage and embeddings used for calibration or tests stay outside the repository, except the test photos already in it.
- Every new setting of the People detector has a default.
- A lock is a `System.Threading.Lock` field taken with `lock`, never across an `await`.
- Messages a user can see are plain sentences. Commit messages are one plain sentence, no prefix, no trailer.
- Never use an `mcp__hass__*` tool. Never touch the containers `hass`, `hass-dummy-camera` or `hass_wt_stale_entity_nodes`. Read diffs with `git --no-pager diff --no-ext-diff`.
- Suites: `(cd native && cargo test --release --locked)`; `dotnet run --project tests/Vision.Tests --disable-build-servers -p:HassSharpRoot=/Users/dompa/dev/HassSharp/.claude/worktrees/library-class-nodes -- <--suite …>`; `python3 -m unittest discover -s tests`.

## Review Focus

- **Two faces of one person in one frame** (a mirror, a photo on the wall, a false detection): both match the same person; the person is recognised once, and at most one of the two can become a learned sample in that check.
- **An empty people list:** every usable face goes to the unknown groups; nothing throws, and the page shows People as empty with its own sentence.
- **The shared store deleted or cleared while a detector runs** (Stored data dialog after a restart, or another detector's merge): the detector reloads an empty or changed list on `Changed` and keeps running; a card pressed for a person or group that is gone answers with a refusal, not an exception.
- **A name that differs only by case or spaces** from an existing one ("ivan " against "Ivan"): Save on an unknown group adds to that person; a rename to it is refused as taken.
- **A camera frame smaller than a face crop's source box, or a face at the picture's edge:** the crop is clamped to the picture; alignment fills outside with black; nothing throws.

---

### Task 1: The native recogniser

**Files:**
- Create: `native/src/align.rs`, `native/src/recognizer.rs`, `native/fetch-model.sh`, `native/model/LICENSE-sface`
- Modify: `native/src/lib.rs`, `native/Cargo.toml` (version `1.1.0`), `native/Cargo.lock`, `native/NOTICE`, `native/THIRD-PARTY-LICENSES` (through `build.sh --licenses` if a dependency changed, else by the same template), `.gitignore` (the model file), `.github/workflows/ci.yaml` and `release.yaml` (fetch the model before the tests; the release uploads it and `LICENSE-sface`)
- Test: `native/src/align.rs`, `native/src/recognizer.rs` unit tests; `native/tests/` as the crate's existing tests are laid out

**Interfaces:**
- Produces (C ABI, version 2):
  - `hs_vision_open_recognizer(model_path: *const c_char) -> *mut Recognizer` — null with the error set when the path is null, not UTF-8, missing, or not the model.
  - `hs_vision_embed(rec: *mut Recognizer, rgb: *const u8, width: i32, height: i32, stride: i32, landmarks: *const f32 /* 10 */, out: *mut f32 /* 128 */, fit: *mut f32 /* 1, may be null */) -> i32` — 0, or −1 with the error set. `fit` is the alignment's RMS residual in the 112×112 crop's pixels.
  - `hs_vision_close_recognizer(rec: *mut Recognizer)` — null is a no-op.
  - `ABI_VERSION = 2`.
- Produces (Rust, for Task 2's tool): `pub fn align(picture: &Picture, landmarks: &[f32; 10]) -> (Vec<u8> /* 112*112*3 RGB */, f32 /* fit */)` and `Recognizer::open(path) -> Result<Recognizer, String>`, `Recognizer::embed(&self, picture: &Picture, landmarks: &[f32; 10]) -> Result<([f32; 128], f32), String>`.
- `native/fetch-model.sh` downloads the model from the OpenCV zoo's Hugging Face mirror (`opencv/face_recognition_sface`) into `native/model/face_recognition_sface_2021dec.onnx` unless a file with the pinned SHA-256 is already there, and fails on a mismatch. A copy with the right SHA-256 is at `/private/tmp/claude-501/-Users-dompa-dev-HassSharp/92c2fcca-d7d3-4576-819b-a3af01129b90/scratchpad/sface-spike/` (find it by size); use it if it is still there instead of downloading again.

- [ ] **Step 1: Write the failing tests.**
  - `align.rs`: `the_reference_points_map_to_themselves` (landmarks equal to the five reference points on a 112×112 picture give the picture back within 1 grey level and a fit under 0.01); `a_scaled_rotated_face_aligns` (reference points scaled by 2, rotated 20° and shifted give a fit under 0.01); `a_profile_fits_badly` (both eyes 1.6 px apart: fit over 10); `outside_the_picture_is_black` (landmarks at the picture's corner: no panic, the crop has black pixels).
  - `recognizer.rs` / `native/tests`: `the_embedding_is_a_unit_vector_and_pinned` (a face of `tests/fixtures/group.jpg`, its landmarks from the detector at 640: length 1 within 1e-5, and its first four values pinned to what this build gives, within 1e-4); `one_model_file_is_planned_once` (two recognisers on the same path share one plan: opening the second takes under a tenth of the first's time, or assert through a counter the module exposes to tests); `closing_the_last_frees_and_reopening_works`; `a_missing_model_is_an_error` and `a_file_that_is_not_the_model_is_an_error` (the message is a plain sentence naming the path); `embedding_from_four_threads_gives_one_answer`.
  - `lib.rs`: `the_abi_is_version_2`; null recogniser, null pixels, null landmarks and null out each give −1 with their own message; a panic inside is an internal error.
- [ ] **Step 2: Run** `(cd native && ./fetch-model.sh && cargo test --release --locked)`. Expected: FAIL to compile (no such modules).
- [ ] **Step 3: Implement.** The plan for a model path is kept in a process-wide map behind a `Mutex`, counted by its recognisers and dropped with the last; `embed` runs on the shared plan without holding that mutex. The picture's arguments are checked as `picture()` checks them (without input size and score).
- [ ] **Step 4: Run** the tests again, and `cargo clippy --release --locked -- -D warnings` if CI runs it. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "hs_vision 1.1.0 turns a face into an SFace embedding: it aligns the face from its five landmarks, runs the model from a file kept outside the library and shares its plan across recognisers."`

### Task 2: Calibration on LFW

**Files:**
- Create: `native/examples/calibrate.rs`, `docs/calibration.md`
- Modify: `native/Cargo.toml` (dev-dependencies only if needed for reading a directory of JPEGs; `image` is there)

**Interfaces:**
- Consumes: Task 1's `Recognizer`, `align`, and the crate's `Detector`.
- Produces: `docs/calibration.md` with a section **Defaults** holding exactly these names and their measured values, which Tasks 4 to 6 copy: `UsableEyes` (px), `LearnEyes` (px), `FitLimit` (px), `MatchScore`, `MatchMargin`, `SuggestScore`, `LearnScore`, `LearnMargin`; and a section **Verdict**: `PASS`, or `STOP` when no size up to 40 px between the eyes reaches 8 in 10.

- [ ] **Step 1:** Download LFW (the original, not the funnelled or deep-funnelled variant if both are offered; about 13,233 images of 5,749 people, about 170 MB) into a directory outside the repository, under `/private/tmp/claude-501/-Users-dompa-dev-HassSharp/92c2fcca-d7d3-4576-819b-a3af01129b90/scratchpad/lfw/`, from a mirror that publishes a checksum (scikit-learn's figshare copy is one). Record the URL, size and SHA-256 in `docs/calibration.md`. Download nothing else.
- [ ] **Step 2: Write `calibrate.rs`** (`cargo run --release --example calibrate -- <lfw dir> <model>`), seeded and reproducible:
  - For each image: the detector at 640, minimum score 0.7; the face nearest the centre; skip an image with none. Record the eye distance and the fit.
  - For each target eye distance in 40, 32, 24, 20, 16, 12, 10 px: scale the picture and the landmarks down to it (never up; an image whose face is smaller is skipped at that size) and embed.
  - **Pairs** per size: every same-person pair among at most 10 images a person; 1,000,000 different-person pairs sampled with a fixed seed. Print for each size the score quantiles of both, the threshold at which at most 1 in 1,000 different-person pairs match, and the share of same-person pairs at or above it.
  - **Mixed sizes:** the same with one image at 40 px and the other at each smaller size.
  - **Galleries** at the usable size: 200 seeded galleries of 20 people with 3 named images each, drawn from people with at least 6 images; probes are the enrolled people's other images and images of 200 people not enrolled. For margins 0, 0.05, 0.1, 0.15, 0.2: the share of genuine probes recognised as the right person, and of all probes recognised as a wrong person.
  - **Fit:** the fit's quantiles over all faces, and over the faces whose best same-person score is below the match threshold.
- [ ] **Step 3: Run it** and write `docs/calibration.md`: the tables, then **Defaults** by these rules: `UsableEyes` = the smallest size at which at least 8 in 10 same-person pairs match at that size's 1-in-1,000 threshold; `MatchScore` = that threshold rounded up to 0.05; `MatchMargin` = the smallest margin at which at most 1 in 1,000 probes is recognised as a wrong person at `MatchScore`; `SuggestScore` = the threshold for 1 in 100, rounded to 0.05; `LearnScore` and `LearnMargin` = the same as match for 1 in 10,000; `LearnEyes` = the smallest size at which at least 8 in 10 same-person pairs reach `LearnScore`, and no less than `UsableEyes` + 8; `FitLimit` = the fit's 99th percentile over the faces that matched, rounded up. Then **Verdict**, and three plain sentences on what recognition can and cannot be trusted with at a door camera.
- [ ] **Step 4:** Check the output is reproducible (a second run prints the same tables).
- [ ] **Step 5: Commit** the tool and `docs/calibration.md` only: `git commit -m "A calibration tool measures SFace's same-person and different-person scores by face size on LFW, and its results give the recognition defaults."`

### Task 3: `Vision.cs` opens the recogniser

**Files:**
- Modify: `nodes/Vision.cs`
- Test: `tests/Vision.Tests/VisionTests.cs` (`--vision`)

**Interfaces:**
- Consumes: Task 1's exports.
- Produces:
  - `public interface IFaceRecognizer : IDisposable { FaceEmbedding Embed(SharpImage image, DetectedFace face); }`
  - `public readonly record struct FaceEmbedding(float[] Values /* 128, length 1 */, float Fit)` with `public float Similarity(FaceEmbedding other)` (the dot product).
  - `public sealed class VisionRecognizer : IFaceRecognizer` (its calls and its close behind one `Lock`, as `VisionDetector`).
  - `public static Task<VisionRecognizer> Vision.OpenRecognizerAsync(SharpFiles files, string runtimeIdentifier, CancellationToken cancellation)`: downloads the library (as `OpenAsync`) and the model (name `face_recognition_sface_2021dec.onnx`, from `Source`, by the pinned SHA-256), then opens.
  - `public static float Vision.EyeDistance(DetectedFace face)` and `public static bool Vision.Frontal(DetectedFace face)` (false when the eyes are closer than a fifth of the box's width, or the nose tip is not between the eyes horizontally).
  - `public static SharpImage Vision.DrawFaces(SharpImage image, IReadOnlyList<DetectedFace> faces, IReadOnlyList<string?> names)`: as `DrawFaces`, with a face's name in place of its score where it has one.
  - `AbiVersion = 2` for the whole library.

- [ ] **Step 1: Write the failing tests** in `VisionTests.cs`: a real recogniser on a face of `group.jpg` gives 128 values of length 1 and the same answer twice (skipped without a local library or model, as the detector's tests are; the model is found through `HS_VISION_MODEL`, else `native/model/`); `Similarity` of a vector with itself is 1; `EyeDistance` and `Frontal` on the eight faces of `group.jpg` (the profile is not frontal, the other seven are); `DrawFaces` with names draws the name and falls back to the score; a library of ABI 1 is refused with the existing sentence; `Embed` after `Dispose` throws `ObjectDisposedException`; a model download with a wrong checksum fails as the library's does.
- [ ] **Step 2: Run** `-- --vision`. Expected: FAIL to compile.
- [ ] **Step 3: Implement.** `Exports` gains the three functions.
- [ ] **Step 4: Run** `-- --vision`. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Vision.cs downloads the SFace model and opens a recogniser that turns a detected face into an embedding, with helpers that say how large and how frontal a face is."`

### Task 4: The people list and the matcher (`People.cs`)

**Files:**
- Create: `nodes/People.cs`
- Modify: `tests/Vision.Tests/Vision.Tests.csproj` only if the node files are listed there
- Test: `tests/Vision.Tests/PeopleTests.cs` (new suite `--people`, `Order = 43`)

**Interfaces:**
- Consumes: `FaceEmbedding`; HassSharp's `SharpData` (`Get`, `Set`, `GetBytes`, `SetBytes`, `Remove`, `Keys`, `Update`, `Changed`).
- Produces:
  - `public sealed record RecognitionRules(double MatchScore, double MatchMargin, double SuggestScore, double LearnScore, double LearnMargin, double UsableEyes, double LearnEyes, double FitLimit)` with `public static RecognitionRules Default` holding `docs/calibration.md`'s Defaults.
  - `public sealed record Person(string Id, string Name, DateTimeOffset Added, IReadOnlyList<FaceSample> Samples, DateTimeOffset? LastSeen, string? LastSeenAt)`; `public sealed record FaceSample(string Id, FaceEmbedding Embedding, DateTimeOffset At, string Camera, bool Learned)`.
  - `public sealed class PeopleList` over a `SharpData`: `IReadOnlyList<Person> People` (the in-memory copy), `Reload()`, `byte[]? Crop(string sampleId)`, `Person Add(string name, IEnumerable<NewSample> samples)`, `Person AddSamples(string personId, IEnumerable<NewSample> samples, bool learned)`, `Rename(string personId, string name)`, `Merge(string fromId, string intoId)`, `Delete(string personId)`, `RemoveSample(string personId, string sampleId)`, `Seen(string personId, DateTimeOffset at, string camera)` (written at most once a minute per person), `Person? Named(string name)` (case and surrounding spaces ignored). `public sealed record NewSample(FaceEmbedding Embedding, byte[] CropJpeg, DateTimeOffset At, string Camera)`. A refused change throws `PeopleRefusal(string message)`.
  - `public static class FaceMatcher`: `public static FaceMatch Match(FaceEmbedding face, IReadOnlyList<Person> people, RecognitionRules rules)`; `public sealed record FaceMatch(MatchKind Kind, Person? Person, double Score, double Margin)`; `public enum MatchKind { Unknown, Maybe, Recognised }`.
  - Store layout (fixed here so two versions agree): key `format` = 1; `names` = a JSON object from person id to name, the one key every create, rename, merge and delete goes through with `Update`; `people/<id>` = the person without embeddings and crops; `samples/<personId>/<sampleId>/embedding` = 512 bytes (128 little-endian floats); `samples/<personId>/<sampleId>/crop` = the JPEG.

- [ ] **Step 1: Write the failing tests** (`PeopleTests.cs`; embeddings are made up: unit vectors built from a seed, and vectors at a chosen cosine from another):
  - Matching: a face at 0.8 from one person's sample and 0.3 from everyone else's is `Recognised` with that person, score 0.8, margin 0.5; at `MatchScore` exactly it is recognised and just under it is `Maybe`; a face over `MatchScore` for two people within `MatchMargin` is `Maybe` for the better; under `SuggestScore` is `Unknown`; a person's score is their best sample's; an empty list gives `Unknown`.
  - The list: `Add` then `Reload` on a second `PeopleList` over the same store gives the same person, embeddings bit for bit and crops byte for byte; `Add` with a taken name (differing in case and spaces) throws `PeopleRefusal`; `Rename` to a taken name is refused and to its own name in another case works; the 21st named sample is refused and the 21st learned one replaces the oldest learned; the 201st person is refused; `Merge` moves samples up to the caps (named first), removes the source, and an interruption after the samples are written and before the source is removed leaves both readable; `RemoveSample` of the last named sample is refused; `Delete` removes the person's every key; `Seen` twice within a minute writes once.
  - Two lists on one store: both `Add` different people at once (16 tasks each) and no name is lost; `Changed` on one runs when the other writes; a list reloads to empty after `Clear()`.
  - No method writes a name or a byte of a crop to the log (the test host's log lines are read).
- [ ] **Step 2: Run** `-- --people`. Expected: FAIL to compile.
- [ ] **Step 3: Implement** `People.cs`, with the header comment line the other node files have.
- [ ] **Step 4: Run** `-- --people`. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "People.cs keeps one shared list of named people with their face samples and matches a face's embedding against it by best sample and margin."`

### Task 5: Unknown-face groups and the learning rule

**Files:**
- Modify: `nodes/People.cs`
- Test: `tests/Vision.Tests/PeopleTests.cs` (same suite)

**Interfaces:**
- Consumes: Task 4's types.
- Produces:
  - `public sealed class FaceGroups` over a `SharpData` (the detector's private `Data`): `IReadOnlyList<FaceGroup> Shown`, `Seen(FaceEmbedding face, Func<byte[]> crop, double eyes, DateTimeOffset now, string? suggestedPersonId)` (joins, starts or merges groups; one sighting a check per group), `Rematch(IReadOnlyList<Person> people, RecognitionRules rules)` (removes groups that now match someone), `Expire(DateTimeOffset now, TimeSpan keep)`, `FaceGroup? Find(string id)`, `Remove(string id)`, `IReadOnlyList<NewSample> Samples(string id)`, `byte[]? Crop(string groupId)`. `public sealed record FaceGroup(string Id, int Sightings, DateTimeOffset First, DateTimeOffset Last, string? SuggestedPersonId)`.
  - `public static bool FaceMatcher.Learns(FaceEmbedding face, double eyes, FaceMatch match, DateTimeOffset now, RecognitionRules rules)` — the five conditions of the spec's section 7; the person's last learned sample's time is read from their samples.
  - Constants: a group is shown at 3 sightings; an unshown group is dropped after 1 hour; 50 groups; 5 samples a group; "adds something" is a cosine under 0.9; one learned sample per person per 10 minutes.

- [ ] **Step 1: Write the failing tests**: a face starts a group; a second face at `MatchScore` or more from one of its samples joins it and one just under starts another; two groups that a new face is close to both are joined into the older; a group is shown at its third sighting and two sightings in one check count once; an unshown group is gone after an hour and a shown one after the keep time; the 51st group drops the one seen longest ago; a group keeps at most 5 samples, preferring larger faces and unlike ones; `Rematch` removes a group that now matches a person; a second `FaceGroups` over the same store reads the same groups; last seen and count are written at most once a minute (count the store's writes). `Learns`: one test per condition failing alone, one where all hold, and one where the only sample over `MatchScore` is a learned one (not learned).
- [ ] **Step 2: Run** `-- --people`. Expected: FAIL to compile.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `-- --people`. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "People.cs groups the faces nobody matches, shows a group after three sightings and lets a detector learn a further look of a person it is sure of."`

### Task 6: The People detector recognises

**Files:**
- Modify: `nodes/PeopleDetector.cs`
- Test: `tests/Vision.Tests/PeopleDetectorTests.cs` (`--people-detector`)

**Interfaces:**
- Consumes: Tasks 3 to 5.
- Produces: settings, in this order after `PersonScore`: `DetectPeople` ("Detect people", true), `RecogniseFaces` ("Recognise faces", true), `MatchScore` ("Match score", 0.3 to 0.9, default `RecognitionRules.Default.MatchScore`), `LearnNewLooks` ("Learn new looks", true), `KeepUnknownFacesFor` ("Keep unknown faces for", 1 to 365, default 7), `NamesOnTheCamera` ("Names on the camera", true). `protected virtual Task<IFaceRecognizer> OpenRecognizer()` (the seam: it calls `Vision.OpenRecognizerAsync`; a test's derived class returns a fake). `protected PeopleList People` and `protected FaceGroups Unknown` for Task 7.

- [ ] **Step 1: Write the failing tests** (a derived test class returns a fake recogniser whose embedding of a face is decided by the face's place in the picture, from a table the test sets; the detector itself is the real one on the test photo, so these tests skip without a local library as the existing ones do):
  - with *Recognise faces* off: `OpenRecognizer` is never called, no store key is written, the entities behave as before;
  - with *Detect people* off: Person seen, People and Last person seen are unavailable, the person model is never run (the detector is asked through a counting wrapper or the camera picture has no person box), faces still work;
  - a face that is not usable (too small, a profile, a bad fit) is counted and drawn but never embedded;
  - at most 4 faces are embedded in a check, the largest first;
  - an unknown face becomes a shown group after three checks; a named person's face is recognised and `Seen` records the camera's entity id; a maybe is grouped with its suggestion;
  - the camera picture carries the recognised name (the drawing's text), and none with *Names on the camera* off;
  - with *Learn new looks* on, a face that meets the rule adds one learned sample; two such faces of one person in one check add one; with it off, none;
  - groups older than the keep time are gone after a check;
  - a recogniser that throws for one face fails that face only, logs once without any name, and the check's entities are still set;
  - `Stop()` disposes the recogniser; a restart keeps people and groups.
- [ ] **Step 2: Run** `-- --people-detector`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement.** Crops are cut from the frame with `SharpImage.Crop` around the face box grown by a quarter, clamped to the picture, and resized to 160 px on the long side. The stores are opened in `Start()` (they throw before it).
- [ ] **Step 4: Run** `-- --vision --people --people-detector`. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "The People detector recognises the clear faces of a checked frame against the shared people list, groups the ones it does not know, learns new looks, draws names on its camera, and can leave person detection off."`

### Task 7: The page

**Files:**
- Modify: `nodes/PeopleDetector.cs`
- Test: `tests/Vision.Tests/PeopleDetectorTests.cs`

**Interfaces:**
- Consumes: Task 6; HassSharp's `Ui.List(id, title, cards, attention, empty)`, `Ui.Remove(id)`, `UiCard` (`Image`, `Text`, `Field`, `Select`, `Action(label, run, confirm, style, validate)`), `UiRefusal`.
- Produces: lists `unknown` ("Unknown faces", attention, empty "No unknown faces."), `people` ("People", empty "Nobody is named yet."), and `samples` ("Samples of <name>", only after **Samples**), as the spec's section 8 draws them.

- [ ] **Step 1: Write the failing tests** (the page is read and pressed through the host's device page request, as hass-sharp's `DeviceUiHostTests` do):
  - the unknown list has one card per shown group, newest sighting first, with its crop, "Seen 3 times · last …", a required Name field offering the known names, **Save** (primary) and **Dismiss** (does not validate); a maybe has the name filled in and "Might be Ivan";
  - Save with a new name makes a person with the group's samples as named samples and removes the group; with a known name in another case adds to that person; Dismiss removes the group with the field empty;
  - the people list has one card per person by name, with "9 samples (3 learned) · last seen … at <camera's friendly name>", Name, and, only with two or more people, *Merge into* and **Merge**; **Save** renames; **Merge** asks ("Merge Ivan into Ana?") and moves; **Delete** asks ("Delete Ivan and their samples?") and removes; a rename to a taken name answers a refusal with a plain sentence;
  - **Samples** draws the samples list, named first then learned, newest first, each with **Remove**, and the first card with **Close**; removing the last named sample is refused; the list goes with Close and when the person is deleted;
  - a press for a group or person that is gone answers a refusal; Save on a group for a person who already has 20 named samples answers "… has no room for more samples" and keeps the group;
  - a change made by a second detector on the same store redraws this one's people list within two seconds;
  - with *Recognise faces* off the detector has no page;
  - the attention count is the number of shown groups.
- [ ] **Step 2: Run** `-- --people-detector`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement.** One `Lock` around drawing, as the guide's Snapshot namer has.
- [ ] **Step 4: Run** `-- --vision --people --people-detector`. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "The People detector's page lists the unknown faces to name or dismiss, the people to rename, merge and delete, and a person's samples to remove."`

### Task 8: A node of its own, the manifest and the guide

**Files:**
- Modify: `nodes/PeopleDetector.cs` (`[Node("People detector", Category = "Vision")]`, not abstract, a public constructor), `nodes/front_door_people.json` (the node of kind `library:people_detector:global%3A%3ANodeLib.PeopleDetector`, node id `front_door`, key `front_door`, its `Camera` argument, no `source` and no `signature`), `hass_sharp.toml` (a third `[[library]]`: id `people`, name `People.cs`, path `nodes/People.cs`, between Vision.cs and PeopleDetector.cs), `README.md`, the first-line version comment of the three node files (1.1.0)
- Test: `tests/Vision.Tests/ManifestTests.cs` (`--vision-manifest`), `tests/test_manifest.py`, `tests/Vision.Tests/PeopleDetectorTests.cs`

**Interfaces:**
- Consumes: hass-sharp's library class nodes (the worktree's HEAD).

- [ ] **Step 1: Write the failing tests:** the manifest imports three libraries in order and previews the definition valid; the catalog has the People detector's kind, labelled "People detector" in "Vision", with the eleven settings in order and only `Camera` required; a definition with that node runs the detector (one of Task 6's tests, run through the library node); a C# device node deriving `PeopleDetector` still works (an existing test, kept).
- [ ] **Step 2: Run** `-- --vision-manifest --people-detector` and `HASS_SHARP_ROOT=/Users/dompa/dev/HassSharp/.claude/worktrees/library-class-nodes python3 -m unittest discover -s tests`. Expected: FAIL.
- [ ] **Step 3: Implement,** and write the README: the People detector as a node; recognition (what it does, the page, the settings table, which faces are used, learning and its limits); `docs/calibration.md`'s defaults, its table by face size and its three sentences on trust, with the statement that recognition is a convenience and must not open a door; that the people list is in Home Assistant's backups and names on the camera are seen by every user; the model's source, licence and that its training data is not stated; building and testing with the model; the release steps with the model as an asset; the HassSharp release the files need (the first after 0.0.49-beta that has library classes as nodes).
- [ ] **Step 4: Run** all three C# suites and the Python tests. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "The People detector is a node of its own in the Vision category, Add external offers People.cs as a third library, and the README describes recognition."`

### Task 9: Builds, pins and the live check

**Files:**
- Modify: `native/SHA256SUMS`, `nodes/Vision.cs` (`Source` to `…/download/v1.1.0/`, the four pins, the model's pin)
- Scratch: under the plan's SDD workspace in the hass-sharp worktree

- [ ] **Step 1:** `native/build.sh` for all four runtimes; copy `native/dist/SHA256SUMS` to `native/SHA256SUMS` and the pins into `Vision.cs`. `python3 -m unittest discover -s tests` passes (without `HS_VISION_CHECK_RELEASE`: 1.1.0 is not released). Commit: `git commit -m "The pins of hs_vision 1.1.0's four libraries and of the SFace model."`
- [ ] **Step 2:** Start the throwaway Home Assistant `hass_wt` on port 5002 from the hass-sharp worktree's published build. Before the definition starts, put the built `libhs_vision-linux-musl-arm64.so` and the model where `SharpFiles` keeps downloads, under the names and checksums `Vision.cs` asks for, so nothing is fetched from a release that does not exist yet (read `SharpFiles.cs` for the place).
- [ ] **Step 3:** Give it two cameras: a stream of a moving clip of one person (build one from the dummy front door's stream by recording some seconds of `rtsp` from the `hass-dummy-camera` container's address without touching that container, or use any local clip), and a still of `native/tests/fixtures/group.jpg`.
- [ ] **Step 4:** Import the three libraries and a definition with a People detector node on each camera. Check, with evidence: unknown groups appear after a few checks; naming one makes a person; the name is drawn on the camera picture; a restart keeps people and groups; the second detector shows the same People list and recognises a person named on the first when shown the same face; a learned sample appears on the moving clip and can be removed; Merge and Delete work; with *Detect people* off the person sensors are unavailable; with both definitions off the Stored data dialog deletes the people; the log has no name and no image data; the time of a check with four faces.
- [ ] **Step 5:** Write the report with the evidence. Leave the container running for the controller's look.

## Waiting for the owner

Not part of any task: moving `HASS_SHARP_REF` in `ci.yaml` to a pushed hass-sharp commit that has library classes as nodes, pushing the branch, the tag `v1.1.0` and the release. After the release, its `SHA256SUMS` is compared with the pins as the README's release steps say.
