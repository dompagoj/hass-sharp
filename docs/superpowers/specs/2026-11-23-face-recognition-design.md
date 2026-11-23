# Face recognition in the People detector: design

Status: design for the owner's approval. Nothing is built before approval. No push, tag or release without the
owner's go for that item. The work is in `/Users/dompa/dev/hass-sharp-vision`; this document lives in hass-sharp
beside the detection design it follows (`2026-11-22-face-detection-external-import-design.md`).

## Context

The People detector (hass-sharp-vision 1.0.0) finds faces and people in a camera's frames. It does not know who a
face is. HassSharp 0.0.49-beta gave C# devices a page in the panel (`Ui`), data that outlasts restarts (`Data`,
`Shared`) and `SharpImage.Crop`/`Resize` (`2026-11-23-device-ui-design.md`). This project uses them.

It is the second of three: device pages (done), recognition (this), the "who" sensor for automations (later, its own
design). It is built after "Library classes as nodes" (`2026-11-23-library-class-nodes-design.md`), and its release
carries the People detector as such a node.

A spike measured SFace (OpenCV zoo, `face_recognition_sface_2021dec.onnx`) on tract 0.23.10:

- Apache-2.0. The zoo does not state the training data; the paper's datasets have research-only terms.
- The float model loads and runs unchanged; 36 ms per face (alignment and inference) in Home Assistant's arm64
  Alpine image, where YuNet at 640 takes 24 ms. Planning takes about 115 ms once; memory peaks at about 140 MB while
  loading and settles about 15 MB above today's.
- The int8 model needs two operators tract lacks, and runs 2.4 times slower once rewritten. Not used.
- 38.7 MB. Embedded, each library would grow from 20 MB to 59 MB.
- Input: 112×112 RGB, 0–255, aligned with YuNet's five landmarks in the order hs_vision already returns. Output: 128
  floats, compared by cosine after normalising.
- **Quality is unproven.** The repos held one usable photo. In it two different men score 0.55, above OpenCV's 0.363
  line for "same person"; altered copies of one face score 0.51–0.98. Faces there have 16–32 px between the eyes.
  The embedding is stable down to about 10 px between the eyes and noise below 5. Profile and covered faces pass
  detection and give junk embeddings.

## Intent

The People detector learns who people are from the user. It shows the faces it does not know, grouped by person, on
its page; the user names each person once; from then on it recognises them, on every People detector of the
installation.

## Goals

- Each clear face in a checked frame is matched against one shared list of people.
- Faces that match nobody are grouped, so one person is one card, and shown on the detector's Device tab once seen
  more than in passing. The user names a group (a new person, or one already known) or dismisses it.
- The user can rename, merge and delete people, and look through and remove a person's sample faces.
- The detector teaches itself: a face that matches a person very surely is kept as a further sample of them, within
  the safeguards below. A setting turns this off.
- A face that matches a person only fairly is shown with that name suggested, for the user to confirm.
- Recognised names are drawn on the detector's camera picture.
- A setting turns person detection off; a setting turns recognition off.
- The People detector is a node of its own in the step list (the vision repo's part of "Library classes as nodes").

## Non-goals

- Anything for automations: no "who" sensor, no event, no trigger (project 3).
- Following a face from frame to frame, or holding a name while a face is briefly lost (project 3 decides what it
  needs).
- A smoother camera picture than one frame per check.
- Recognising people from their body, clothes or gait.
- A "Recognise faces" graph node.
- Importing or exporting people, or enrolling from uploaded photos.
- Any use as a lock: the guide says recognition is a convenience and must not open a door.

## Anti-goals

- The detector or the library sending a face crop, an embedding or a name anywhere, or writing one to a log line.
  Two things are outside its reach and are said plainly in the README: stored data is part of Home Assistant's
  backups, so a backup kept off the machine carries the people list; and the camera picture, names included, is seen
  by every Home Assistant user and by whatever the camera is exposed to (*Names on the camera* turns the names off).
- The detector learning a wrong face and drifting: a learned sample must never be the only reason a later one is
  learned.
- A passer-by seen once becoming a card, or strangers' faces being kept without limit.
- A check that takes so long with many faces that the detector falls behind its *Check every*.
- Two detectors overwriting each other's changes to the people list.
- People that cannot be deleted because no detector is running (the Stored data dialog deletes the shared store).
- Users of Detect faces alone paying for recognition: no larger library download, no model in memory.

## Decisions (owner's)

- Recognition by SFace embeddings, in the same native library on tract, aligned with YuNet's landmarks.
- A stored list of people with names, embeddings and face crops; local, deletable, never sent anywhere.
- One people list for all People detectors.
- Unknown faces are grouped; a group is named (new or existing person) or dismissed; people can be reviewed, merged,
  renamed and deleted.
- The detector teaches itself, with the safeguards described to the owner: anchored to the samples the user named, a
  clear winner only, capped, visible and removable; a setting turns it off; faces that match only fairly are shown
  for confirmation.
- A setting turns person detection off.
- The People detector becomes a node the user plugs in.

## Approaches considered

**Where the model lives**

- **A separate, pinned download (chosen).** `Vision.cs` downloads the model through `Files` when recognition is
  first opened, and hands the library its path. One 38.7 MB file serves all four platforms, and only installations
  that recognise fetch it.
- **Embedded in the library**, as YuNet and YOLOX are. One file to pin, but every library download triples, for
  Detect faces users too.
- **The int8 model.** A quarter of the size, but it does not load without rewriting the file and is slower. Rejected.

**Where the logic lives**

- **Native embeds, C# decides (chosen).** One new native call turns a face into an embedding. Matching, grouping,
  learning, storage and the page are C# in the node library, testable with made-up embeddings and changeable without
  a native release.
- **Native matches and groups too.** Faster in theory, but with a few hundred samples the comparison costs nothing,
  and every rule change would need four new binaries. Rejected.

**How unknown faces are grouped**

- **As they arrive (chosen).** A face joins the closest group if it is close enough, else starts one; two groups that
  grow close are joined. Simple, steady cost, results at once.
- **In batches** (cluster everything every few minutes). Better groups in theory, but cards would appear late and
  reshuffle. Rejected.

The owner left these choices to me.

## Design

Everything below is my call unless it repeats a decision above. Names and numbers are proposals; the builder may
adjust them, keeping the behaviour. Every threshold marked *(calibrated)* gets its default from the calibration step.

### 1. Native library (hs_vision 1.1.0, ABI 2)

- New exports, beside the unchanged ones: open a recogniser from a model file's path, embed one face, close it.
- Embed takes the picture as the detector calls take it, and the face's five landmarks in the picture's pixels. It
  aligns the face to 112×112 as OpenCV's `alignCrop` does (the spike's `align.rs` is the reference: a similarity
  transform onto alignCrop's five points, bilinear), from the **full picture**, not the detector's letterbox. It
  returns the 128 values normalised to length 1, and the alignment's residual so the caller can judge the fit.
- The model is loaded and planned **once per process**, shared by every recogniser opened on the same model file and
  freed with the last of them. Each definition has its own copy of the C# code but the same native library, so
  without this each People detector would hold its own 15 MB and starting several at once would peak at 140 MB each.
  Whether calls share the detector's lock is the builder's call; the spike found a shared plan safe from four
  threads.
- The model is not embedded. `LICENSE-sface` and the NOTICE and THIRD-PARTY-LICENSES entries (source, SHA-256
  `0ba9fbfa…4e79`, "unmodified") ship with the release, and the model file is a release asset.
- Rust tests: the alignment against fixed landmarks, a pinned embedding of a test face, a missing or wrong model
  file answered as an error. The model is not committed: CI and a local script fetch it by its pinned SHA-256.

### 2. `Vision.cs`

- `Vision.OpenRecognizerAsync(Files, rid, Stopping)` downloads the pinned library and the pinned model, checks the
  ABI is 2, and opens a `VisionRecognizer`; `Embed(image, face)` returns the embedding and the fit.
- The library check moves to ABI 2 for everything, so 1.1.0's `Vision.cs` needs 1.1.0's library.
- A small helper judges a face before it is embedded (section 4).
- Recognition is behind an interface, so the tests of everything above it use made-up embeddings.

### 3. The people list (`People.cs`, a new node library)

One shared store, `Shared("hass-sharp-vision.people")`, used by every People detector.

- A person: an id, a name, when added, and samples. A sample: an id, its embedding, a face crop (JPEG, about
  160×160), when and at which camera it was taken, and whether the user **named** it or the detector **learned** it.
- "Where" is the detector's camera: its entity id is stored and its friendly name is shown. A device cannot read its
  own name in HassSharp today, and the camera is what tells the user where someone was.
- At most 20 named and 20 learned samples per person, and 200 people (a page list holds 200 cards).
- Every change is one `Update` of one key, so two detectors cannot overwrite each other; a change that spans two
  people (a merge) is written so that an interruption loses nothing: the samples arrive before the source goes.
- Each detector keeps the list in memory and reloads it on `Changed`.
- The layout of keys is the builder's, within device data's limits (a value at most 8 MiB, a store at most 512 MiB),
  and carries a format version. At the caps the embeddings alone exceed one value, so the list is split across keys;
  `Update` is atomic for one key only, so the names live in one key that every create and rename goes through, and a
  change that touches two keys is ordered so that an interruption leaves nothing dangling that matters.
- Names are compared without regard to case and surrounding spaces; two people cannot have the same name.

### 4. Which faces are used

A detected face is **usable** when it is clear enough to embed: at least 16 px between the eyes *(calibrated)*, both
eyes clearly apart relative to the box (not a profile), and an alignment fit under a limit *(calibrated)*. A face
that is not usable still counts for Face seen and Faces; it is drawn without a name and never embedded, matched,
grouped or learned.

At most 4 faces are embedded per check, the largest first, so a check costs at most about 150 ms more on the measured
host.

### 5. Matching

A usable face's embedding is compared with every sample of every person. A person's score is their best sample's
cosine.

- **Recognised:** the best person's score is at least *Match score* (a setting, default *(calibrated)*, expected
  near 0.6) **and** beats the second-best person's by at least 0.1 *(calibrated)*.
- **Maybe:** the best score is under that but at least the suggest line *(calibrated, expected near 0.45)*. The face
  goes to the unknown groups, with that person's name suggested.
- **Unknown:** anything else.

### 6. Unknown faces (each detector's own `Data`)

- An unknown or maybe face joins the closest group if its cosine to one of the group's samples is at least *Match
  score*; otherwise it starts a group. Two groups whose samples come that close are joined.
- A group keeps up to 5 samples (embedding and crop), preferring larger faces and ones unlike those it has, and its
  first seen, last seen and number of sightings (one per check).
- A group is **shown** once it has 3 sightings. One not shown within an hour is dropped.
- A shown group not seen for *Keep unknown faces for* days (a setting, default 7, 1 to 365) is deleted. At most 50
  groups are kept; the one seen longest ago goes first.
- When the people list changes, groups are matched again: one that now matches a person is removed.
- Writes are held down: a group is written when it is created, gains a sample or is shown, and its last seen and
  count at most once a minute.

### 7. Learning

With *Learn new looks* on (a setting, default on), a recognised face is kept as a **learned** sample of that person
when all of these hold:

- its score is at least the learn line *(calibrated, expected near 0.7)*, and it beats the second-best person by at
  least 0.15 *(calibrated)*;
- its cosine to one of the person's **named** samples is at least *Match score* (the anchor: a learned sample never
  qualifies another);
- the face is well above usable (at least 24 px between the eyes *(calibrated)*);
- it adds something: its cosine to every sample the person has is under 0.9;
- the person gained no learned sample in the last 10 minutes.

When a person has 20 learned samples, the oldest learned one is replaced. Named samples are never replaced.

### 8. The page

Drawn in `Start()` and again when the people list or the groups change. It is not drawn when *Recognise faces* is
off.

```
Unknown faces (2)                                   [attention]
 [crop]  Seen 14 times · last 5 minutes ago
         Might be Ivan
         Name [Ivan______]   [Save] [Dismiss]
 [crop]  Seen 3 times · last 2 hours ago
         Name [__________]   [Save] [Dismiss]

People (2)
 [crop]  Ivan
         9 samples (3 learned) · last seen 5 minutes ago at Front door
         Name [Ivan______]  Merge into [Ana ▾]   [Save] [Merge] [Samples] [Delete]
 [crop]  Ana …

Samples of Ivan                                     [only after Samples]
 [crop]  Named · 12 Nov                             [Remove]
 [crop]  Learned · 2 hours ago at Front door        [Remove]
                                                    [Close] on the first card
```

- **Unknown faces** (attention): one card per shown group, newest sighting first. The Name field is required and
  offers the known names; for a maybe it is filled with the suggested name. **Save** with a known name adds the
  group's samples to that person as named samples; with a new name it makes a person. **Dismiss** deletes the group
  and does not check the field.
- **People:** one card per person, by name. **Save** renames. **Merge** moves this person's samples to the one chosen
  in *Merge into* and removes this one; it asks first. A button's question is fixed when the card is drawn, which is
  why merging is a button of its own, and *Merge into* and **Merge** are drawn only when there is another person (a
  select needs an option). **Samples** shows the third list. **Delete** asks first and removes the person with their
  samples.
- **Samples of …:** one card per sample of the chosen person, named first, then learned, newest first. **Remove**
  deletes the sample; the last named sample of a person cannot be removed (delete the person instead). The list goes
  away with **Close**, and when the person does.
- A refusal (`UiRefusal`) says what is wrong in a sentence: the name is taken by a merge target, the group is gone,
  the person has no room for more samples.
- "Last seen" is where and when a person was last recognised, kept in the people list and written at most once a
  minute per person.

### 9. The People detector's settings and entities

New settings, after the five it has:

| Setting | Default | |
|---|---|---|
| Detect people | on | Off: the person model is not run. Person seen, People and Last person seen read unavailable. |
| Recognise faces | on | Off: no model download, no matching, no page. |
| Match score | *(calibrated)* | 0.3 to 0.9: higher recognises less, and less wrongly. |
| Learn new looks | on | Section 7. |
| Keep unknown faces for | 7 | Days, 1 to 365. |
| Names on the camera | on | Off: the camera picture shows boxes without names. |

All have defaults, so the library saves over definitions that use the People detector today. No entity is added or
removed. The camera picture draws a recognised face's name over its box.

`PeopleDetector` gets `[Node("People detector", Category = "Vision")]`, loses `abstract`, and its constructor
becomes public. The bundled Front door people definition uses that node, with the node id and key it has today.
`hass_sharp.toml` lists `People.cs` as a third library.

### 10. Calibration (the first build step)

A small tool in the vision repo's tests runs detection and embedding over labelled faces and prints the scores of
same-person and different-person pairs by face size (pixels between the eyes).

- **What it runs on** (the owner's choice): the public labelled benchmark LFW (about 13,000 photos of 5,700 people,
  1,680 of them with two or more), downloaded for the purpose and downscaled to door-camera face sizes. Footage of
  known people from the owner's own camera can be added later. The repos' own footage is not enough: one photo of
  eight people, one still of people from behind, and the dummy front door stream, a moving clip of one man.
- **The targets.** *Match score* is the lowest score at which at most 1 in 1,000 different-person pairs match, at
  the usable face size. The match margin, the suggest line, the learn line (at most 1 in 10,000) and its margin and
  size are set the same way. The usable size is the smallest at which at least 8 in 10 same-person pairs still match
  at that *Match score*.
- **When it stops for the owner.** If no face size up to 40 px between the eyes reaches 8 in 10, the build stops
  and the owner decides whether to go on. Otherwise it continues without asking.

The numbers go into the README with the plain statement of what recognition can and cannot be trusted with. Footage,
datasets and embeddings used for it are not committed.

### 11. Release

- hass-sharp-vision 1.1.0: libraries for the four runtimes and the model as release assets, pinned by SHA-256 in
  `Vision.cs`; the three node files carry the version.
- Its CI builds against a hass-sharp commit that has device pages and library classes as nodes; the README names the
  HassSharp release it needs.
- Every tag, push and release waits for the owner's go.

## Risks

- **Recognition quality on a door camera** is the open question; calibration answers it before the rules are fixed.
  If same and different people cannot be separated at usable face sizes, the usable size goes up and the README says
  so; if that leaves too little, the owner decides whether to go on.
- **Learning from a wrong match.** The anchor, the margin, the size bar and the review list are the guard; the
  setting is the way out.
- **Check time.** Four embeddings add about 150 ms; with *Check every* at 0.1 the detector checks less often than
  set. The README says so.
- **Memory.** Loading the model peaks at about 140 MB for a moment.
- **The training data's terms** are not stated by the zoo. The owner knows; the NOTICE says what is known.

## Left to the builder

- The native call's exact signature, and the layout of the stores' keys.
- How "not a profile" is measured, and every *(calibrated)* number.
- How samples of a group and of a person are chosen when there are too many.
- The wording on the page, and the order of work after calibration.

## Verification

- **Rust** (`cargo test` in `native/`): section 1's tests.
- **C#** (`tests/Vision.Tests`, against a hass-sharp checkout, with made-up embeddings): matching with its margin; a
  maybe; grouping, joining groups, the 3-sighting rule, expiry and the cap; naming a group as a new and as an
  existing person; rename, merge, delete; samples list and the last-named-sample rule; every learning condition, one
  test each, and that a learned sample never anchors; two detectors on one list see each other's changes and do not
  lose an update; nothing is embedded with *Recognise faces* off, and no person model runs with *Detect people* off;
  the page's lists, fields and actions; no name or image in a log line.
- **Manifest and pins:** the existing Python tests, with the model's pin and the third library.
- **Live check** in a throwaway `hass_wt` container on port 5002 (never the `hass` container, never the
  `mcp__hass__*` tools), with a People detector node on a stream of the dummy front door's moving clip, and a second
  camera showing the group photo for other faces: a group appears after a few sightings; naming it makes a person; the name is drawn on the camera picture; a restart keeps it; a second
  detector recognises the same person and shows the same People list; learned samples appear and one can be removed;
  merge and delete work; with both definitions off, the Stored data dialog deletes the people; the log holds no name
  or image data; check time with four faces is measured and reported.
