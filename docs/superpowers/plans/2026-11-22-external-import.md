# "Add external": import node libraries and entity definitions from a GitHub repo

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Paste a public GitHub repository's URL in the panel; HassSharp reads `hass_sharp.toml` at the repo's root at one commit, lists the node libraries and entity definitions it names, and imports the ones picked: the libraries in one atomic save, then each definition through the existing entity save, new ones turned off. hass-sharp-vision (`https://github.com/dompagoj/hass-sharp-vision`) gets a root `hass_sharp.toml` offering `Vision.cs`, `PeopleDetector.cs` and a "Front door people" definition (a People detector on `camera.front_door`), and is the first repository imported for real.

**Architecture:** (1) .NET gets one new library operation, `import`, which commits several library documents through the same preview, rebuild and `AuthoringFiles.Commit` that `save` uses, so libraries that depend on each other compile together; (2) a new stdlib-only Python module `external.py` holds every rule (URL forms, manifest, paths, caps, ids, the library-kind rewrite, what Overwrite keeps) and the inspect/import flow over two injected seams, a fetcher and a backend; (3) `AuthoringService.external` wires those seams to Home Assistant's aiohttp session and to the .NET requests it already makes, and `websocket.py` registers `hass_sharp/external` like the other envelope routes; (4) the frontend gets one shared dialog opened by an **Add external** button in two views; (5) hass-sharp-vision's manifest and example definition, in that repository; (6) the docs.

**Tech Stack:** C# on .NET 11 (`CC/HassSharp/`, custom runner `tests/Runtime/`), Python 3.13+ (`tomllib`, Home Assistant's aiohttp session; `tests/python/`), SolidJS + TypeScript 7 + vitest 5 + happy-dom (`FE/`). `CC/` below is hass-sharp's `custom_components/hass_sharp/`, `FE/` is `CC/frontend/`. `HV/` is hass-sharp-vision's root, cloned at `/Users/dompa/dev/hass-sharp-vision`; `HS/` is this checkout, `/Users/dompa/dev/HassSharp`.

Each task names its repository: **[hass-sharp]** for Tasks 1–3 and 5–7, **[hass-sharp-vision]** for Task 4 (owner's decision of 2026-11-22, `.superpowers/sdd/2026-11-22-vision/split-decisions.md`).

**Spec:** `docs/superpowers/specs/2026-11-22-face-detection-external-import-design.md`, sections 5–10 and the Project B lines of Verification. Project A (`docs/superpowers/plans/2026-11-22-vision.md`: the Rust library, `[NodeSetup]`, `DrawCircle`, the vision library files) is a separate plan that lands first; its vision files and their C# test project are in hass-sharp-vision. This plan assumes, in namespace `NodeLib`:
- `HV/nodes/Vision.cs` with `public sealed class VisionDetector`, `public static class Vision` and `public static class FaceDetection` holding `[Node("Detect faces", Category = "Vision", Pure = true)] public static SharpImage DetectFaces(SharpImage image, double minScore = 0.7, int inputSize = 320)`; its kind, stored under id `vision`, is `library:vision:global%3A%3ANodeLib.FaceDetection.DetectFaces`.
- `HV/nodes/PeopleDetector.cs` with `public abstract class PeopleDetector : SharpDevice`, which uses `Vision` and `VisionDetector` (so it does not compile without `Vision.cs`), and a required `CameraEntity` setting labelled `Camera` whose property (the pin id) is `Camera`. If Project A names the property otherwise, Task 4 uses that name as the argument key.
- `HV/tests/Vision.Tests/`, Project A's C# test project (its Task 10): it builds against a hass-sharp checkout given by `-p:HassSharpRoot=…`, compiles hass-sharp's `tests/Runtime/` with it, and offers `VisionTests.Root` and `VisionTests.Fixture(name)`.

## Global Constraints

- **Every commit leaves its repository's suites green.**
  - **hass-sharp**, from `HS/`: `python3 -m unittest discover -s tests/python`; `python3 -m unittest discover -s tests`; `dotnet run --project tests/Runtime --disable-build-servers`; `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests`; `(cd custom_components/hass_sharp/frontend && bun run test)` and `(cd custom_components/hass_sharp/frontend && bunx tsc --noEmit)`.
  - **hass-sharp-vision** (Task 4), from `HV/`: Project A's suites (its Global Constraints), with `--vision-manifest` added to the C# flags and `HASS_SHARP_ROOT=/Users/dompa/dev/HassSharp` set for the Python tests.
- **No pushes, tags or releases by an implementer**, in either repository. Commit locally only. The live import from `https://github.com/dompagoj/hass-sharp-vision` waits for the owner's go to push and release that repository (Project A's Task 15, and a push of Task 4's commit), not for a hass-sharp merge (Task 7).
- **Never the `mcp__hass__*` tools**: they reach the production Home Assistant. Live checks use the local container only (port 5001, container `hass`).
- **GitHub only.** Accepted: `https://github.com/<owner>/<repo>` with an optional `/tree/<ref>`. Ref → sha with `GET https://api.github.com/repos/{owner}/{repo}/commits/{ref}` (`HEAD` when no ref is given: GitHub resolves it to the default branch). Files only from `https://raw.githubusercontent.com/{owner}/{repo}/{sha}/{path}`. No git process, no tarball.
- **Caps and path rules.** Each fetched file, `hass_sharp.toml` included, at most `2 * 1024 * 1024` bytes (`CAP`); at most 50 items in a manifest (`MAX_ITEMS`). A path is relative, `/`-separated, with no leading `/`, no `\`, no empty, `.` or `..` part; a library's ends in `.cs`, an entity's in `.json` (case-sensitive).
- **Library choices:** Import when no library of that file name exists; otherwise only Overwrite (of one of the same-named files, the user picks which when there are several) or Skip. Never a second copy: all library files share the `NodeLib` namespace.
- **Entity choices:** Import when no definition of that name exists; otherwise Overwrite / Add as new / Skip. A new one is saved with `expectedRevision: 0` and `enabled: false`, its id by the `definitionId()` rule (`FE/src/entities/model.ts:333`). "Existing" matches by library file name and by definition name, exactly.
- **Import re-fetches the inspected sha**; it makes no API call. Libraries first (one `import` request to .NET, atomic, every picked library compiled together with the stored ones), then entities one by one.
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

1. **A definition that uses a library node, imported where the library gets another id.** A library node's kind is `library:<library document id>:<type>.<method>` (`NodeLibraryCompiler.cs:391`), and the panel gives a new file the id `library-<uuid>` (`FE/src/visual/library-session.ts:189`), so the repo cannot know it. The manifest names a library `id`, and import rewrites the kinds (Ruling 1). Expected: a graph camera using Detect faces, saved after overwriting a `Vision.cs` stored as `library-1234`, runs `library:library-1234:global%3A%3ANodeLib.FaceDetection.DetectFaces`. Tests in Tasks 2 and 3. (hass-sharp-vision's own example uses no library kind: Ruling 13.)
2. **`PeopleDetector.cs` picked without `Vision.cs`.** Expected: when `Vision.cs` is already stored, it imports (the candidate set holds the stored files); when it is not, the import is refused with the compiler's message naming `PeopleDetector.cs`, nothing written, and the definition not attempted. Tests in Tasks 1, 3 and 4.
3. **A URL copied from the browser** with a trailing `/`, `.git`, `?tab=readme-ov-file`, `#readme`, `www.github.com`, or `/tree/feature/x` (a ref with a slash). Expected: accepted as the repo (and the ref). Test in Task 2.
4. **GitHub's limit for requests without signing in, or no network in the container.** Expected: the plain sentence, and the dialog back at its URL step and closable (not stuck under `preventClose`). Tests in Tasks 2 and 5.
5. **Overwriting a definition the user filled in**: definition settings (an address, a secret) and a C# device's inputs typed into its node (the People detector's **Camera**). Expected: each imported setting whose key and type the existing definition has keeps its stored value (`keep: true`), a secret is never lost, and the device keeps the camera the user picked (Ruling 7). Test in Task 3.

---

### Task 1: The library `import` operation

**Repository:** hass-sharp.

**Files:**
- Modify: `CC/HassSharp/Visual/NodeLibraryApi.cs` (operation dispatch before `var id = …` at :202; the commit tail of `save` at :256–289)
- Create: `tests/Runtime/NodeLibraryImportTests.cs`

**Interfaces:**
- Produces, request: `{operation: "import", libraries: [{id, name, source, expectedRevision}]}` — one or more; each `id` passes `NodeLibraryStore.ValidateId`, each `name` `ValidateName`, ids distinct within the request; `expectedRevision` the stored revision (0 for a new id).
- Produces, answer: `{saved: true, documents: [NodeLibraryDocument, …] (in request order, each at its new revision), generation, catalog, automations, invalid, definitions, invalidDefinitions}` — the members `save` answers beside `document`, with the same meaning. A refusal is exactly `save`'s: `validation` with `{diagnostics, automations, invalid, definitions}`, `conflict` when any revision differs, `invalid_request` for a bad field, `storage`.
- Internal: the tail of `save` (candidates → compile → rebuild graphs → `CheckDefinitions` → `AuthoringFiles.Commit` → activation) becomes one private helper taking the documents written and the id deleted (or none); `save`, `delete` and `import` all go through it. `save`'s and `delete`'s answers are byte-identical to today's.

**Behaviour:**
- One candidate set (saved documents minus those replaced, plus the imported ones, ordered by id), one `Preview`, one rebuild of every graph and definition, one `Commit` holding every library file and every rebuilt document. Nothing is written unless all of it is valid. A library that uses another's types compiles when the other is in the same request or already stored.
- Messages: an empty `libraries` → `invalid_request` `libraries must list at least one library`; a repeated id → `invalid_request` `Each library may be imported once: {id}`.
- Not exposed to MCP (`llm.py` unchanged).

- [ ] **Step 1: Write the failing suite** `[RuntimeSuite("--node-library-import", Order = 41)]` in `NodeLibraryImportTests.cs`, helpers as `NodeLibraryStoreTests` has (`Request`, `Result`, a temp `HassSharpManager`). The dependent pair mirrors the vision files' shape: `A.cs` declares `public record Reading(int Value);` and `public static class Meter { public static Reading Read() => new(1); }`; `B.cs` declares `public abstract class MeterDevice : SharpDevice { protected Reading Last() => Meter.Read(); }` and a `[Node("Use reading")]` taking `Reading`.
  - `TwoDependentLibrariesImportTogether`: importing both (rev 0) succeeds, `documents` has both at revision 1 in request order, `list` shows both; on a fresh manager, saving `B.cs` alone first (rev 0) is refused with `validation` (proves the single compile).
  - `ADependentImportsAfterItsDependencyIsStored`: `A.cs` saved through `save`, then an `import` of `B.cs` alone succeeds; on a fresh manager an `import` of `B.cs` alone is `validation` with a diagnostic whose file is `B.cs`, and `list` is empty (Review Focus 2).
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

**Repository:** hass-sharp.

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
- `rewrite_library_kinds(graph: dict, ids: dict[str, str]) -> dict` — a deep copy in which each node `kind` `library:<a>:<rest>` with `a` in `ids` becomes `library:<ids[a]>:<rest>`; nothing else changes (a `csharp_device` node, whose code names a library class by its name, is left as it is: Ruling 13). (Both ids match the id pattern, so `Uri.EscapeDataString` leaves them as they are.)
- `library_source(item: Item, data: bytes) -> str` and `entity_body(item: Item, data: bytes) -> dict` — raise `ItemProblem(message)`; `entity_body` returns `{"name": item.name, "settings": file.get("settings") or [], "logic": file["logic"]}` (only these three: a read answer's `id`, `revision`, `entities`, `devices`, `source` are dropped) and requires `logic` to be an object with `kind == "graph"` and a `graph` object.
- `Fetch = Callable[[str, str], Awaitable[bytes]]` — `(url, accept)`; returns at most `CAP` bytes or raises `HttpStatus`, or `ExternalError("fetch", …)` for a body over `CAP` (`{name} is larger than 2 MiB`, name = the last path part) or a failure to connect.
- `aiohttp_fetch(session, timeout) -> Fetch` — `session.get(url, headers={"Accept": accept, "User-Agent": "HassSharp"}, timeout=timeout)`; refuses a final `response.url.host` outside `ALLOWED_HOSTS` (`Could not reach GitHub: it sent the request to {host}`); checks `Content-Length` first, then reads `response.content` in chunks and stops past `CAP`; `TimeoutError`/client errors → `Could not reach GitHub: {reason}`. Duck-typed: the module never imports aiohttp.
- `class GitHub: __init__(self, fetch: Fetch)`; `async resolve(repo: Repo) -> str` (40-hex sha from the API's `sha`; 404/422 → the `not_found` sentences; 403/429 → the limit sentence); `async file(owner, repo, sha, path) -> bytes` (404 → `HttpStatus` passed up for the caller to word).

- [ ] **Step 1: Write the failing tests** in `test_external.py` (module loaded by path):
  - `test_url_forms`: `https://github.com/o/r`, `…/r/`, `…/r.git`, `https://www.github.com/o/r`, `  https://github.com/o/r?tab=readme-ov-file#readme `→ `Repo("o","r",None)`; `…/tree/main` → ref `main`; `…/tree/feature/x` → ref `feature/x`. Refused with the `invalid_request` sentence: `http://github.com/o/r`, `https://gitlab.com/o/r`, `https://github.com/o`, `https://github.com/o/r/blob/main/x.cs`, `https://github.com/o/r/issues`, `https://github.com/o/r/tree/`, `https://github.com/-o/r`, `https://github.com/o/..`.
  - `test_resolve_ref`: a fake fetch records URLs; `resolve(Repo("o","r","feature/x"))` asks `https://api.github.com/repos/o/r/commits/feature/x` with accept `application/vnd.github+json`, no ref asks `…/commits/HEAD`; answers its `sha`; 404 → `not_found` `GitHub has no public repository o/r with a branch, tag or commit feature/x`; 404 without ref → `GitHub has no public repository o/r`; 403 and 429 → `fetch` with the limit sentence; a `sha` that is not 40 hex → `fetch`.
  - `test_manifest`: the spec's §6 example (hass-sharp-vision's manifest) parses to three `Item`s — `Item("library", "Vision.cs", "nodes/Vision.cs", …, id="vision")`, `Item("library", "PeopleDetector.cs", "nodes/PeopleDetector.cs", …, id="people_detector")`, `Item("entity", "Front door people", "nodes/front_door_people.json", …, id=None)`; bad TOML → `manifest` starting `hass_sharp.toml is not valid TOML: `; one manifest with an absolute path, `a/../b.cs`, `a\\b.cs`, `./a.cs`, `a//b.cs`, `a.CS`, a library path ending `.json`, an entity path ending `.cs`, a missing name, a 121-character name, a library name with `/`, `id = "Bad"`, a repeated path, a repeated library name and a repeated library id → one `manifest` error whose message has each problem, `library 1: …`-numbered within its kind; 51 items → `hass_sharp.toml lists more than 50 items`; unknown keys ignored.
  - `test_definition_id_matches_frontend`: every vector of `FE/tests/entity-model.test.ts:296–324` (`Any motion`→`any_motion`, `  Kitchen -- Plug!! `→`kitchen_plug`, `Café`→`cafe`, `Ølstue`→`lstue`, `'a'*100`→`'a'*80`, `'a'*79+' b'`→`'a'*79`, `New`→`new_2`, taken `['new_2']` → `new_3`, `'a'*100` taken `['a'*80]` → `'a'*78+'_2'`, …).
  - `test_rewrite_library_kinds`: `library:vision:global%3A%3ANodeLib.FaceDetection.DetectFaces` with `{"vision": "library-1234"}` → `library:library-1234:global%3A%3ANodeLib.FaceDetection.DetectFaces`; `library:other:…`, `camera_frame` and a `csharp_device` node whose source is `public class FrontDoorPeople : PeopleDetector { }` unchanged; the input dict is not mutated.
  - `test_entity_body`: a read answer with `id`, `revision`, `entities`, `source` gives only `name` (the manifest's), `settings`, `logic`; no logic / `kind: "class"` / non-JSON / non-object → `ItemProblem` with the sentences of Global Constraints.
  - `test_aiohttp_fetch_caps`: a fake session (async context manager; `.status`, `.url.host`, `.headers`, `.content.iter_chunked`) — body of exactly `CAP` returned whole; `CAP + 1` (no `Content-Length`) → `fetch` `x.cs is larger than 2 MiB` after reading at most `CAP + 64 KiB`; `Content-Length: CAP+1` → refused without reading; status 404 → `HttpStatus(404)`; final host `example.com` → refused; `asyncio.TimeoutError` → `Could not reach GitHub: …`.
- [ ] **Step 2: Run** `python3 -m unittest tests.python.test_external` from the root (or `discover -s tests/python -p test_external.py`). Expected: FAIL (no module).
- [ ] **Step 3: Implement** `CC/external.py` with the interfaces above.
- [ ] **Step 4: Run** the same; then `python3 -m unittest discover -s tests/python`. Expected: PASS.
- [ ] **Step 5: Commit** — `External import: the rules for a GitHub repository's hass_sharp.toml, its paths, sizes and ids.`

---

### Task 3: Inspect and import, and the `hass_sharp/external` route

**Repository:** hass-sharp.

**Files:**
- Modify: `CC/external.py` (the flow), `CC/authoring.py` (`AuthoringService.external`, after `entities` at :137–166), `CC/websocket.py` (the registration loop :68–77)
- Test: `tests/python/test_external.py` (flow, with a fake backend), `tests/python/test_visual.py` (`WebsocketReadinessTests` :113–: add `hass_sharp/external`), a route test in `tests/python/test_external.py` through `load_routes` (:76–101) with fakes `external=<the loaded module>`, `async_get_clientsession=lambda hass: <fake session>`, `aiohttp=SimpleNamespace(ClientTimeout=lambda **k: k)`.

**Interfaces:**
- Consumes: Task 1's `import` request; Task 2's whole module.
- Produces in `external.py`:
  - `class Backend(Protocol)`: `async libraries() -> list[dict]` (`{id, name, revision}`), `async definitions() -> tuple[list[dict], list[str]]` (summaries `{id, name, revision}`, unreadable ids), `async read_definition(id) -> dict`, `async import_libraries(libraries: list[dict]) -> dict` (the .NET envelope), `async save_definition(request: dict) -> dict` (the envelope `AuthoringService.entities` answers).
  - `async inspect(github: GitHub, backend: Backend, url: str) -> dict` → `{owner, repo, ref, sha, items: [{kind, name, description, path, existing: [{id, revision, name}], problem: str | None}]}`. It resolves the ref, fetches the manifest (404 → the `no hass_sharp.toml` sentence) and then every listed file, so a missing, oversized, non-UTF-8 or malformed file is that item's `problem` (a network failure on a file is the whole answer's `fetch` error).
  - `async import_items(github: GitHub, backend: Backend, request: dict) -> dict`, request `{owner, repo, sha, picks: [{kind, path, mode: "new" | "overwrite", target?: id, expectedRevision?: int}]}` → `{owner, repo, sha, items: [{kind, name, path, outcome: "imported" | "overwritten" | "failed", id: str | None, enabled: bool | None, message: str | None}], libraries: <.NET result or None>}`.
  - `keep_device_inputs(imported: dict, existing: dict) -> dict` — a deep copy of the imported graph with Ruling 7's node arguments taken from the existing graph.
- Produces in `authoring.py`: `async def external(self, request, *, is_admin)` → the envelope (`{"version": 1, "ok": True, "result": …}` or `ExternalError.answer()`); its backend calls `self._request(self.manager.NodeLibraryRequest, …)` for `list` and `import`, `self._request(self.manager.EntityRequest, …)` for `list` and `read`, and `self.entities({...}, is_admin=True)` for each save, so Home Assistant's trigger check and `registeredEntities` apply as for any save. HTTP through `async_get_clientsession(self.hass)` with `aiohttp.ClientTimeout(total=30)`.
- Produces in `websocket.py`: `("external", envelope_command(authoring.external, "External import", "external_failed"))` in the loop, so the route is `hass_sharp/external`, admin-only and refused while not ready.

**Behaviour (import):**
- **Checked before any fetch or write**, each → `invalid_request` with a sentence naming the pick: `owner`/`repo` by Task 2's patterns, `sha` `^[0-9a-f]{40}$`, `picks` a non-empty list, each `kind` and `mode` known, no `(kind, path)` twice; after re-fetching the manifest at `sha`: each path is in it for that kind. Against the current lists: a library pick whose name has existing files must be `overwrite` with `target` one of them and `expectedRevision` an int; one with none must be `new` without `target`; an entity `overwrite` needs `target` a definition of that name and `expectedRevision`.
- **Fetch all picked files first**; a fetch failure refuses the whole import with nothing written.
- **Library ids.** `new` → the manifest `id` when given and no stored library has it, else `f"library-{uuid.uuid4()}"`, the form the panel gives a new file. `overwrite` → `target`.
- **One library request** holding every picked library, in manifest order: `{operation: "import", libraries: [{id, name: item.name, source, expectedRevision: 0 | pick's}]}`, so `PeopleDetector.cs` compiles with `Vision.cs` when both are picked. Refused → every library item `failed` with a message from the refusal (`validation`: up to three error diagnostics as `{file name} {line}:{column}: {message}`, then `it would break {automation or definition name}` for each listed, joined `; `; `conflict`: `It changed after you inspected the repository; inspect it again`; else the detail), every entity item `failed` with `Not imported, because the node libraries were not`, and no entity request is made.
- **Kind map** for the entities: manifest library `id` → the id used — the stored id for a picked library; for an unpicked library, the one existing file of that name when there is exactly one; otherwise no entry.
- **Each entity**, in manifest order: body from `entity_body`, graph through `rewrite_library_kinds`; every save sends `deriveSignatures: true` (Ruling 14). `new` → `save` with `id = definition_id(name, taken)` (taken = listed ids + unreadable ids + ids given earlier in this import), `expectedRevision: 0`, `enabled: false`, outcome `imported`, `enabled: false`; `overwrite` → read the target, mark each imported setting whose `key` and `type` it has with `keep: true` and no `value`, graph through `keep_device_inputs(graph, target graph)`, `save` with the pick's `expectedRevision` and no `enabled`, outcome `overwritten`. A refused save is that item's `failed` with up to three error diagnostic messages joined `; ` (or the conflict sentence), and the next entity is still tried. **Libraries already committed stay** (Ruling 4).

- [ ] **Step 1: Write the failing tests** (fake `GitHub` fetch serving a dict of URLs, with the spec's §6 manifest plus, where a test needs a library kind, a second entity `Faces at the door` whose graph has a node of kind `library:vision:global%3A%3ANodeLib.FaceDetection.DetectFaces`; fake `Backend` recording calls):
  - `test_inspect_lists_items_and_existing`: two stored `Vision.cs` (ids `a`, `b`) and a definition `Front door people` → the `Vision.cs` item's `existing` has both, `PeopleDetector.cs`'s is empty, the entity's has one; `sha` is the API's; a manifest entry whose raw URL is 404 has `problem` `x.json is not in the repository at 0123abc`; an oversized `.cs` has `x.cs is larger than 2 MiB`.
  - `test_inspect_errors`: API 404 / 403, raw manifest 404, bad TOML → the `not_found`, `fetch`, `not_found`, `manifest` envelopes; no backend call is a write.
  - `test_import_refetches_the_sha_only`: every fetched URL starts `https://raw.githubusercontent.com/o/r/<sha>/`; none is `api.github.com`.
  - `test_import_libraries_then_entities`: picks both libraries `new` + `Front door people` `new` → backend calls in order: `import_libraries` once with `[{id: "vision", name: "Vision.cs", …, expectedRevision: 0}, {id: "people_detector", name: "PeopleDetector.cs", …, expectedRevision: 0}]`, then `save_definition` with `id: "front_door_people"`, `expectedRevision: 0`, `enabled: False`, `deriveSignatures: True`, and its graph equal to the file's (no `library:` kind to rewrite); outcomes `imported`, `imported`, `imported` (enabled False).
  - `test_kinds_follow_the_library_actually_used` (Review Focus 1): stored `Vision.cs` as `library-1234`; overwrite it → `Faces at the door`'s saved node kind is `library:library-1234:global%3A%3ANodeLib.FaceDetection.DetectFaces`; with `Vision.cs` unpicked (Skip) the same; with two same-named files and `Vision.cs` unpicked, the kind is unchanged.
  - `test_manifest_id_taken_gives_a_new_id`: a stored library `vision` named `Other.cs` → the new `Vision.cs`'s id matches `^library-[0-9a-f-]{36}$` and the graph's kinds use it; `people_detector` is still stored under its manifest id.
  - `test_library_overwrite_only`: library `new` while a same-named file exists, `overwrite` with a `target` not of that name, a path not in the manifest, a repeated pick → `invalid_request`; no backend write.
  - `test_refused_libraries_stop_entities` (Review Focus 2): only `PeopleDetector.cs` and the entity picked, `import_libraries` answers `validation` with one error diagnostic in `PeopleDetector.cs` → that item `failed` `PeopleDetector.cs 3:5: …`, entity item `failed` `Not imported, because the node libraries were not`, no `save_definition` call.
  - `test_entity_failure_keeps_libraries`: libraries succeed, the first entity's save answers `validation`, the second succeeds → outcomes `imported`, `imported`, `failed` (its diagnostic message), `imported`; no library request after the import.
  - `test_overwrite_keeps_setting_values` (Review Focus 5): target has `address` (string, secret) and `interval` (number); the file has `address` (string) and `interval` (string) and `extra` → saved `address` with `keep: True` and no `value`, `interval` with the file's value, `extra` as given; `expectedRevision` the pick's.
  - `test_overwrite_keeps_device_inputs` (Review Focus 5): the target's `csharp_device` node `front_door` has `arguments.Camera` `{kind: "literal", type: "entity_id:camera", value: "camera.porch"}` and a signature setting `Camera` of type `entity`, domain `camera` → the saved node's `Camera` is `camera.porch`; when the target's setting `Camera` is another type, or the target's node of that id is another kind, or the imported node has no such setting, the file's argument is saved; an argument only the target has is not added.
  - `test_new_ids_avoid_taken_and_each_other`: two entities named `Front door people` imported `new` with `front_door_people` listed and `front_door_people_2` unreadable → ids `front_door_people_3`, `front_door_people_4`.
  - Route: `test_external_route_is_admin_and_envelope`: non-admin → `unauthorized`, no fetch; admin `inspect` through `hass_sharp/external` answers the flow's result; an unknown `operation` → `invalid_request` `operation must be inspect or import`. `WebsocketReadinessTests`: `hass_sharp/external` answers `not_ready` while no entry is loaded.
- [ ] **Step 2: Run** `python3 -m unittest discover -s tests/python -p 'test_external.py'` and `-p 'test_visual.py'`. Expected: FAIL.
- [ ] **Step 3: Implement** the flow, `keep_device_inputs`, `AuthoringService.external` and the registration.
- [ ] **Step 4: Run** `python3 -m unittest discover -s tests/python`. Expected: PASS. Also in the container (AGENTS.md "To run them against the local container's Home Assistant") if the host lacks Home Assistant 2026.11.
- [ ] **Step 5: Commit** — `External import: hass_sharp/external inspects a GitHub repository and imports what was picked, libraries first.`

---

### Task 4: hass-sharp-vision's `hass_sharp.toml` and the Front door people definition

**Repository:** hass-sharp-vision. It runs after Tasks 1–3 here and after Project A's Tasks 10–11: its tests need this plan's `import` operation and `external.py` in the hass-sharp checkout they build against (`HS/` on this branch), and the vision files.

**Files:**
- Create: `HV/hass_sharp.toml` (repository root), `HV/nodes/front_door_people.json`, `HV/tests/Vision.Tests/ManifestTests.cs` (suite `--vision-manifest`, `Order = 43`), `HV/tests/test_manifest.py`
- Modify: `HV/README.md` (its Install section, Project A Task 13), `HV/.github/workflows/ci.yaml` (the `csharp` job's flags and its Python step)

**Interfaces:**
- Consumes: `HV/nodes/Vision.cs` and `HV/nodes/PeopleDetector.cs` (Project A); Task 1's `import`; Task 2's `parse_manifest`, `library_source`, `entity_body`, `check_path`, `CAP`, `rewrite_library_kinds`, loaded from `$HASS_SHARP_ROOT/custom_components/hass_sharp/external.py` by path (it is stdlib-only, Task 2).
- Produces, `HV/hass_sharp.toml` exactly as spec §6, comments included (hass-sharp's docs quote it; Task 6 tests that it parses):

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

- Produces, `front_door_people.json`: `{name: "Front door people", settings: [], logic: {kind: "graph", graph}}`, `graph` `{version: 5, name: "Front door people", nodes: [<one node>], edges: []}` (`version` as hass-sharp's `CC/authoring_examples.json`'s "Plug in C#" has it). The one node, as "Plug in C#"'s `plug` node is shaped (hass-sharp's `docs/custom-entities.md` "C# nodes": config `{key, name, via?, source, signature, arguments?}`):
  - `id` `front_door`, `kind` `csharp_device`, `position` `{x: 60, y: 100}`
  - `config.key` `front_door`, `config.name` `Front door`
  - `config.source`: `/// <summary>A People detector on the front door's camera.</summary>\npublic class FrontDoorPeople : PeopleDetector { }\n`
  - `config.arguments`: `{"Camera": {"kind": "literal", "type": "entity_id:camera", "value": "camera.front_door"}}` (an entity setting is typed into the node: it takes no Setting step; its literal's type is `entity_id:<domain>`, `VisualNodeCatalog.cs:76`). The other settings are left to the class's defaults.
  - `config.signature`: not written by hand — produced by an entity `preview` of the body with `signature` left out and `deriveSignatures: true` against the two library files (the first test below prints it), then pasted into the file. It carries PeopleDetector's settings, `Camera` first as `{key: "Camera", label: "Camera", type: "entity", domain: "camera", required: true, …}`.
- Produces, the README's Install section: import with **Add external** and `https://github.com/dompagoj/hass-sharp-vision` (what it lists, that the definition comes in turned off, pick the camera in the **Front door** device's **Camera**, then turn it on), with pasting the two files kept as the way for a HassSharp without Add external.

- [ ] **Step 1: Write the failing tests:**
  - C# (`ManifestTests`, in Project A's test project), each on a temp manager given `manager.SetEntityDescriptions(<ha-entity-components.json from AppContext.BaseDirectory>, started: true)` (it has the `camera` domain, so `CameraEntity` exists), library sources read from `VisionTests.Root`:
    - `TheRepositoryLibrariesImportTogether`: one `import` of `{id: "vision", name: "Vision.cs"}` and `{id: "people_detector", name: "PeopleDetector.cs"}` (rev 0) → saved; the catalog has the kind `library:vision:global%3A%3ANodeLib.FaceDetection.DetectFaces`.
    - `PeopleDetectorNeedsVision` (Review Focus 2): on a fresh manager, an `import` of `people_detector` alone → `validation`, a diagnostic in `PeopleDetector.cs`, `list` empty; after `Vision.cs` is saved through `save`, the same import succeeds.
    - `TheRepositoryFrontDoorPeoplePreviews`: after the pair import, an entity `preview` of `front_door_people.json`'s body as stored (no `deriveSignatures`) is `valid: true` with no error (an unknown-entity warning for `camera.front_door` allowed), so a stored signature that lags `PeopleDetector.cs` fails here, not on a user's import; with `signature` removed and `deriveSignatures: true`, the answered node's `signature` `JsonNode.DeepEquals` the stored one (pattern: hass-sharp's `AuthoringAdapterTests.ExampleDefinitionsThroughTheApi`); the graph has no node kind starting `library:`.
  - Python `test_manifest.py`, `test_repository_manifest`: loads `external.py` from `HASS_SHARP_ROOT` (unset → skipped with `set HASS_SHARP_ROOT to a hass-sharp checkout`); `parse_manifest(Path("hass_sharp.toml").read_bytes())` gives the three items of Task 2's `test_manifest`; each path exists and is under `CAP`; `library_source` accepts each `.cs` and `entity_body` the JSON; `PeopleDetector.cs` contains `public abstract class PeopleDetector : SharpDevice`; the definition's only node is a `csharp_device` whose source matches `:\s*PeopleDetector\b` and whose `Camera` argument is `camera.front_door`; no node kind starts `library:`, so `rewrite_library_kinds(graph, {"vision": "x", "people_detector": "y"}) == graph`.
- [ ] **Step 2: Run** `dotnet run --project tests/Vision.Tests --disable-build-servers -p:HassSharpRoot=/Users/dompa/dev/HassSharp -- --vision-manifest` and `HASS_SHARP_ROOT=/Users/dompa/dev/HassSharp python3 -m unittest discover -s tests -p test_manifest.py`. Expected: FAIL (no files).
- [ ] **Step 3: Write** `hass_sharp.toml` and `front_door_people.json`, the signature pasted from the derived preview; the README's Install section; and in `ci.yaml`'s `csharp` job, `--vision-manifest` after `--people-detector` and a step `python -m unittest discover -s tests -v` with `HASS_SHARP_ROOT: ${{ github.workspace }}/.hass-sharp` (the job stays switched off until Project A's Task 15 pins a hass-sharp commit that has this plan's Tasks 1–2).
- [ ] **Step 4: Run** both, then every hass-sharp-vision suite, and `docker run --rm -v "$PWD":/repo --workdir /repo rhysd/actionlint:1.7.7`. Expected: PASS.
- [ ] **Step 5: Commit** (hass-sharp-vision) — `hass-sharp-vision offers Vision.cs, PeopleDetector.cs and a Front door people definition through hass_sharp.toml.`

---

### Task 5: The Add external dialog and its two buttons

**Repository:** hass-sharp.

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
- A refused inspect or import shows the sentence (`role="alert"`) and returns to the step it came from, closable (Review Focus 4). The warning of Global Constraints is on every step.
- `NodeLibraryView`: `Add external` beside `Add file`; `onImported` → `session.refresh()`. `EntitiesListView`: `Add external` in the heading actions when `admin()`; `onImported` → the list's `refresh()`.

- [ ] **Step 1: Write the failing tests** in `add-external.test.tsx` (vitest + happy-dom, a fake `callWS` answering `hass_sharp/external` from a script and recording requests, `hold` to delay a reply), the script's inspection being hass-sharp-vision's three items:
  - `inspects, picks and imports`: type the URL, Inspect → both groups, the warning text exactly, links `https://github.com/o/r/blob/<sha>/nodes/Vision.cs` and `…/nodes/PeopleDetector.cs`; Import sends `{operation: 'import', owner: 'o', repo: 'r', sha, picks: [{kind: 'library', path: 'nodes/Vision.cs', mode: 'new'}, {kind: 'library', path: 'nodes/PeopleDetector.cs', mode: 'new'}, {kind: 'entity', path: 'nodes/front_door_people.json', mode: 'new'}]}`; the result lines read `Vision.cs: imported`, `PeopleDetector.cs: imported` and `Front door people: imported, turned off`; `onImported` called once.
  - `existing items start skipped`: an entity with one existing → unchecked, and with both libraries unchecked too, Import disabled; checking it shows `Overwrite Front door people (front_door_people)` and `Add as new`; choosing `Add as new` sends `mode: 'new'`; choosing Overwrite sends `{mode: 'overwrite', target: 'front_door_people', expectedRevision: 3}`.
  - `a library offers no second copy`: `Vision.cs` with two existing → options are exactly `Overwrite Vision.cs (a)`, `Overwrite Vision.cs (b)` (no `Add as new`); choosing `b` sends `target: 'b'`.
  - `an item with a problem cannot be picked`: its checkbox disabled, the problem shown.
  - `a refused library shows on every line`: the import answers `PeopleDetector.cs` `failed` with a compiler message and the entity `failed` with `Not imported, because the node libraries were not` → the lines `PeopleDetector.cs: not imported — …` and `Front door people: not imported — Not imported, because the node libraries were not`.
  - `cannot close while fetching, and recovers from a refusal` (Review Focus 4): with inspect held, Escape and Cancel do not close; release with the `fetch` limit sentence → alert shows it, the URL field is back, Cancel closes.
  - `NodeLibraryView.test.tsx`: `Add external` opens the dialog; after an import that overwrote `shared` while `Shared.cs` had an unsaved edit, the file list shows the revision-conflict mark (`aria-label="Revision conflict"`) and the draft is kept, as a save made elsewhere does today; a `list` request follows the import.
  - `EntitiesListView.test.tsx`: `Add external` is shown for an admin and not otherwise; an import refetches the list (`list` with `view: 'home'` requested again).
- [ ] **Step 2: Run** `(cd custom_components/hass_sharp/frontend && bun run test -- add-external NodeLibraryView EntitiesListView)`. Expected: FAIL.
- [ ] **Step 3: Implement** `api.ts`, the dialog and the two buttons.
- [ ] **Step 4: Run** `bun run test` and `bunx tsc --noEmit` in `FE/`. Expected: PASS.
- [ ] **Step 5: Commit** — `The panel's Add external dialog imports node libraries and entity definitions from a GitHub repository.`

---

### Task 6: Documents

**Repository:** hass-sharp.

**Files:**
- Modify: `docs/node-libraries.md` (new sections after "Node libraries", before "Entity classes" at :579), `docs/custom-entities.md` (its own `## Import from GitHub` before "Run history" at :1064)
- Test: `tests/test_docs_examples.py`

**Behaviour:**
- `docs/node-libraries.md` `## Import from GitHub`: the button in both views; the URL forms; what Inspect shows (one commit; existing items unchecked); the choices (libraries Overwrite or Skip only, and why: one `NodeLib` namespace; entities Overwrite / Add as new / Skip; new definitions come in turned off, with an id from their name); the picked libraries are compiled together with the stored ones, so a library that uses another (hass-sharp-vision's `PeopleDetector.cs` uses `Vision.cs`) is imported with it or after it, and alone, while the other is not stored, is refused with the compiler's message; libraries first and atomically, then each definition, and that a definition that fails leaves the imported libraries in place, its line saying why; Overwrite keeps the values of definition settings whose key and type still match and what is typed into a C# node's settings, such as a device's Camera, whose setting the imported node still has with the same type; a one-time copy, nothing tracks the repository; the warning sentence of Global Constraints; GitHub's 60-requests-an-hour limit for inspecting.
- `### Writing a hass_sharp.toml`: the fields (`[[library]]` `id`, `name`, `path`, `description`; `[[entity]]` `name`, `path`, `description`), the path rules, the 2 MiB and 50-item caps, the root only; an entity file is a definition body (`name`, `settings`, `logic`), the manifest's name winning, a read answer acceptable (its other members are dropped), and its C# nodes' pins are worked out again from their code against the imported libraries; and **library ids**: a library node's kind carries its library's id, so set `id` to the id the definitions' kinds use (`library:<id>:…`), and the import rewrites those kinds to the id the library gets; a class a C# node derives from (`public class FrontDoorPeople : PeopleDetector { }`) is found by its name, so it needs no id. hass-sharp-vision's `hass_sharp.toml` (spec §6) quoted in full after the line `hass-sharp-vision's manifest, for example:`.
- `docs/custom-entities.md`: a short section that a definition can be imported with **Add external**, comes in turned off, and links to `node-libraries.md#import-from-github`; the Front door people example: Add external with `https://github.com/dompagoj/hass-sharp-vision`, import `Vision.cs`, `PeopleDetector.cs` and the definition, pick your camera in the **Front door** C# device's **Camera**, then turn it on; a later Overwrite keeps the camera picked.
- `tests/test_docs_examples.py` `test_the_manifest_in_the_docs_parses`: `fenced_block_after(node-libraries.md, "hass-sharp-vision's manifest, for example:", "toml")`, given to `external.parse_manifest` (the module loaded by path), gives the three items of Task 2's `test_manifest`, and each path passes `check_path`. hass-sharp has no `hass_sharp.toml` of its own to compare it with; hass-sharp-vision's file is the same text (Task 4 writes it from spec §6). The existing link check covers the new anchors.

- [ ] **Step 1: Write the failing test.** **Step 2: Run** `python3 -m unittest discover -s tests`, expect FAIL. **Step 3: Write** the sections. **Step 4: Run** `python3 -m unittest discover -s tests`, expect PASS. **Step 5: Commit** — `The documents describe Add external and how to write a hass_sharp.toml.`

---

### Task 7: Live check on the local instance

**Repository:** hass-sharp's dev container (it mounts `HS/`, on this branch); the import reads hass-sharp-vision from GitHub.

No product change; a fix found here goes back to the task that owns it, with a test. Local container only (port 5001, container `hass`); never the `mcp__hass__*` tools. No implementer pushes; this check waits for the owner's go to push and release hass-sharp-vision, not for a hass-sharp merge (split decision 8).

- [ ] **Step 1: Build and restart.** `dotnet publish -c Release ./custom_components/hass_sharp/HassSharp -o ./custom_components/hass_sharp/out`; `(cd custom_components/hass_sharp/frontend && bun run build)`; `docker compose restart -t 60 hass` (a hard refresh alone serves the old bundle: the panel's bundle URL is fixed at startup); `docker compose logs --tail=200 hass` shows the integration loaded.
- [ ] **Step 2: The refusals, at any time.** At <http://localhost:5001>, HassSharp panel → Node libraries → **Add external**: `https://github.com/dompagoj/hass-sharp` → the message `dompagoj/hass-sharp has no hass_sharp.toml at its root at <sha7>` (proves the container reaches the API and raw hosts and resolves `HEAD`; hass-sharp has no manifest of its own); `https://github.com/dompagoj/hass-sharp/tree/no-such-branch` → the ref sentence; `https://gitlab.com/x/y` → the URL sentence; Cancel works after each. Report these.
- [ ] **Step 3: Wait for hass-sharp-vision on GitHub.** Project A's Task 15 has pushed and released it on the owner's go, and Task 4's commit has been pushed after it on the owner's go (`hass_sharp.toml` is on `main`). Until then leave Steps 4–5 open in the report.
- [ ] **Step 4: Import from hass-sharp-vision.** Add external with `https://github.com/dompagoj/hass-sharp-vision` lists `Vision.cs`, `PeopleDetector.cs` and `Front door people`, each with a working `View on GitHub` link at the sha. If Project A's live check (its Task 14) left `Vision.cs` or `PeopleDetector.cs`, each is offered Overwrite only; overwrite them, which replaces that check's host-served `Source` with the release's. Import all three → `Vision.cs: imported` (or `overwritten`), `PeopleDetector.cs: imported` (or `overwritten`) and `Front door people: imported, turned off`; the library list shows both files; Entities shows **Front door people** off. If `testcamera` uses Detect faces (Project A's live check), it still runs after an Overwrite of `Vision.cs` (its kind kept its id).
- [ ] **Step 5: Run it from the release.** Open Front door people: its **Front door** C# device has no problem and its Camera is `camera.front_door`; set Camera to the camera Project A's check used (the dummy camera or `People clip`'s), save, turn it on; the device's entities appear (camera, Face seen, Faces, Person seen, People, Last face seen, Last person seen) and change as someone is in view. The logs (`docker compose logs --tail=200 hass`) show at most one native download, now `Downloading libhs_vision-linux-musl-arm64.so from github.com`: none when the release's checksum equals the one Project A's check cached (the file is cached by checksum), one when Project A's Task 15 pinned new checksums. Inspect again: all three items unchecked with their Overwrite choices; Overwrite the definition → its Camera is still the one picked.
- [ ] **Step 6: Report** what was seen at each step, with log lines, and anything that did not match.

---

## Rulings

1. **The kind-id problem is real, and the manifest names each library's id.** A library node's kind is `library:<library document id>:<type>.<method>`, and a file added in the panel gets `library-<uuid>`, so a definition in a repo cannot name the id the importing instance will use. The `[[library]]` table takes an optional `id` (now in spec §6); a new library is stored under it when no stored library has it (else `library-<uuid>`), and import rewrites the id segment of `library:` kinds in imported definitions to the id actually used — a new file's, the overwritten file's, or, for an unpicked library, its one same-named existing file's. Alternatives rejected: kinds by type and method alone (changes every saved graph's kind and the catalog contract for an import feature); always storing under the manifest id (impossible on Overwrite of a file with another id). Cost if wrong: one optional manifest key and a twenty-line rewrite.
2. **HTTP through Home Assistant's aiohttp session, not urllib in an executor** (the spec said executor). `async_get_clientsession` is the idiomatic Home Assistant client: no thread per request, Home Assistant's SSL context and proxy settings, cancellation on unload, and a 30 s `ClientTimeout`. `external.py` stays testable by taking the session duck-typed. Cost if wrong: swap the fetcher.
3. **`HEAD` for the default branch**, one API call per inspect and none per import (GitHub allows 60 unauthenticated calls an hour per address). Cost if wrong: a second call to read `default_branch`.
4. **A failed definition does not undo the libraries.** They were valid and compiled together, every automation and definition was rebuilt against them, and undoing them is a second rebuild-everything commit that can itself fail; a definition is saved one at a time by the existing save, so the result lists each item and says why one failed, and that one can be fixed and saved or imported again. A refused library import, by contrast, stops before any definition. Cost if wrong: a user removes a library by hand.
5. **Inspect fetches every listed file**, so a missing or malformed file is shown before Import (`problem`, an addition to the spec's item shape); import fetches the picked ones again at the same sha, all before any write, so a network failure writes nothing.
6. **Picks carry `expectedRevision`** for Overwrite (the spec's pick had `target` only), taken from inspect's `existing`: a library or definition changed between Inspect and Import is a conflict, not silently replaced.
7. **Overwrite keeps what the user filled in.** Definition settings whose key and type still match keep their values (`keep: true`), so a chosen address or a secret survives; and a C# node's typed arguments are kept where the existing graph has a node of the same id and kind whose signature has that setting with the same type and domain as the imported node's — needed because an entity setting such as the People detector's Camera takes no Setting step and is typed into the node. A new or retyped setting takes the file's. Not in the spec. Cost if wrong: drop `keep_device_inputs` and the marking.
8. **The manifest's entity `name` is the definition's name**, overriding the file's, and is what "existing" matches; only `name`, `settings`, `logic` are taken from the file, so an exported read answer works.
9. **The new definition id is worked out in Python** by a port of `definitionId`, pinned to the frontend's test vectors, so the import needs no round trip to the panel. Cost if wrong: drift between two copies, caught by the shared vectors.
10. **Caps beyond the spec's**: at most 50 manifest items (bounds memory at 100 MiB worst case and the time an unload may wait for an in-flight import); redirects only within `api.github.com` and `raw.githubusercontent.com`; unknown manifest keys ignored for forward compatibility.
11. **Existing items default to Skip** in the dialog; re-inspecting an imported repo and pressing nothing imports nothing.
12. **Not in MCP.** `import` is a panel feature; `llm.py`'s tools are unchanged.
13. **A definition that uses a library class needs no rewriting.** A C# node's code is compiled in its own `NodeLib.<node>` namespace (`NodeLibraryCompiler.CustomWrapperPrefix`) and finds `PeopleDetector` in `NodeLib` by name, whatever the library's document id; its stored `signature` holds title, description, domain and settings only (`NodeLibraryCompiler.cs:1031–1046`), and the embedded catalog that names `sourceId`s is part of a preview or run, not of the body `entity_body` keeps. So hass-sharp-vision's `front_door_people.json` comes in unchanged and the manifest `id`s matter only to `library:` kinds (Detect faces in a graph camera); they stay in the manifest for those. A clash a user could hit instead: a stored library already declaring a `PeopleDetector` class is a compile error, which the refused import reports.
14. **Imported definitions are saved with `deriveSignatures: true`.** A stored signature that is not what the code gives is an error (`VisualGraphValidator` `StaleSignature`), and a definition file can lag the library it is imported with (an Overwrite brings a `PeopleDetector.cs` with a new setting); deriving rewrites each C# node's pins from its code against the libraries just imported and keeps typed values that still fit, as MCP saves do. hass-sharp-vision's own file is still kept current by Task 4's no-derive preview. Cost if wrong: drop one request member.
15. **The manifest and the example definition live in hass-sharp-vision, as this plan's Task 4** (owner's decision: they move with the node code). They describe that repository's files, so they sit beside them and are tested there, against a hass-sharp checkout, as Project A's vision tests are; the task stays in this plan because its tests need this plan's `import` and `external.py`. hass-sharp keeps the `hass_sharp.toml` format and its docs, whose example is checked by parsing it (Task 6). Cost if wrong: the task moves to Project A, unchanged.
16. **The live import no longer waits for a hass-sharp merge** (split decision 8): the local instance runs this branch, and hass-sharp-vision is a separate public repository, so the import needs only that repository pushed and released, on the owner's go. Its download comes from the release, which is the first real test of the release URL.
