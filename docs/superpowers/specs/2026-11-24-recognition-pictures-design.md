# The picture of a recognition: design

Status: **approved by the owner on 2026-11-24** ("lets do it"), as laid out in the conversation. Two repositories:
HassSharp (a device can keep files in Home Assistant's media folder) and hass-sharp-vision (the People detector keeps
the face of each arrival and says where it is).

## Why

The owner wants an automation: a person comes in front of the camera, is recognised, and a phone notification shows
their face with a button. Today nothing gives an automation the face. One image entity ("Last recognised face") would
show whatever it holds when the phone fetches it, which can be another person's face: the phone fetches at delivery,
later when it was offline, and two people can be in frame at once.

## Intent

Each arrival has its own picture, and the Recognised sensor says where that picture is in the same state change that
announces the arrival. An automation reads the path from its trigger, which later recognitions cannot change.

## Goals

- A C# device or entity can keep files in Home Assistant's media folder and get the path Home Assistant serves each at
  (`/media/local/…`), which the phone app loads with the user's login.
- The People detector, when asked to, keeps the face crop of each arrival and of an unknown arrival, and the Recognised
  sensor carries their paths.
- The pictures stay few and short-lived, and go with the person they show.

## Non-goals

- An image entity. A picture of every check, or of every face. Video.
- Sending a notification from the detector: the automation does that.
- A public link, or a new HTTP route.
- Media files for graph-built (declarative) devices, or through the MCP tools.

## Anti-goals

- A notification showing a face other than the one that triggered it.
- A face picture reachable without a Home Assistant login, a name in a file name, or anything of a face in the log.
- Pictures piling up, or outliving the person's deletion or the definition's.
- A state published with a path whose file is not on disk yet.

## Approaches

- **Files in the media folder, the path as an attribute (chosen).** The companion apps load `/media/local/…` with the
  user's login (their documentation's recommended way), and a trigger's attributes are frozen when it fires. Costs: any
  logged-in user can browse the media folder, so the detector's setting is off by default; HassSharp needs a small
  addition.
- **One image entity per person.** Still the latest only, and entities come and go with the people list.
- **A login-protected picture address in HassSharp.** More work, and it is not confirmed that the phone apps sign in to
  an integration's own address.

## Design (the controller's calls unless the owner said it)

### HassSharp: `Media`

- `SharpComponent` gains `protected SharpMedia Media`, from `Start()` on, as `Data` is; before that it throws in a plain
  sentence, after the definition stopped `ObjectDisposedException`.
- The folder is `<media folder>/hass_sharp/<definitionId>/<nodeKey>/`, private to the C# node as `Data` is. The media
  folder is Home Assistant's `media_dirs` entry `local`, else the first by name; Python hands the entries to .NET.
  Without any, every call throws `InvalidOperationException` saying Home Assistant has no media folder.
- `SharpMedia`:
  - `string Save(string name, byte[] content)`: writes the file whole (temporary file, then rename) and gives the path
    Home Assistant serves it at, `/media/<entry>/hass_sharp/<definitionId>/<nodeKey>/<name>`. A file of that name is
    replaced.
  - `string? Path(string name)`: that path when the file is there.
  - `bool Remove(string name)`, `IReadOnlyList<SharpMediaFile> Files()` (name, path, size, when written; newest first),
    `void Clear()`.
- A name is 1 to 100 of `A–Z a–z 0–9 . _ -`, does not start with a dot, and has an extension; anything else throws
  `ArgumentException` in a plain sentence. A file is at most 10 MiB (the phone apps' limit) and a node keeps at most 200
  files; a `Save` past either throws.
- Lifetime, as private data: kept while the definition is off, failed or recompiled; deleted when the definition is
  deleted or saved without that node.
- Safe from any thread. Nothing of a file's content is logged.
- `docs/custom-entities.md` gains a section with the notification example and says who can see the files.

### hass-sharp-vision: the People detector

- A setting **Keep recognition pictures** (`KeepPictures`), off by default. Off: no file is written, the detector's
  media files are cleared at start, and the attributes below are empty.
- **An arrival** is a person entering Recognised (the moment `last_recognised_at` is set today). Its picture is the crop
  of the face given to that person in that check, cut as the page's crops are. **An unknown arrival** is
  `unknown_faces` rising from 0; its picture is the crop of the largest face compared and given to no one in that check.
- A picture's name is `<UTC yyyyMMddTHHmmss>-<16 random hex>.jpg`: no person's name or id.
- Recognised's attributes:
  - `pictures`: the paths of the people in `people`, in its order; null for a person without one.
  - `last_recognised_picture`: the path of `last_recognised`'s arrival picture, or null.
  - `unknown_picture`: the path of the unknown arrival's picture while unknown faces are in frame, else null.
- The file is on disk before the state that names it is published.
- Kept: at most 20 pictures per detector and none older than 24 hours, pruned at each save and at start. The detector
  keeps, in its own data, which picture shows which person; a person deleted or merged away loses their pictures, here
  and on the other detectors, and an attribute naming a removed picture becomes null.
- The README gains the automation: trigger on Recognised, notify with
  `image: "{{ trigger.to_state.attributes.last_recognised_picture }}"` and an action button.
- The library needs a HassSharp that has `Media`; the README says from which release, and CI's `HASS_SHARP_REF` moves.

## Left to the builder

Exact member names and overloads; how Python hands the media folders over; where the picture index lives in the
detector's data; how a restart treats `pictures` of people still held.

## Verification

- HassSharp runtime tests: save, replace, path, files, remove, clear; names and sizes refused; before `Start()` and
  after stop; kept across a recompile; deleted with the definition and with the node; no media folder. Python: the media
  folders reach .NET. All of `tests/python` in the `hass` container.
- Vision tests: off writes nothing and clears; an arrival's picture is on disk when the state names it; two arrivals in
  one check; an unknown arrival; a second check of the same visit writes nothing; 21 arrivals keep 20; a picture older
  than a day goes; a deleted and a merged person's pictures go and their attributes become null; no name in a file name
  or in the log.
- Live, on the local instance: the attribute's file exists in the container's media folder and opens in the Media
  browser. The notification on a phone is the owner's check: the local instance has no phone.
