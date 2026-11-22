# "Add external": import node libraries and entity definitions from a GitHub repo

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Paste a public GitHub repository's URL in the panel; HassSharp reads `hass_sharp.toml` at the repo's root at one commit, lists the node libraries and entity definitions it names, and imports the ones picked: the libraries in one atomic save, then each definition through the existing entity save, new ones turned off. This repo gets a root `hass_sharp.toml` offering `FaceDetection.cs` and a "Face camera" definition.

**Architecture:** (1) .NET gets one new library operation, `import`, which commits several library documents through the same preview, rebuild and `AuthoringFiles.Commit` that `save` uses; (2) a new stdlib-only Python module `external.py` holds every rule (URL forms, manifest, paths, caps, ids, the library-kind rewrite) and the inspect/import flow over two injected seams, a fetcher and a backend; (3) `AuthoringService.external` wires those seams to Home Assistant's aiohttp session and to the .NET requests it already makes, and `websocket.py` registers `hass_sharp/external` like the other envelope routes; (4) the frontend gets one shared dialog opened by an **Add external** button in two views; (5) the repo's manifest, the example definition and the docs.

**Tech Stack:** C# on .NET 11 (`CC/HassSharp/`, custom runner `tests/Runtime/`), Python 3.13+ (`tomllib`, Home Assistant's aiohttp session; `tests/python/`), SolidJS + TypeScript 7 + vitest 5 + happy-dom (`FE/`). `CC/` below is `custom_components/hass_sharp/`, `FE/` is `CC/frontend/`.

**Spec:** `docs/superpowers/specs/2026-11-22-face-detection-external-import-design.md`, sections 6–9 and the Project B lines of Verification. Project A (the Rust library, `[NodeSetup]`, `DrawCircle`, `nodes/face_detection/FaceDetection.cs`) is a separate plan that lands first; this plan assumes `FaceDetection.cs` exists with `[Node("Detect faces", Category = "Vision", Pure = true)] public static SharpImage DetectFaces(SharpImage image, double minScore = 0.7, int inputSize = 320)` in `public static class FaceDetection` (namespace `NodeLib`).

## Global Constraints

- **Every commit leaves these green**, from the repository root: `python3 -m unittest discover -s tests/python`; `python3 -m unittest discover -s tests`; `dotnet run --project tests/Runtime --disable-build-servers`; `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests`; `(cd custom_components/hass_sharp/frontend && bun run test)` and `(cd custom_components/hass_sharp/frontend && bunx tsc --noEmit)`.
- **No pushes.** Commit locally only. The live import from this repo's URL waits until the owner has merged to main (Task 7).
- **Never the `mcp__hass__*` tools**: they reach the production Home Assistant. Live checks use the local container only (port 5001, container `hass`).
- **GitHub only.** Accepted: `https://github.com/<owner>/<repo>` with an optional `/tree/<ref>`. Ref → sha with `GET https://api.github.com/repos/{owner}/{repo}/commits/{ref}` (`HEAD` when no ref is given: GitHub resolves it to the default branch). Files only from `https://raw.githubusercontent.com/{owner}/{repo}/{sha}/{path}`. No git process, no tarball.
- **Caps and path rules.** Each fetched file, `hass_sharp.toml` included, at most `2 * 1024 * 1024` bytes (`CAP`); at most 50 items in a manifest (`MAX_ITEMS`). A path is relative, `/`-separated, with no leading `/`, no `\`, no empty, `.` or `..` part; a library's ends in `.cs`, an entity's in `.json` (case-sensitive).
- **Library choices:** Import when no library of that file name exists; otherwise only Overwrite (of one of the same-named files, the user picks which when there are several) or Skip. Never a second copy: all library files share the `NodeLib` namespace.
- **Entity choices:** Import when no definition of that name exists; otherwise Overwrite / Add as new / Skip. A new one is saved with `expectedRevision: 0` and `enabled: false`, its id by the `definitionId()` rule (`FE/src/entities/model.ts:333`). "Existing" matches by library file name and by definition name, exactly.
- **Import re-fetches the inspected sha**; it makes no API call. Libraries first (one `import` request to .NET, atomic), then entities one by one.
- **Exact copy** (frontend):
  - Button (both views): `Add external`. Dialog title: `Add external`.
  - Intro: `Paste the address of a public GitHub repository with a hass_sharp.toml at its root, such as https://github.com/owner/repo or https://github.com/owner/repo/tree/main.`
  - Warning, always shown, `role="note"`: `Node libraries and entity definitions run C# inside Home Assistant with full access to it: its devices, its data, its files and your network. Import only from repositories you trust.`
  - Field label `Repository URL`; buttons `Inspect`, `Import`, `Cancel`, `Close`; group headings `Node libraries`, `Entity definitions`; link text `View on GitHub`.
  - Choice options: `Overwrite {name} ({id})` (one per existing), `Add as new` (entities only).
- **Exact copy** (Python answers; `ExternalError(code, message)`):
  - `invalid_request`: `Paste a GitHub repository's address, such as https://github.com/owner/repo or https://github.com/owner/repo/tree/main`
  - `not_found`: `GitHub has no public repository {owner}/{repo}` / `GitHub has no public repository {owner}/{repo} with a branch, tag or commit {ref}` / `{owner}/{repo} has no hass_sharp.toml at its root at {sha7}`
  - `fetch`: `GitHub refused the request: it allows 60 requests an hour without signing in; try again later` (HTTP 403 or 429) / `Could not reach GitHub: {reason}` / `{path} is larger than 2 MiB`
  - `manifest`: `hass_sharp.toml is not valid TOML: {tomllib message}`, or `hass_sharp.toml: ` followed by the problems joined with `; `, each like `library 1: path must be a relative path inside the repository to a .cs file`
  - item problems (inspect): `{path} is not in the repository at {sha7}`, `{path} is larger than 2 MiB`, `{path} is not UTF-8 text`, `{path} is not valid JSON: {message}`, `{path} is not an entity definition: it needs logic of kind graph`
  - entity not attempted after a refused library import: `Not imported, because the node libraries were not`
- **Locks** (AGENTS.md): no new lock is needed (the import runs under `_visualGate` as `save` does); if one is, it is a `readonly Lock gate = new();`.

## Review Focus

1. **A definition that uses a library node, imported where the library gets another id.** A library node's kind is `library:<library document id>:<type>.<method>` (`NodeLibraryCompiler.cs:391`), and the panel gives a new file the id `library-<uuid>` (`FE/src/visual/library-session.ts:189`), so the repo cannot know it. The manifest names a library `id`, and import rewrites the kinds (Ruling 1). Expected: Face camera saved after overwriting a `FaceDetection.cs` stored as `library-1234` runs `library:library-1234:…`. Tests in Tasks 2 and 3.
2. **A URL copied from the browser** with a trailing `/`, `.git`, `?tab=readme-ov-file`, `#readme`, `www.github.com`, or `/tree/feature/x` (a ref with a slash). Expected: accepted as the repo (and the ref). Test in Task 2.
3. **GitHub's limit for requests without signing in, or no network in the container.** Expected: the plain sentence, and the dialog back at its URL step and closable (not stuck under `preventClose`). Tests in Tasks 2 and 5.
4. **Overwriting a definition whose settings the user filled in** (an address, a secret). Expected: each imported setting whose key and type the existing definition has keeps its stored value (`keep: true`); a secret is never lost. Test in Task 3.
5. **An unsaved draft of a library the import overwrites.** Expected: after the import the library view lists it as in conflict (`!`), the draft kept, as a save made elsewhere does today. Test in Task 5.

---

### Task 1: The library `import` operation

**Files:**
- Modify: `CC/HassSharp/Visual/NodeLibraryApi.cs` (operation dispatch before `var id = …` at :202; the commit tail of `save` at :256–289)
- Create: `tests/Runtime/NodeLibraryImportTests.cs`

**Interfaces:**
- Produces, request: `{operation: "import", libraries: [{id, name, source, expectedRevision}]}` — one or more; each `id` passes `NodeLibraryStore.ValidateId`, each `name` `ValidateName`, ids distinct within the request; `expectedRevision` the stored revision (0 for a new id).
- Produces, answer: `{saved: true, documents: [NodeLibraryDocument, …] (in request order, each at its new revision), generation, catalog, automations, invalid, definitions, invalidDefinitions}` — the members `save` answers beside `document`, with the same meaning. A refusal is exactly `save`'s: `validation` with `{diagnostics, automations, invalid, definitions}`, `conflict` when any revision differs, `invalid_request` for a bad field, `storage`.
- Internal: the tail of `save` (candidates → compile → rebuild graphs → `CheckDefinitions` → `AuthoringFiles.Commit` → activation) becomes one private helper taking the documents written and the id deleted (or none); `save`, `delete` and `import` all go through it. `save`'s and `delete`'s answers are byte-identical to today's.

**Behaviour:**
- One candidate set (saved documents minus those replaced, plus the imported ones, ordered by id), one `Preview`, one rebuild of every graph and definition, one `Commit` holding every library file and every rebuilt document. Nothing is written unless all of it is valid.
- Messages: an empty `libraries` → `invalid_request` `libraries must list at least one library`; a repeated id → `invalid_request` `Each library may be imported once: {id}`.
- Not exposed to MCP (`llm.py` unchanged).

- [ ] **Step 1: Write the failing suite** `[RuntimeSuite("--node-library-import", Order = 41)]` in `NodeLibraryImportTests.cs`, helpers as `NodeLibraryStoreTests` has (`Request`, `Result`, a temp `HassSharpManager`):
  - `TwoDependentLibrariesImportTogether`: `A.cs` declares `public record Reading(int Value);`, `B.cs` a `[Node]` taking `Reading`; importing both (rev 0) succeeds, `documents` has both at revision 1, `list` shows both; saving `B.cs` alone first (rev 0) is refused with `validation` (proves the single compile).
  - `OneInvalidLibraryWritesNothing`: `A.cs` valid + `C.cs` with a syntax error → `validation`, `list` unchanged, no file under `libraries/` for either id, the dependent graph of the fixture still at its revision.
  - `AStaleRevisionWritesNothing`: an existing `shared` at revision 1 imported with `expectedRevision: 0` beside a new `extra` → `conflict`; neither changed.
  - `ImportRebuildsGraphsOnce`: a saved graph using `shared`'s node; import overwriting `shared` (rev 1) and adding `extra` → the graph's revision advances by exactly 1 and it is active; the answer's `automations` lists it once.
  - `BadRequests`: `libraries: []`, a repeated id, an invalid id (`"Bad Id"`) and a name with `/` → `invalid_request` with the messages above.
  - `SaveAnswerUnchanged`: `--node-library-store` passes with no edit (run it in Step 4).
- [ ] **Step 2: Run** `dotnet run --project tests/Runtime --disable-build-servers -- --node-library-import`. Expected: FAIL, `Unknown library operation`.
- [ ] **Step 3: Implement** the `import` branch and the shared commit helper.
- [ ] **Step 4: Run** `-- --node-library-import --node-library-store`, then every suite. Expected: PASS.
- [ ] **Step 5: Commit** — `Node libraries: an import operation saves several library files in one checked commit.`

---

### Task 2: `external.py`: the rules and the GitHub fetcher

**Files:**
- Create: `CC/external.py` — stdlib only at module level (no Home Assistant, no aiohttp import, no relative import), so tests load it with `importlib.util.spec_from_file_location` as `tests/python/test_scheduling.py` does.
- Create: `tests/python/test_external.py`

**Interfaces (produces):**
- `CAP = 2 * 1024 * 1024`, `MAX_ITEMS = 50`, `ALLOWED_HOSTS = frozenset({"api.github.com", "raw.githubusercontent.com"})`
- `class ExternalError(Exception)` with `.code: str` (`invalid_request` | `not_found` | `fetch` | `manifest`) and `.message: str`; `answer()` → `{"version": 1, "ok": False, "error": {"code", "detail": message}}`.
- `class HttpStatus(Exception)` with `.status: int` — what a fetcher raises for a non-200 answer.
- `@dataclass(frozen=True) class Repo: owner: str; repo: str; ref: str | None`
- `parse_url(url: str) -> Repo` — strips whitespace, a query and a fragment, a trailing `/` and `.git`; host `github.com` or `www.github.com`, scheme `https`; owner `^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$`, repo `^[A-Za-z0-9._-]{1,100}$` and not `.`/`..`; optional `/tree/<ref…>` (the rest, slashes kept, non-empty). Anything else → `ExternalError("invalid_request", …)`.
- `@dataclass(frozen=True) class Item: kind: str ("library" | "entity"); name: str; path: str; description: str; id: str | None` (`id` libraries only).
- `parse_manifest(data: bytes) -> list[Item]` — UTF-8, `tomllib.loads`; top level: `library` and `entity` arrays of tables (each optional; other keys ignored, as are unknown keys in an item). Per item: `name` required (library: `ValidateName`'s rule — non-blank, ≤ 120, none of `/ \ \0 \r \n`; entity: non-blank, ≤ 120), `path` required (`check_path`), `description` optional string (default `""`), library `id` optional, `^[a-z0-9][a-z0-9_-]{0,79}$`. Across items: no repeated path, no repeated library name, no repeated library id, at most `MAX_ITEMS`. All problems collected, then one `ExternalError("manifest", …)`. Items in manifest order, libraries first.
- `check_path(path: str, kind: str) -> str | None` — the problem sentence or None.
- `definition_id(name: str, taken: Iterable[str]) -> str` — the port of `definitionId` and `slug`/`trimSeparators` (`FE/src/visual/contract.ts:349–352`): lower-case, NFD, drop combining marks (category `M*`), runs of `[^a-z0-9]` → `_`, trim `_`/`-` at both ends, first 80, trim trailing separators, `entity` when empty, `new` always taken, `_2`, `_3`, … shortening the base to fit 80.
- `rewrite_library_kinds(graph: dict, ids: dict[str, str]) -> dict` — a deep copy in which each node `kind` `library:<a>:<rest>` with `a` in `ids` becomes `library:<ids[a]>:<rest>`; nothing else changes. (Both ids match the id pattern, so `Uri.EscapeDataString` leaves them as they are.)
- `library_source(item: Item, data: bytes) -> str` and `entity_body(item: Item, data: bytes) -> dict` — raise `ItemProblem(message)`; `entity_body` returns `{"name": item.name, "settings": file.get("settings") or [], "logic": file["logic"]}` (only these three: a read answer's `id`, `revision`, `entities`, `devices`, `source` are dropped) and requires `logic` to be an object with `kind == "graph"` and a `graph` object.
- `Fetch = Callable[[str, str], Awaitable[bytes]]` — `(url, accept)`; returns at most `CAP` bytes or raises `HttpStatus`, or `ExternalError("fetch", …)` for a body over `CAP` (`{name} is larger than 2 MiB`, name = the last path part) or a failure to connect.
- `aiohttp_fetch(session, timeout) -> Fetch` — `session.get(url, headers={"Accept": accept, "User-Agent": "HassSharp"}, timeout=timeout)`; refuses a final `response.url.host` outside `ALLOWED_HOSTS` (`Could not reach GitHub: it sent the request to {host}`); checks `Content-Length` first, then reads `response.content` in chunks and stops past `CAP`; `TimeoutError`/client errors → `Could not reach GitHub: {reason}`. Duck-typed: the module never imports aiohttp.
- `class GitHub: __init__(self, fetch: Fetch)`; `async resolve(repo: Repo) -> str` (40-hex sha from the API's `sha`; 404/422 → the `not_found` sentences; 403/429 → the limit sentence); `async file(owner, repo, sha, path) -> bytes` (404 → `HttpStatus` passed up for the caller to word).

- [ ] **Step 1: Write the failing tests** in `test_external.py` (module loaded by path):
  - `test_url_forms`: `https://github.com/o/r`, `…/r/`, `…/r.git`, `https://www.github.com/o/r`, `  https://github.com/o/r?tab=readme-ov-file#readme `→ `Repo("o","r",None)`; `…/tree/main` → ref `main`; `…/tree/feature/x` → ref `feature/x`. Refused with the `invalid_request` sentence: `http://github.com/o/r`, `https://gitlab.com/o/r`, `https://github.com/o`, `https://github.com/o/r/blob/main/x.cs`, `https://github.com/o/r/issues`, `https://github.com/o/r/tree/`, `https://github.com/-o/r`, `https://github.com/o/..`.
  - `test_resolve_ref`: a fake fetch records URLs; `resolve(Repo("o","r","feature/x"))` asks `https://api.github.com/repos/o/r/commits/feature/x` with accept `application/vnd.github+json`, no ref asks `…/commits/HEAD`; answers its `sha`; 404 → `not_found` `GitHub has no public repository o/r with a branch, tag or commit feature/x`; 404 without ref → `GitHub has no public repository o/r`; 403 and 429 → `fetch` with the limit sentence; a `sha` that is not 40 hex → `fetch`.
  - `test_manifest`: the spec's example parses to two `Item`s; bad TOML → `manifest` starting `hass_sharp.toml is not valid TOML: `; one manifest with an absolute path, `a/../b.cs`, `a\\b.cs`, `./a.cs`, `a//b.cs`, `a.CS`, a library path ending `.json`, an entity path ending `.cs`, a missing name, a 121-character name, a library name with `/`, `id = "Bad"`, a repeated path and a repeated library name → one `manifest` error whose message has each problem, `library 1: …`-numbered within its kind; 51 items → `hass_sharp.toml lists more than 50 items`; unknown keys ignored.
  - `test_definition_id_matches_frontend`: every vector of `FE/tests/entity-model.test.ts:296–324` (`Any motion`→`any_motion`, `  Kitchen -- Plug!! `→`kitchen_plug`, `Café`→`cafe`, `Ølstue`→`lstue`, `'a'*100`→`'a'*80`, `'a'*79+' b'`→`'a'*79`, `New`→`new_2`, taken `['new_2']` → `new_3`, `'a'*100` taken `['a'*80]` → `'a'*78+'_2'`, …).
  - `test_rewrite_library_kinds`: `library:face_detection:global%3A%3ANodeLib.FaceDetection.DetectFaces` with `{"face_detection": "library-1234"}` → `library:library-1234:global%3A%3ANodeLib.FaceDetection.DetectFaces`; `library:other:…`, `camera_frame` unchanged; the input dict is not mutated.
  - `test_entity_body`: a read answer with `id`, `revision`, `entities`, `source` gives only `name` (the manifest's), `settings`, `logic`; no logic / `kind: "class"` / non-JSON / non-object → `ItemProblem` with the sentences of Global Constraints.
  - `test_aiohttp_fetch_caps`: a fake session (async context manager; `.status`, `.url.host`, `.headers`, `.content.iter_chunked`) — body of exactly `CAP` returned whole; `CAP + 1` (no `Content-Length`) → `fetch` `x.cs is larger than 2 MiB` after reading at most `CAP + 64 KiB`; `Content-Length: CAP+1` → refused without reading; status 404 → `HttpStatus(404)`; final host `example.com` → refused; `asyncio.TimeoutError` → `Could not reach GitHub: …`.
- [ ] **Step 2: Run** `python3 -m unittest tests.python.test_external` from the root (or `discover -s tests/python -p test_external.py`). Expected: FAIL (no module).
- [ ] **Step 3: Implement** `CC/external.py` with the interfaces above.
- [ ] **Step 4: Run** the same; then `python3 -m unittest discover -s tests/python`. Expected: PASS.
- [ ] **Step 5: Commit** — `External import: the rules for a GitHub repository's hass_sharp.toml, its paths, sizes and ids.`

---

### Task 3: Inspect and import, and the `hass_sharp/external` route

**Files:**
- Modify: `CC/external.py` (the flow), `CC/authoring.py` (`AuthoringService.external`, after `entities` at :137–166), `CC/websocket.py` (the registration loop :68–77)
- Test: `tests/python/test_external.py` (flow, with a fake backend), `tests/python/test_visual.py` (`WebsocketReadinessTests` :113–: add `hass_sharp/external`), a route test in `tests/python/test_external.py` through `load_routes` (:76–101) with fakes `external=<the loaded module>`, `async_get_clientsession=lambda hass: <fake session>`, `aiohttp=SimpleNamespace(ClientTimeout=lambda **k: k)`.

**Interfaces:**
- Consumes: Task 1's `import` request; Task 2's whole module.
- Produces in `external.py`:
  - `class Backend(Protocol)`: `async libraries() -> list[dict]` (`{id, name, revision}`), `async definitions() -> tuple[list[dict], list[str]]` (summaries `{id, name, revision}`, unreadable ids), `async read_definition(id) -> dict`, `async import_libraries(libraries: list[dict]) -> dict` (the .NET envelope), `async save_definition(request: dict) -> dict` (the envelope `AuthoringService.entities` answers).
  - `async inspect(github: GitHub, backend: Backend, url: str) -> dict` → `{owner, repo, ref, sha, items: [{kind, name, description, path, existing: [{id, revision, name}], problem: str | None}]}`. It resolves the ref, fetches the manifest (404 → the `no hass_sharp.toml` sentence) and then every listed file, so a missing, oversized, non-UTF-8 or malformed file is that item's `problem` (a network failure on a file is the whole answer's `fetch` error).
  - `async import_items(github: GitHub, backend: Backend, request: dict) -> dict`, request `{owner, repo, sha, picks: [{kind, path, mode: "new" | "overwrite", target?: id, expectedRevision?: int}]}` → `{owner, repo, sha, items: [{kind, name, path, outcome: "imported" | "overwritten" | "failed", id: str | None, enabled: bool | None, message: str | None}], libraries: <.NET result or None>}`.
- Produces in `authoring.py`: `async def external(self, request, *, is_admin)` → the envelope (`{"version": 1, "ok": True, "result": …}` or `ExternalError.answer()`); its backend calls `self._request(self.manager.NodeLibraryRequest, …)` for `list` and `import`, `self._request(self.manager.EntityRequest, …)` for `list` and `read`, and `self.entities({...}, is_admin=True)` for each save, so Home Assistant's trigger check and `registeredEntities` apply as for any save. HTTP through `async_get_clientsession(self.hass)` with `aiohttp.ClientTimeout(total=30)`.
- Produces in `websocket.py`: `("external", envelope_command(authoring.external, "External import", "external_failed"))` in the loop, so the route is `hass_sharp/external`, admin-only and refused while not ready.

**Behaviour (import):**
- **Checked before any fetch or write**, each → `invalid_request` with a sentence naming the pick: `owner`/`repo` by Task 2's patterns, `sha` `^[0-9a-f]{40}$`, `picks` a non-empty list, each `kind` and `mode` known, no `(kind, path)` twice; after re-fetching the manifest at `sha`: each path is in it for that kind. Against the current lists: a library pick whose name has existing files must be `overwrite` with `target` one of them and `expectedRevision` an int; one with none must be `new` without `target`; an entity `overwrite` needs `target` a definition of that name and `expectedRevision`.
- **Fetch all picked files first**; a fetch failure refuses the whole import with nothing written.
- **Library ids.** `new` → the manifest `id` when given and no stored library has it, else `f"library-{uuid.uuid4()}"`, the form the panel gives a new file. `overwrite` → `target`.
- **One library request**: `{operation: "import", libraries: [{id, name: item.name, source, expectedRevision: 0 | pick's}]}`. Refused → every library item `failed` with a message from the refusal (`validation`: up to three error diagnostics as `{file name} {line}:{column}: {message}`, then `it would break {automation or definition name}` for each listed, joined `; `; `conflict`: `It changed after you inspected the repository; inspect it again`; else the detail), every entity item `failed` with `Not imported, because the node libraries were not`, and no entity request is made.
- **Kind map** for the entities: manifest library `id` → the id used — the stored id for a picked library; for an unpicked library, the one existing file of that name when there is exactly one; otherwise no entry.
- **Each entity**, in manifest order: body from `entity_body`, graph through `rewrite_library_kinds`; `new` → `save` with `id = definition_id(name, taken)` (taken = listed ids + unreadable ids + ids given earlier in this import), `expectedRevision: 0`, `enabled: false`, outcome `imported`, `enabled: false`; `overwrite` → read the target, mark each imported setting whose `key` and `type` it has with `keep: true` and no `value`, `save` with the pick's `expectedRevision` and no `enabled`, outcome `overwritten`. A refused save is that item's `failed` with up to three error diagnostic messages joined `; ` (or the conflict sentence), and the next entity is still tried. **Libraries already committed stay** (Ruling 4).

- [ ] **Step 1: Write the failing tests** (fake `GitHub` fetch serving a dict of URLs; fake `Backend` recording calls):
  - `test_inspect_lists_items_and_existing`: two stored `FaceDetection.cs` (ids `a`, `b`) and a definition `Face camera` → the library item's `existing` has both, the entity's has one; `sha` is the API's; a manifest entry whose raw URL is 404 has `problem` `x.json is not in the repository at 0123abc`; an oversized `.cs` has `x.cs is larger than 2 MiB`.
  - `test_inspect_errors`: API 404 / 403, raw manifest 404, bad TOML → the `not_found`, `fetch`, `not_found`, `manifest` envelopes; no backend call is a write.
  - `test_import_refetches_the_sha_only`: every fetched URL starts `https://raw.githubusercontent.com/o/r/<sha>/`; none is `api.github.com`.
  - `test_import_libraries_then_entities`: picks library `new` + entity `new` → backend calls in order: `import_libraries` once with `[{id: "face_detection", name: "FaceDetection.cs", …, expectedRevision: 0}]`, then `save_definition` with `id: "face_camera"`, `expectedRevision: 0`, `enabled: False`; outcomes `imported`, `imported` (enabled False).
  - `test_kinds_follow_the_library_actually_used` (Review Focus 1): stored `FaceDetection.cs` as `library-1234`; overwrite it → the saved graph's node kind is `library:library-1234:global%3A%3ANodeLib.FaceDetection.DetectFaces`; with the library unpicked (Skip) the same; with two same-named files and the library unpicked, the kind is unchanged.
  - `test_manifest_id_taken_gives_a_new_id`: a stored library `face_detection` named `Other.cs` → the new library's id matches `^library-[0-9a-f-]{36}$` and the graph's kinds use it.
  - `test_library_overwrite_only`: library `new` while a same-named file exists, `overwrite` with a `target` not of that name, a path not in the manifest, a repeated pick → `invalid_request`; no backend write.
  - `test_refused_libraries_stop_entities`: `import_libraries` answers `validation` with one error diagnostic → library item `failed` `FaceDetection.cs 3:5: …`, entity item `failed` `Not imported, because the node libraries were not`, no `save_definition` call.
  - `test_entity_failure_keeps_libraries`: libraries succeed, the first entity's save answers `validation`, the second succeeds → outcomes `imported`, `failed` (its diagnostic message), `imported`; no library request after the import.
  - `test_overwrite_keeps_setting_values` (Review Focus 4): target has `address` (string, secret) and `interval` (number); the file has `address` (string) and `interval` (string) and `extra` → saved `address` with `keep: True` and no `value`, `interval` with the file's value, `extra` as given; `expectedRevision` the pick's.
  - `test_new_ids_avoid_taken_and_each_other`: two entities named `Face camera` imported `new` with `face_camera` listed and `face_camera_2` unreadable → ids `face_camera_3`, `face_camera_4`.
  - Route: `test_external_route_is_admin_and_envelope`: non-admin → `unauthorized`, no fetch; admin `inspect` through `hass_sharp/external` answers the flow's result; an unknown `operation` → `invalid_request` `operation must be inspect or import`. `WebsocketReadinessTests`: `hass_sharp/external` answers `not_ready` while no entry is loaded.
- [ ] **Step 2: Run** `python3 -m unittest discover -s tests/python -p 'test_external.py'` and `-p 'test_visual.py'`. Expected: FAIL.
- [ ] **Step 3: Implement** the flow, `AuthoringService.external` and the registration.
- [ ] **Step 4: Run** `python3 -m unittest discover -s tests/python`. Expected: PASS. Also in the container (AGENTS.md "To run them against the local container's Home Assistant") if the host lacks Home Assistant 2026.11.
- [ ] **Step 5: Commit** — `External import: hass_sharp/external inspects a GitHub repository and imports what was picked, libraries first.`

---

### Task 4: This repo's `hass_sharp.toml` and the Face camera definition

**Files:**
- Create: `hass_sharp.toml` (repository root), `nodes/face_detection/face_camera.json`
- Test: `tests/Runtime/NodeLibraryImportTests.cs` (one more case), `tests/python/test_external.py` (one more case)

**Interfaces:**
- Consumes: `nodes/face_detection/FaceDetection.cs` (Project A); Task 2's `parse_manifest`, `entity_body`, `check_path`, `CAP`.
- Produces, `hass_sharp.toml` exactly (the docs quote it; Task 6 tests that):

```toml
[[library]]
name = "FaceDetection.cs"
id = "face_detection"
path = "nodes/face_detection/FaceDetection.cs"
description = "Detect faces: box + 5 landmarks (YuNet on tract)"

[[entity]]
name = "Face camera"
path = "nodes/face_detection/face_camera.json"
description = "Camera that marks faces"
```

- Produces, `face_camera.json`: `{name: "Face camera", settings: [], logic: {kind: "graph", graph}}`, the graph shaped as "Camera with a box" in `CC/authoring_examples.json` (`version` as that example has it): nodes `source` (`ha_entity`, `config.entity` `camera.front_door`), `frame` (`camera_frame`, `config.entity` `{kind: "unset"}`), `faces` (kind `library:face_detection:global%3A%3ANodeLib.FaceDetection.DetectFaces`, `config.arguments` `{}` so the defaults 0.7 and 320 apply), `face_camera` (`entity`, key `face_camera`, domain `camera`, name `Face camera`); data edges `source.entity → frame.entity`, `frame.image → faces.image`, `faces.result → face_camera.image`; positions 60/300/540/780 at y 100.

- [ ] **Step 1: Write the failing tests:**
  - Runtime `TheRepositoryFaceCameraPreviews`: save `nodes/face_detection/FaceDetection.cs` as id `face_detection`, name `FaceDetection.cs`; an entity `preview` of `face_camera.json`'s body is `valid: true` (an unknown-entity warning for `camera.front_door` allowed, no error). Paths from the repository root as other suites read `tests/fixtures`.
  - Python `test_repository_manifest`: `parse_manifest(Path("hass_sharp.toml").read_bytes())` gives the two items; each path exists, is under `CAP`, and `entity_body` accepts the JSON; the library's `id` equals the source-id segment of the `faces` node's kind.
- [ ] **Step 2: Run** both. Expected: FAIL (no files).
- [ ] **Step 3: Write** the two files.
- [ ] **Step 4: Run** both, then `python3 -m unittest discover -s tests` (the release package does not change shape: the root file and `nodes/` are outside `CC/`). Expected: PASS.
- [ ] **Step 5: Commit** — `The repository offers FaceDetection.cs and a Face camera definition through hass_sharp.toml.`

---

### Task 5: The Add external dialog and its two buttons

**Files:**
- Create: `FE/src/external/api.ts`, `FE/src/external/AddExternalDialog.tsx`, `FE/tests/add-external.test.tsx`
- Modify: `FE/src/views/NodeLibraryView.tsx` (:112, beside `Add file`), `FE/src/views/EntitiesListView.tsx` (:155–157, heading actions beside `New entity definition`), styles beside the existing `hs-` dialog classes if needed
- Test: `FE/tests/NodeLibraryView.test.tsx`, `FE/tests/EntitiesListView.test.tsx`

**Interfaces:**
- Consumes: Task 3's answers.
- Produces, `api.ts`: types `ExternalItem`, `ExternalInspection`, `ExternalPick`, `ExternalImportItem`, `ExternalImportResult` mirroring Task 3; `externalApi(hass: Pick<HomeAssistant, 'callWS'>)` with `inspect(url: string): Promise<ExternalInspection>` and `import(request: { owner: string; repo: string; sha: string; picks: ExternalPick[] }): Promise<ExternalImportResult>` over `callWS({type: 'hass_sharp/external', request: {operation, …}})`; a refused envelope or Home Assistant's `{code, message}` rejects with an `Error` whose message is the detail sentence.
- Produces, `AddExternalDialog`: props `{ open: boolean; onClose: () => void; onImported: (result: ExternalImportResult) => void }`; built on `components/Dialog.tsx`, `preventClose` while inspecting or importing.

**Behaviour:**
- Steps: URL (field + `Inspect`, Enter submits) → list → result. `<owner>/<repo>` and the sha's first 7 characters (and the ref when given) head the list; items grouped under `Node libraries` and `Entity definitions` (a group with no items is not shown), each with a checkbox labelled by its name, its description, `View on GitHub` → `https://github.com/{owner}/{repo}/blob/{sha}/{path}` (each path part `encodeURIComponent`ed), `target="_blank" rel="noreferrer"`.
- Defaults: an item with no `existing` is checked; one with `existing` is unchecked (Skip), and checking it shows a select: libraries `Overwrite {name} ({id})` per existing; entities those plus `Add as new`. An item with a `problem` is disabled and shows it. `Import` is disabled when nothing is checked.
- The picks sent: library → `overwrite` with `target` and `expectedRevision` of the chosen existing file, else `new`; entity → `overwrite` likewise, `Add as new` and no-existing → `new`.
- Result: one line per item — `{name}: imported`, `{name}: imported, turned off`, `{name}: overwritten`, `{name}: not imported — {message}`; then `onImported(result)`; footer `Close`.
- A refused inspect or import shows the sentence (`role="alert"`) and returns to the step it came from, closable (Review Focus 3). The warning of Global Constraints is on every step.
- `NodeLibraryView`: `Add external` beside `Add file`; `onImported` → `session.refresh()`. `EntitiesListView`: `Add external` in the heading actions when `admin()`; `onImported` → the list's `refresh()`.

- [ ] **Step 1: Write the failing tests** in `add-external.test.tsx` (vitest + happy-dom, a fake `callWS` answering `hass_sharp/external` from a script and recording requests, `hold` to delay a reply):
  - `inspects, picks and imports`: type the URL, Inspect → both groups, the warning text exactly, links `https://github.com/o/r/blob/<sha>/nodes/face_detection/FaceDetection.cs`; Import sends `{operation: 'import', owner: 'o', repo: 'r', sha, picks: [{kind: 'library', path, mode: 'new'}, {kind: 'entity', path, mode: 'new'}]}`; the result lines read `FaceDetection.cs: imported` and `Face camera: imported, turned off`; `onImported` called once.
  - `existing items start skipped`: an entity with one existing → unchecked, Import disabled; checking it shows `Overwrite Face camera (face_camera)` and `Add as new`; choosing `Add as new` sends `mode: 'new'`; choosing Overwrite sends `{mode: 'overwrite', target: 'face_camera', expectedRevision: 3}`.
  - `a library offers no second copy`: a library with two existing → options are exactly `Overwrite FaceDetection.cs (a)`, `Overwrite FaceDetection.cs (b)` (no `Add as new`); choosing `b` sends `target: 'b'`.
  - `an item with a problem cannot be picked`: its checkbox disabled, the problem shown.
  - `cannot close while fetching, and recovers from a refusal` (Review Focus 3): with inspect held, Escape and Cancel do not close; release with the `fetch` limit sentence → alert shows it, the URL field is back, Cancel closes.
  - `NodeLibraryView.test.tsx`: `Add external` opens the dialog; after an import that overwrote `shared` while `Shared.cs` had an unsaved edit, the file list shows the revision-conflict mark (`aria-label="Revision conflict"`) and the draft is kept (Review Focus 5); a `list` request follows the import.
  - `EntitiesListView.test.tsx`: `Add external` is shown for an admin and not otherwise; an import refetches the list (`list` with `view: 'home'` requested again).
- [ ] **Step 2: Run** `(cd custom_components/hass_sharp/frontend && bun run test -- add-external NodeLibraryView EntitiesListView)`. Expected: FAIL.
- [ ] **Step 3: Implement** `api.ts`, the dialog and the two buttons.
- [ ] **Step 4: Run** `bun run test` and `bunx tsc --noEmit` in `FE/`. Expected: PASS.
- [ ] **Step 5: Commit** — `The panel's Add external dialog imports node libraries and entity definitions from a GitHub repository.`

---

### Task 6: Documents

**Files:**
- Modify: `docs/node-libraries.md` (new sections after "Node libraries", before "Entity classes" at :579), `docs/custom-entities.md` (its own `## Import from GitHub` before "Run history" at :1064)
- Test: `tests/test_docs_examples.py`

**Behaviour:**
- `docs/node-libraries.md` `## Import from GitHub`: the button in both views; the URL forms; what Inspect shows (one commit; existing items unchecked); the choices (libraries Overwrite or Skip only, and why: one `NodeLib` namespace; entities Overwrite / Add as new / Skip; new definitions come in turned off, with an id from their name); libraries first and atomically, then each definition, and that a definition that fails leaves the imported libraries in place, its line saying why; Overwrite keeps the values of settings whose key and type still match; a one-time copy, nothing tracks the repository; the warning sentence of Global Constraints; GitHub's 60-requests-an-hour limit for inspecting.
- `### Writing a hass_sharp.toml`: the fields (`[[library]]` `name`, `path`, `description`, `id`; `[[entity]]` `name`, `path`, `description`), the path rules, the 2 MiB and 50-item caps, the root only; an entity file is a definition body (`name`, `settings`, `logic`), the manifest's name winning, a read answer acceptable (its other members are dropped); and **library ids**: a library node's kind carries its library's id, so set `id` to the id the definitions' kinds use (`library:<id>:…`), and the import rewrites those kinds to the id the library gets. This repo's `hass_sharp.toml` quoted in full after the line `This repository's own manifest:`.
- `docs/custom-entities.md`: a short section that a definition can be imported with **Add external**, comes in turned off, and links to `node-libraries.md#import-from-github`; the Face camera example (choose your camera in its Home Assistant entity node, then turn it on).
- `tests/test_docs_examples.py` `test_manifest_in_the_docs_is_the_repositorys`: `fenced_block_after(node-libraries.md, "This repository's own manifest:", "toml")` equals `hass_sharp.toml` (trailing newline aside). The existing link check covers the new anchors.

- [ ] **Step 1: Write the failing test.** **Step 2: Run** `python3 -m unittest discover -s tests`, expect FAIL. **Step 3: Write** the sections. **Step 4: Run** `python3 -m unittest discover -s tests`, expect PASS. **Step 5: Commit** — `The documents describe Add external and how to write a hass_sharp.toml.`

---

### Task 7: Live check on the local instance

No product change; a fix found here goes back to the task that owns it, with a test. Local container only (port 5001, container `hass`); never the `mcp__hass__*` tools. No pushes.

- [ ] **Step 1: Build and restart.** `dotnet publish -c Release ./custom_components/hass_sharp/HassSharp -o ./custom_components/hass_sharp/out`; `(cd custom_components/hass_sharp/frontend && bun run build)`; `docker compose restart -t 60 hass` (a hard refresh alone serves the old bundle: the panel's bundle URL is fixed at startup); `docker compose logs --tail=200 hass` shows the integration loaded.
- [ ] **Step 2: Before the merge** (the branch is not on GitHub's main): at <http://localhost:5001>, HassSharp panel → Node libraries → **Add external**: `https://github.com/dompagoj/hass-sharp` → the message `dompagoj/hass-sharp has no hass_sharp.toml at its root at <sha7>` (proves the container reaches the API and raw hosts and resolves `HEAD`); `https://github.com/dompagoj/hass-sharp/tree/no-such-branch` → the ref sentence; `https://gitlab.com/x/y` → the URL sentence; Cancel works after each. Report these; this is all that can be checked before the merge.
- [ ] **Step 3: After the owner merges to main** (the owner says so; until then leave this step open in the report): Add external with `https://github.com/dompagoj/hass-sharp` lists `FaceDetection.cs` and `Face camera`, each with a working `View on GitHub` link at the sha. If Project A's live check left a `FaceDetection.cs`, it is offered Overwrite only. Import both → `FaceDetection.cs: imported` (or `overwritten`) and `Face camera: imported, turned off`; the library list shows the file; Entities shows **Face camera** off. Open it: its Detect faces node is a known kind (no "missing library node"); set its Home Assistant entity to a real camera, save, turn it on; the camera shows boxes and five points; the logs show one native download (`docker compose logs --tail=200 hass`). Inspect again: both items now unchecked with their Overwrite choices.
- [ ] **Step 4: Report** what was seen at each step, with log lines, and anything that did not match.

---

## Rulings

1. **The kind-id problem is real, and the manifest names each library's id.** A library node's kind is `library:<library document id>:<type>.<method>`, and a file added in the panel gets `library-<uuid>`, so a definition in a repo cannot name the id the importing instance will use. The `[[library]]` table takes an optional `id` (the spec's manifest did not have one); a new library is stored under it when no stored library has it (else `library-<uuid>`), and import rewrites the id segment of `library:` kinds in imported definitions to the id actually used — a new file's, the overwritten file's, or, for an unpicked library, its one same-named existing file's. Alternatives rejected: kinds by type and method alone (changes every saved graph's kind and the catalog contract for an import feature); always storing under the manifest id (impossible on Overwrite of a file with another id). Cost if wrong: one optional manifest key and a twenty-line rewrite.
2. **HTTP through Home Assistant's aiohttp session, not urllib in an executor** (the spec said executor). `async_get_clientsession` is the idiomatic Home Assistant client: no thread per request, Home Assistant's SSL context and proxy settings, cancellation on unload, and a 30 s `ClientTimeout`. `external.py` stays testable by taking the session duck-typed. Cost if wrong: swap the fetcher.
3. **`HEAD` for the default branch**, one API call per inspect and none per import (GitHub allows 60 unauthenticated calls an hour per address). Cost if wrong: a second call to read `default_branch`.
4. **A failed definition does not undo the libraries.** They were valid and compiled together, every automation and definition was rebuilt against them, and undoing them is a second rebuild-everything commit that can itself fail; a definition is saved one at a time by the existing save, so the result lists each item and says why one failed, and that one can be fixed and saved or imported again. A refused library import, by contrast, stops before any definition. Cost if wrong: a user removes a library by hand.
5. **Inspect fetches every listed file**, so a missing or malformed file is shown before Import (`problem`, an addition to the spec's item shape); import fetches the picked ones again at the same sha, all before any write, so a network failure writes nothing.
6. **Picks carry `expectedRevision`** for Overwrite (the spec's pick had `target` only), taken from inspect's `existing`: a library or definition changed between Inspect and Import is a conflict, not silently replaced.
7. **Overwrite keeps setting values whose key and type still match** (`keep: true`), so a chosen address or a secret survives; a new or retyped setting takes the file's. Not in the spec. Cost if wrong: drop the marking.
8. **The manifest's entity `name` is the definition's name**, overriding the file's, and is what "existing" matches; only `name`, `settings`, `logic` are taken from the file, so an exported read answer works.
9. **The new definition id is worked out in Python** by a port of `definitionId`, pinned to the frontend's test vectors, so the import needs no round trip to the panel. Cost if wrong: drift between two copies, caught by the shared vectors.
10. **Caps beyond the spec's**: at most 50 manifest items (bounds memory at 100 MiB worst case and the time an unload may wait for an in-flight import); redirects only within `api.github.com` and `raw.githubusercontent.com`; unknown manifest keys ignored for forward compatibility.
11. **Existing items default to Skip** in the dialog; re-inspecting an imported repo and pressing nothing imports nothing.
12. **Not in MCP.** `import` is a panel feature; `llm.py`'s tools are unchanged.
