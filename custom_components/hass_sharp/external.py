"""Importing node libraries and entity definitions from a public GitHub repository's hass_sharp.toml.

The rules (the URL forms, the manifest, its paths, sizes and ids, the rewrite of library node kinds), the fetcher, and
the flow: `inspect` a repository, then `import_items` what was picked from it, libraries first.
The standard library only, so tests load this file by its path without Home Assistant or aiohttp.
"""
import copy
import json
import logging
import re
import tomllib
import unicodedata
import uuid
from collections.abc import Awaitable, Callable, Iterable
from dataclasses import dataclass
from typing import Protocol
from urllib.parse import quote, unquote, urljoin, urlsplit

CAP = 2 * 1024 * 1024
"""The most bytes of any one fetched file, hass_sharp.toml included."""
MAX_ITEMS = 50
ALLOWED_HOSTS = frozenset({"api.github.com", "raw.githubusercontent.com"})
CHUNK = 64 * 1024
MAX_REDIRECTS = 5
MAX_DEPTH = 64
"""The deepest nesting of an entity file, System.Text.Json's default; deeper, copying it would exhaust the stack."""
_REDIRECTS = frozenset({301, 302, 303, 307, 308})

_OWNER = re.compile(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})")
_REPO = re.compile(r"[A-Za-z0-9._-]{1,100}")
_LIBRARY_ID = re.compile(r"[a-z0-9][a-z0-9_-]{0,79}")
_SHA = re.compile(r"[0-9a-f]{40}")
_INVALID_URL = ("Paste a GitHub repository's address, such as https://github.com/owner/repo or "
                "https://github.com/owner/repo/tree/main")
_LIMIT = "GitHub refused the request: it allows 60 requests an hour without signing in; try again later"
_EXTENSIONS = {"library": ".cs", "entity": ".json"}
_CONFLICT = "It changed after you inspected the repository; inspect it again"
_NOT_ATTEMPTED = "Not imported, because the node libraries were not"
_C_SHARP_NODES = frozenset({"csharp_entity", "csharp_device"})
# Under the integration's logger (const.py), so its level setting reaches this module's messages.
_LOGGER = logging.getLogger("custom_components.HassSharp.external")


class ExternalError(Exception):
    """A refusal of the whole request, answered as the envelope's error."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message

    def answer(self) -> dict:
        return {"version": 1, "ok": False, "error": {"code": self.code, "detail": self.message}}


class HttpStatus(Exception):
    """What a fetcher raises for an answer other than 200."""

    def __init__(self, status: int) -> None:
        super().__init__(f"HTTP {status}")
        self.status = status


class TooLarge(ExternalError):
    """What a fetcher raises for a file over CAP: a refusal of the whole request, unless the file is one listed item."""

    def __init__(self, name: str) -> None:
        super().__init__("fetch", f"{name} is larger than 2 MiB")


class ItemProblem(Exception):
    """Why one listed file cannot be imported; the message names its path."""


@dataclass(frozen=True)
class Repo:
    owner: str
    repo: str
    ref: str | None


@dataclass(frozen=True)
class Item:
    kind: str
    """`library` or `entity`."""
    name: str
    path: str
    description: str
    id: str | None
    """The id a library asks to be stored under; None for an entity."""


Fetch = Callable[[str, str], Awaitable[bytes]]
"""`(url, accept)`: the body, at most CAP bytes; raises HttpStatus, TooLarge, or ExternalError("fetch", …)."""


def parse_url(url: str) -> Repo:
    """The repository (and the branch, tag or commit) of a GitHub address as a browser shows it."""
    parts = urlsplit(url.strip())
    if parts.scheme != "https" or parts.netloc.lower() not in ("github.com", "www.github.com"):
        raise ExternalError("invalid_request", _INVALID_URL)
    segments = parts.path.rstrip("/").split("/")[1:]
    ref = None
    if len(segments) >= 4 and segments[2] == "tree":
        ref = unquote("/".join(segments[3:]))
        segments = segments[:2]
    if len(segments) != 2:
        raise ExternalError("invalid_request", _INVALID_URL)
    owner, repo = segments[0], segments[1].removesuffix(".git")
    # No ref has an empty, `.` or `..` part (git refuses them), and the request's path would lose it.
    bad_ref = ref is not None and any(part in ("", ".", "..") for part in ref.split("/"))
    if not _OWNER.fullmatch(owner) or not _REPO.fullmatch(repo) or repo in (".", "..") or bad_ref:
        raise ExternalError("invalid_request", _INVALID_URL)
    return Repo(owner, repo, ref)


def check_path(path: object, kind: str) -> str | None:
    """The problem with a listed path, or None: relative, `/`-separated, inside the repository, of the kind's type."""
    extension = _EXTENSIONS[kind]
    problem = f"path must be a relative path inside the repository to a {extension} file"
    if not isinstance(path, str) or not path.endswith(extension) or "\\" in path:
        return problem
    if any(ord(c) < 0x20 or ord(c) == 0x7F for c in path):
        return problem
    if any(part in ("", ".", "..") for part in path.split("/")):
        return problem
    return None


def _utf16_length(text: str) -> int:
    """The length .NET's `string.Length` gives."""
    return len(text.encode("utf-16-le")) // 2


def _name_problem(name: object, kind: str) -> str | None:
    if kind == "library":
        # NodeLibraryStore.ValidateName's rule.
        if (not isinstance(name, str) or not name.strip() or _utf16_length(name) > 120
                or any(c in name for c in "/\\\0\r\n")):
            return "name must be a file name of up to 120 characters without /, \\ or a line break"
        return None
    # DefinitionGenerator's rule for a definition's name.
    if not isinstance(name, str) or not name.strip() or _utf16_length(name) > 120:
        return "name must be a name of up to 120 characters that is not blank"
    return None


def parse_manifest(data: bytes) -> list[Item]:
    """The libraries, then the entities, hass_sharp.toml lists, each in its order; every problem in one refusal."""
    try:
        text = data.decode("utf-8-sig")
    except UnicodeDecodeError:
        raise ExternalError("manifest", "hass_sharp.toml is not UTF-8 text") from None
    try:
        document = tomllib.loads(text)
    except tomllib.TOMLDecodeError as ex:
        raise ExternalError("manifest", f"hass_sharp.toml is not valid TOML: {ex}") from None
    except RecursionError:
        raise ExternalError("manifest", "hass_sharp.toml is not valid TOML: it nests arrays or tables too deeply") from None
    problems: list[str] = []
    tables: dict[str, list] = {}
    for kind in ("library", "entity"):
        listed = document.get(kind, [])
        if isinstance(listed, list):
            tables[kind] = listed
        else:
            problems.append(f"{kind} must be an array of tables, each starting [[{kind}]]")
            tables[kind] = []
    if sum(len(listed) for listed in tables.values()) > MAX_ITEMS:
        raise ExternalError("manifest", f"hass_sharp.toml lists more than {MAX_ITEMS} items")
    items: list[Item] = []
    paths: dict[str, str] = {}
    names: dict[str, str] = {}
    ids: dict[str, str] = {}
    for kind, listed in tables.items():
        for number, table in enumerate(listed, 1):
            label = f"{kind} {number}"
            if not isinstance(table, dict):
                problems.append(f"{label}: must be a table")
                continue
            found: list[str] = []
            name, path = table.get("name"), table.get("path")
            description, library_id = table.get("description", ""), table.get("id") if kind == "library" else None
            if problem := _name_problem(name, kind):
                found.append(problem)
            elif kind == "library":
                if name in names:
                    found.append(f"name {name} is also {names[name]}'s")
                else:
                    names[name] = label
            if problem := check_path(path, kind):
                found.append(problem)
            elif path in paths:
                found.append(f"path {path} is also {paths[path]}'s")
            else:
                paths[path] = label
            if not isinstance(description, str):
                found.append("description must be text")
            if library_id is not None:
                if not isinstance(library_id, str) or not _LIBRARY_ID.fullmatch(library_id):
                    found.append("id must be up to 80 lower-case letters, digits, _ or -, starting with a letter or digit")
                elif library_id in ids:
                    found.append(f"id {library_id} is also {ids[library_id]}'s")
                else:
                    ids[library_id] = label
            problems.extend(f"{label}: {problem}" for problem in found)
            if not found:
                items.append(Item(kind, name, path, description, library_id))
    if problems:
        raise ExternalError("manifest", "hass_sharp.toml: " + "; ".join(problems))
    return items


def _slug(name: str) -> str:
    """The frontend's `slug`: lower case without accents, each run of other characters one `_`, none at either end."""
    text = "".join(c for c in unicodedata.normalize("NFD", name.lower()) if not unicodedata.category(c).startswith("M"))
    return re.sub(r"^[_-]+|[_-]+$", "", re.sub(r"[^a-z0-9]+", "_", text))


def _trim_separators(text: str) -> str:
    return re.sub(r"[_-]+$", "", text)


def definition_id(name: str, taken: Iterable[str]) -> str:
    """The frontend's `definitionId`: the name's slug within 80 characters, else `entity`; never `new` or a taken id."""
    base = _trim_separators(_slug(name)[:80]) or "entity"
    used = {*taken, "new"}
    if base not in used:
        return base
    n = 2
    while True:
        suffix = f"_{n}"
        candidate = _trim_separators(base[:80 - len(suffix)]) + suffix
        if candidate not in used:
            return candidate
        n += 1


def rewrite_library_kinds(graph: dict, ids: dict[str, str]) -> dict:
    """A copy of the graph whose `library:<id>:…` node kinds name the ids the libraries are stored under."""
    rewritten = copy.deepcopy(graph)
    nodes = rewritten.get("nodes")
    for node in nodes if isinstance(nodes, list) else []:
        kind = node.get("kind") if isinstance(node, dict) else None
        if isinstance(kind, str) and kind.startswith("library:"):
            library, separator, rest = kind[len("library:"):].partition(":")
            if separator and library in ids:
                node["kind"] = f"library:{ids[library]}:{rest}"
    return rewritten


def _text(item: Item, data: bytes) -> str:
    try:
        return data.decode("utf-8-sig")
    except UnicodeDecodeError:
        raise ItemProblem(f"{item.path} is not UTF-8 text") from None


def library_source(item: Item, data: bytes) -> str:
    """A library file's C# source."""
    return _text(item, data)


def entity_body(item: Item, data: bytes) -> dict:
    """A definition body from an entity file: the manifest's name, the file's settings and graph logic, nothing else."""
    try:
        file = json.loads(_text(item, data))
    except json.JSONDecodeError as ex:
        raise ItemProblem(f"{item.path} is not valid JSON: {ex}") from None
    except RecursionError:
        raise ItemProblem(f"{item.path} is not valid JSON: it nests more than {MAX_DEPTH} levels deep") from None
    if _deeper_than(file, MAX_DEPTH):
        raise ItemProblem(f"{item.path} is not valid JSON: it nests more than {MAX_DEPTH} levels deep")
    logic = file.get("logic") if isinstance(file, dict) else None
    if not isinstance(logic, dict) or logic.get("kind") != "graph" or not isinstance(logic.get("graph"), dict):
        raise ItemProblem(f"{item.path} is not an entity definition: it needs logic of kind graph")
    return {"name": item.name, "settings": file.get("settings") or [], "logic": logic}


def _deeper_than(value: object, limit: int) -> bool:
    """Whether objects and arrays nest more than `limit` levels, found without recursion."""
    stack = [(value, 1)]
    while stack:
        node, depth = stack.pop()
        if isinstance(node, (dict, list)):
            if depth > limit:
                return True
            stack.extend((child, depth + 1) for child in (node.values() if isinstance(node, dict) else node))
    return False


def _client_error(ex: BaseException) -> bool:
    """aiohttp's own errors, known by name so this module never imports aiohttp, and the operating system's."""
    return isinstance(ex, OSError) or any(cls.__name__ == "ClientError" for cls in type(ex).__mro__)


def aiohttp_fetch(session, timeout) -> Fetch:
    """A Fetch over an aiohttp ClientSession that reads at most CAP bytes and sends no request off GitHub's hosts.

    Redirects are followed here rather than by aiohttp, so each hop is checked before it is requested.
    """

    async def fetch(url: str, accept: str) -> bytes:
        name = unquote(urlsplit(url).path.rsplit("/", 1)[-1])
        too_large = TooLarge(name)
        try:
            for _ in range(MAX_REDIRECTS + 1):
                parts = urlsplit(url)
                # The netloc whole, so a port or a user name is off the hosts too.
                if parts.netloc.lower() not in ALLOWED_HOSTS:
                    raise ExternalError("fetch", f"Could not reach GitHub: it sent the request to {parts.netloc}")
                if parts.scheme != "https":
                    raise ExternalError("fetch", f"Could not reach GitHub: it sent the request to {parts.netloc} without https")
                async with session.get(url, headers={"Accept": accept, "User-Agent": "HassSharp"}, timeout=timeout,
                                       allow_redirects=False) as response:
                    location = response.headers.get("Location")
                    if response.status in _REDIRECTS and location:
                        url = urljoin(url, location)
                        continue
                    if response.status != 200:
                        raise HttpStatus(response.status)
                    length = response.headers.get("Content-Length")
                    if length is not None and length.strip().isdigit() and int(length) > CAP:
                        raise too_large
                    body = bytearray()
                    async for chunk in response.content.iter_chunked(CHUNK):
                        body += chunk
                        if len(body) > CAP:
                            raise too_large
                    return bytes(body)
            raise ExternalError("fetch", f"Could not reach GitHub: it redirected the request more than {MAX_REDIRECTS} times")
        except (ExternalError, HttpStatus):
            raise
        except TimeoutError:
            raise ExternalError("fetch", "Could not reach GitHub: it did not answer in time") from None
        except Exception as ex:
            if not _client_error(ex):
                raise
            raise ExternalError("fetch", f"Could not reach GitHub: {str(ex) or type(ex).__name__}") from None

    return fetch


def _refusal(status: int, path: str | None = None) -> ExternalError:
    """A refused request: the API's limit for requests without signing in, or, for a file (`path`), the raw host's
    refusal, which is not that limit."""
    if status in (403, 429):
        return ExternalError("fetch", _LIMIT if path is None else f"GitHub refused to send {path}: try again later")
    return ExternalError("fetch", f"Could not reach GitHub: it answered with HTTP status {status}")


class GitHub:
    """GitHub's API for a ref's commit, and its raw files at that commit."""

    def __init__(self, fetch: Fetch) -> None:
        self._fetch = fetch

    async def resolve(self, repo: Repo) -> str:
        """The sha of the commit the ref names, or of the default branch's head when there is none."""
        name = f"{repo.owner}/{repo.repo}"
        url = f"https://api.github.com/repos/{name}/commits/{quote(repo.ref or 'HEAD', safe='/')}"
        try:
            # The sha alone: the whole commit, with each changed file's patch, can be larger than CAP.
            data = await self._fetch(url, "application/vnd.github.sha")
        except HttpStatus as ex:
            if ex.status in (404, 422):
                raise ExternalError("not_found", f"GitHub has no public repository {name}" + (
                    f" with a branch, tag or commit {repo.ref}" if repo.ref else "")) from None
            if ex.status == 409:
                raise ExternalError("not_found", f"GitHub's repository {name} has no commits yet") from None
            raise _refusal(ex.status) from None
        try:
            sha = data.decode("ascii").strip()
        except UnicodeDecodeError:
            sha = None
        if sha is None or not _SHA.fullmatch(sha):
            raise ExternalError("fetch", f"Could not reach GitHub: it answered without the commit of {name}")
        return sha

    async def file(self, owner: str, repo: str, sha: str, path: str) -> bytes:
        """A file at a commit; a 404 is passed up as HttpStatus for the caller to word."""
        try:
            return await self._fetch(f"https://raw.githubusercontent.com/{owner}/{repo}/{sha}/{quote(path, safe='/')}", "*/*")
        except HttpStatus as ex:
            if ex.status == 404:
                raise
            raise _refusal(ex.status, path) from None


class Backend(Protocol):
    """HassSharp's stores, as the flow uses them. The lists and the read raise ExternalError for a refusal; the import
    and the save answer .NET's envelope, a refusal included."""

    async def libraries(self) -> list[dict]:
        """The stored library files, each `{id, name, revision}`."""

    async def definitions(self) -> tuple[list[dict], list[str]]:
        """The stored entity definitions, each `{id, name, revision}`, and the ids of the files that cannot be read."""

    async def read_definition(self, id: str) -> dict:
        """A stored definition as `read` answers it."""

    async def import_libraries(self, libraries: list[dict]) -> dict:
        """The node library `import` request's envelope."""

    async def save_definition(self, request: dict) -> dict:
        """An entity definition `save` request's envelope."""


async def _manifest(github: GitHub, owner: str, repo: str, sha: str) -> list[Item]:
    try:
        data = await github.file(owner, repo, sha, "hass_sharp.toml")
    except HttpStatus:
        raise ExternalError("not_found", f"{owner}/{repo} has no hass_sharp.toml at its root at {sha[:7]}") from None
    return parse_manifest(data)


async def _contents(github: GitHub, owner: str, repo: str, sha: str, item: Item) -> str | dict:
    """A listed library's source or entity's body; ItemProblem when the file cannot be imported, ExternalError when it
    cannot be fetched."""
    try:
        data = await github.file(owner, repo, sha, item.path)
    except HttpStatus:
        raise ItemProblem(f"{item.path} is not in the repository at {sha[:7]}") from None
    except TooLarge:
        raise ItemProblem(f"{item.path} is larger than 2 MiB") from None
    return library_source(item, data) if item.kind == "library" else entity_body(item, data)


def _named(listed: list[dict], name: str) -> list[dict]:
    """The stored files or definitions of exactly that name."""
    return [{"id": d["id"], "revision": d["revision"], "name": d["name"]} for d in listed if d.get("name") == name]


async def inspect(github: GitHub, backend: Backend, url: object) -> dict:
    """The repository at the commit its address names, and each item its manifest lists: the stored files or
    definitions of that name, and why the file cannot be imported, if it cannot. Every listed file is fetched."""
    if not isinstance(url, str):
        raise ExternalError("invalid_request", _INVALID_URL)
    repo = parse_url(url)
    sha = await github.resolve(repo)
    items = await _manifest(github, repo.owner, repo.repo, sha)
    problems = []
    for item in items:
        try:
            await _contents(github, repo.owner, repo.repo, sha, item)
            problems.append(None)
        except ItemProblem as ex:
            problems.append(str(ex))
    libraries = await backend.libraries()
    definitions, _ = await backend.definitions()
    return {"owner": repo.owner, "repo": repo.repo, "ref": repo.ref, "sha": sha, "items": [
        {"kind": item.kind, "name": item.name, "description": item.description, "path": item.path,
         "existing": _named(libraries if item.kind == "library" else definitions, item.name), "problem": problem}
        for item, problem in zip(items, problems)]}


def _invalid(message: str) -> ExternalError:
    return ExternalError("invalid_request", message)


def _revision(value: object) -> bool:
    return type(value) is int and value >= 0


def _request_picks(request: dict) -> tuple[str, str, str, dict[tuple[str, str], dict]]:
    """The import request's repository, commit and picks by (kind, path), checked before anything is fetched."""
    owner, repo, sha, picks = (request.get(key) for key in ("owner", "repo", "sha", "picks"))
    if (not isinstance(owner, str) or not _OWNER.fullmatch(owner) or not isinstance(repo, str) or not _REPO.fullmatch(repo)
            or repo in (".", "..")):
        raise _invalid("owner and repo must name the GitHub repository inspect answered")
    if not isinstance(sha, str) or not _SHA.fullmatch(sha):
        raise _invalid("sha must be the 40-character commit sha inspect answered")
    if not isinstance(picks, list) or not picks:
        raise _invalid("picks must list at least one item to import")
    picked: dict[tuple[str, str], dict] = {}
    for number, pick in enumerate(picks, 1):
        if not isinstance(pick, dict) or not isinstance(pick.get("path"), str):
            raise _invalid(f"pick {number} must be an object with the path of a listed file")
        path = pick["path"]
        if pick.get("kind") not in _EXTENSIONS:
            raise _invalid(f"{path}: kind must be library or entity")
        if pick.get("mode") not in ("new", "overwrite"):
            raise _invalid(f"{path}: mode must be new or overwrite")
        if (pick["kind"], path) in picked:
            raise _invalid(f"{path} is picked twice")
        picked[(pick["kind"], path)] = pick
    return owner, repo, sha, picked


def _check_pick(item: Item, pick: dict, libraries: list[dict], definitions: list[dict]) -> None:
    """A pick against the stored files and definitions: a library file name is never stored twice."""
    if item.kind == "library":
        same = [d["id"] for d in libraries if d.get("name") == item.name]
        if same and pick["mode"] == "new":
            raise _invalid(f"{item.path}: a library file named {item.name} is stored, so it can only overwrite it or be skipped")
        if not same and pick["mode"] == "overwrite":
            raise _invalid(f"{item.path}: no library file named {item.name} is stored, so it can only be imported as new")
        if pick["mode"] == "new" and "target" in pick:
            raise _invalid(f"{item.path}: a new library file takes no target")
        if pick["mode"] == "overwrite" and pick.get("target") not in same:
            raise _invalid(f"{item.path}: target must be the id of the stored library file named {item.name}: {', '.join(same)}")
    elif pick["mode"] == "overwrite" and pick.get("target") not in [d["id"] for d in definitions if d.get("name") == item.name]:
        raise _invalid(f"{item.path}: target must be the id of a stored entity definition named {item.name}")
    if pick["mode"] == "overwrite" and not _revision(pick.get("expectedRevision")):
        raise _invalid(f"{item.path}: expectedRevision must be the revision inspect answered")


def _errors(diagnostics: object, describe: Callable[[dict], str]) -> list[str]:
    """Up to three error diagnostics, each described."""
    errors = [d for d in diagnostics if isinstance(d, dict) and d.get("severity") in (3, "error")] if isinstance(diagnostics, list) else []
    return [describe(d) for d in errors[:3]]


def _refusal_message(error: object, describe_validation: Callable[[dict], list[str]]) -> str:
    """The sentence of a refused import or save."""
    code, detail = (error.get("code"), error.get("detail")) if isinstance(error, dict) else (None, None)
    if code == "conflict":
        return _CONFLICT
    if code == "validation" and isinstance(detail, dict):
        return "; ".join(describe_validation(detail)) or "It is not valid"
    return detail if isinstance(detail, str) else "It could not be saved"


def _library_problems(detail: dict, names: dict[str, str]) -> list[str]:
    """A refused library import's errors, each `{file name} {line}:{column}: {message}`, then what it would break."""
    problems = _errors(detail.get("diagnostics"), lambda d: f"{names.get(d.get('sourceId'), d.get('sourceId'))} "
                                                           f"{d.get('startLine')}:{d.get('startColumn')}: {d.get('message')}")
    broken = [*(detail.get("automations") or []), *(detail.get("definitions") or [])]
    return problems + [f"it would break {b.get('name') or b.get('id')}" for b in broken if isinstance(b, dict)]


def _kept_settings(settings: object, stored: object) -> object:
    """The imported settings, each that the stored definition has by key and type marked to keep its stored value."""
    if not isinstance(settings, list):
        return settings
    types = {s["key"]: s.get("type") for s in stored if isinstance(s, dict) and isinstance(s.get("key"), str)} if isinstance(stored, list) else {}
    kept = []
    for setting in settings:
        if isinstance(setting, dict) and setting.get("key") in types and setting.get("type") == types[setting["key"]]:
            setting = {**{k: v for k, v in setting.items() if k not in ("value", "hasValue")}, "keep": True}
        kept.append(setting)
    return kept


def _signature_settings(config: dict) -> dict[str, dict]:
    signature = config.get("signature")
    settings = signature.get("settings") if isinstance(signature, dict) else None
    return {s["key"]: s for s in settings if isinstance(s, dict) and isinstance(s.get("key"), str)} if isinstance(settings, list) else {}


def _nodes(graph: object) -> list[dict]:
    nodes = graph.get("nodes") if isinstance(graph, dict) else None
    return [n for n in nodes if isinstance(n, dict)] if isinstance(nodes, list) else []


def _pins_wired(graph: dict) -> set[tuple[object, object]]:
    """The (node id, pin) of each input a data edge of the graph fills."""
    edges = graph.get("edges") if isinstance(graph, dict) else None
    return {(e.get("target"), e.get("targetPort")) for e in edges if isinstance(e, dict) and e.get("kind") == "data"} \
        if isinstance(edges, list) else set()


def keep_device_inputs(imported: dict, existing: dict) -> dict:
    """A copy of the imported graph in which each C# entity or device node keeps the values typed into the stored
    graph's node of the same id and kind: each stored literal argument whose setting has the same type and domain in
    the imported node's signature as in the stored one's, and whose pin no edge of the imported graph fills. A file's
    node without a signature is taken to have the stored node's settings: the save works its pins out from its code
    again (Ruling 14) and refuses an argument that does not fit them. A stored argument of another kind, such as a value
    cleared, is kept only where the file's node sets that argument too, and only when both signatures give its setting
    the same type and domain."""
    graph = copy.deepcopy(imported)
    stored = {n.get("id"): n for n in _nodes(existing)}
    wired = _pins_wired(graph)
    for node in _nodes(graph):
        before = stored.get(node.get("id"))
        if node.get("kind") not in _C_SHARP_NODES or before is None or before.get("kind") != node.get("kind"):
            continue
        config, old = node.get("config"), before.get("config")
        if not isinstance(config, dict) or not isinstance(old, dict):
            continue
        old_arguments = old.get("arguments")
        if not isinstance(old_arguments, dict):
            continue
        arguments = config.get("arguments", {})
        if not isinstance(arguments, dict):
            continue
        old_settings = _signature_settings(old)
        has_signature = isinstance(config.get("signature"), dict)
        settings = _signature_settings(config) if has_signature else old_settings
        for key, value in old_arguments.items():
            setting, old_setting = settings.get(key), old_settings.get(key)
            if setting is None or old_setting is None or (node.get("id"), key) in wired:
                continue
            if (setting.get("type"), setting.get("domain")) != (old_setting.get("type"), old_setting.get("domain")):
                continue
            literal = isinstance(value, dict) and value.get("kind") == "literal"
            if literal or (key in arguments and has_signature):
                arguments[key] = copy.deepcopy(value)
                config["arguments"] = arguments
    return graph


def _outcome(item: Item, outcome: str, id: str | None = None, enabled: bool | None = None, message: str | None = None) -> dict:
    return {"kind": item.kind, "name": item.name, "path": item.path, "outcome": outcome, "id": id, "enabled": enabled, "message": message}


async def import_items(github: GitHub, backend: Backend, request: dict) -> dict:
    """Imports what was picked from the repository at the inspected commit: every picked file is fetched again first
    (no API call), then the libraries in one `import`, then each entity definition, saved on its own. A refused library
    import stops before any definition; a refused definition leaves the libraries and the others imported."""
    owner, repo, sha, picked = _request_picks(request)
    items = await _manifest(github, owner, repo, sha)
    listed = {(item.kind, item.path) for item in items}
    for kind, path in picked:
        if (kind, path) not in listed:
            raise _invalid(f"{path} is not a {'node library' if kind == 'library' else 'entity definition'} "
                          f"hass_sharp.toml lists at {sha[:7]}")
    libraries = await backend.libraries()
    definitions, unreadable = await backend.definitions()
    chosen = [(item, picked[(item.kind, item.path)]) for item in items if (item.kind, item.path) in picked]
    for item, pick in chosen:
        _check_pick(item, pick, libraries, definitions)
    contents = {}
    for item, _ in chosen:
        try:
            contents[item] = await _contents(github, owner, repo, sha, item)
        except ItemProblem as ex:
            raise _invalid(str(ex)) from None

    stored_ids = {d["id"] for d in libraries}
    used = {item: pick["target"] if pick["mode"] == "overwrite"
            else item.id if item.id is not None and item.id not in stored_ids else f"library-{uuid.uuid4()}"
            for item, pick in chosen if item.kind == "library"}
    # The ids a definition's `library:` kinds are rewritten to: the one each library is stored under after this import.
    kinds = {}
    for item in items:
        if item.kind == "library" and item.id is not None:
            same = [d["id"] for d in libraries if d.get("name") == item.name]
            if item in used:
                kinds[item.id] = used[item]
            elif len(same) == 1:
                kinds[item.id] = same[0]
    answer = {"owner": owner, "repo": repo, "sha": sha, "items": [], "libraries": None}
    rebuilt: dict[str, int] = {}
    library_picks = [(item, pick) for item, pick in chosen if item.kind == "library"]
    entity_picks = [(item, pick) for item, pick in chosen if item.kind == "entity"]
    if library_picks:
        imported = await backend.import_libraries([
            {"id": used[item], "name": item.name, "source": contents[item],
             "expectedRevision": pick["expectedRevision"] if pick["mode"] == "overwrite" else 0} for item, pick in library_picks])
        if not imported.get("ok"):
            names = {**{d["id"]: d.get("name") for d in libraries}, **{used[item]: item.name for item, _ in library_picks}}
            message = _refusal_message(imported.get("error"), lambda detail: _library_problems(detail, names))
            answer["items"] = ([_outcome(item, "failed", message=message) for item, _ in library_picks]
                               + [_outcome(item, "failed", message=_NOT_ATTEMPTED) for item, _ in entity_picks])
            return answer
        answer["libraries"] = _stored_libraries(imported.get("result"))
        rebuilt = _rebuilt_revisions(imported.get("result"))
        answer["items"] = [_outcome(item, "overwritten" if pick["mode"] == "overwrite" else "imported", used[item])
                           for item, pick in library_picks]
    taken = {d["id"] for d in definitions} | set(unreadable)
    for item, pick in entity_picks:
        try:
            outcome = await _import_entity(backend, item, pick, contents[item], kinds, taken, rebuilt)
        except Exception as ex:  # noqa: BLE001 - the libraries are stored: each item still gets its line
            _LOGGER.exception("External import of %s failed", item.path)
            outcome = _outcome(item, "failed", message=f"Not imported: {str(ex) or type(ex).__name__}")
        answer["items"].append(outcome)
    return answer


def _stored_libraries(result: object) -> dict | None:
    """What the panel uses of a library import's answer: the generation and each file where it is now stored."""
    if not isinstance(result, dict):
        return None
    documents = result.get("documents")
    return {"generation": result.get("generation"), "documents": [
        {"id": d.get("id"), "name": d.get("name"), "revision": d.get("revision")}
        for d in documents if isinstance(d, dict)] if isinstance(documents, list) else []}


def _rebuilt_revisions(result: object) -> dict[str, int]:
    """Each definition a library import rebuilt, by id, and the revision it is now stored at. CommitLibraries rebuilds
    every definition at exactly its next revision (EntityDefinitionStore.Rebuilt) and answers each in `definitions`."""
    definitions = result.get("definitions") if isinstance(result, dict) else None
    revisions = {}
    for answered in definitions if isinstance(definitions, list) else []:
        document = answered.get("document") if isinstance(answered, dict) else None
        if isinstance(document, dict) and isinstance(document.get("id"), str) and _revision(document.get("revision")):
            revisions[document["id"]] = document["revision"]
    return revisions


async def _import_entity(backend: Backend, item: Item, pick: dict, body: dict, kinds: dict[str, str], taken: set[str],
                         rebuilt: dict[str, int]) -> dict:
    graph = rewrite_library_kinds(body["logic"]["graph"], kinds)
    settings = body["settings"]
    if pick["mode"] == "new":
        id = definition_id(item.name, taken)
        taken.add(id)
        request = {"operation": "save", "id": id, "expectedRevision": 0, "enabled": False}
    else:
        id = pick["target"]
        try:
            stored = await backend.read_definition(id)
        except ExternalError as ex:
            return _outcome(item, "failed", message=_CONFLICT if ex.code == "not_found" else ex.message)
        logic = stored.get("logic")
        settings = _kept_settings(settings, stored.get("settings"))
        graph = keep_device_inputs(graph, logic.get("graph") if isinstance(logic, dict) else {})
        # The libraries' import rebuilt it at its next revision: from the revision inspect answered, the rebuild is the
        # only change since. From any other, something else changed it, and the save at the picked revision conflicts.
        expected = pick["expectedRevision"]
        if rebuilt.get(id) == expected + 1:
            expected = rebuilt[id]
        request = {"operation": "save", "id": id, "expectedRevision": expected}
    # deriveSignatures: each C# node's pins are written from the code of the libraries now stored (Ruling 14).
    request |= {"deriveSignatures": True, "definition": {**body, "settings": settings, "logic": {**body["logic"], "graph": graph}}}
    saved = await backend.save_definition(request)
    if not saved.get("ok"):
        message = _refusal_message(saved.get("error"), lambda detail: _errors(detail.get("diagnostics"), lambda d: str(d.get("message"))))
        return _outcome(item, "failed", message=message)
    if pick["mode"] == "new":
        return _outcome(item, "imported", id, False)
    document = (saved.get("result") or {}).get("document")
    enabled = document.get("enabled") if isinstance(document, dict) else None
    return _outcome(item, "overwritten", id, enabled if isinstance(enabled, bool) else None)
