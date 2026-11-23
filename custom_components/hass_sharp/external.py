"""Importing node libraries and entity definitions from a public GitHub repository's hass_sharp.toml.

The rules (the URL forms, the manifest, its paths, sizes and ids, the rewrite of library node kinds) and the fetcher.
The standard library only, so tests load this file by its path without Home Assistant or aiohttp.
"""
import copy
import json
import re
import tomllib
import unicodedata
from collections.abc import Awaitable, Callable, Iterable
from dataclasses import dataclass
from urllib.parse import quote, unquote, urlsplit

CAP = 2 * 1024 * 1024
"""The most bytes of any one fetched file, hass_sharp.toml included."""
MAX_ITEMS = 50
ALLOWED_HOSTS = frozenset({"api.github.com", "raw.githubusercontent.com"})
CHUNK = 64 * 1024

_OWNER = re.compile(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})")
_REPO = re.compile(r"[A-Za-z0-9._-]{1,100}")
_LIBRARY_ID = re.compile(r"[a-z0-9][a-z0-9_-]{0,79}")
_SHA = re.compile(r"[0-9a-f]{40}")
_INVALID_URL = ("Paste a GitHub repository's address, such as https://github.com/owner/repo or "
                "https://github.com/owner/repo/tree/main")
_LIMIT = "GitHub refused the request: it allows 60 requests an hour without signing in; try again later"
_EXTENSIONS = {"library": ".cs", "entity": ".json"}


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
"""`(url, accept)`: the body, at most CAP bytes; raises HttpStatus, or ExternalError("fetch", …)."""


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
    logic = file.get("logic") if isinstance(file, dict) else None
    if not isinstance(logic, dict) or logic.get("kind") != "graph" or not isinstance(logic.get("graph"), dict):
        raise ItemProblem(f"{item.path} is not an entity definition: it needs logic of kind graph")
    return {"name": item.name, "settings": file.get("settings") or [], "logic": logic}


def _client_error(ex: BaseException) -> bool:
    """aiohttp's own errors, known by name so this module never imports aiohttp, and the operating system's."""
    return isinstance(ex, OSError) or any(cls.__name__ == "ClientError" for cls in type(ex).__mro__)


def aiohttp_fetch(session, timeout) -> Fetch:
    """A Fetch over an aiohttp ClientSession that stays on GitHub's hosts and reads at most CAP bytes."""

    async def fetch(url: str, accept: str) -> bytes:
        name = unquote(urlsplit(url).path.rsplit("/", 1)[-1])
        too_large = ExternalError("fetch", f"{name} is larger than 2 MiB")
        try:
            async with session.get(url, headers={"Accept": accept, "User-Agent": "HassSharp"}, timeout=timeout) as response:
                host = response.url.host
                if host not in ALLOWED_HOSTS:
                    raise ExternalError("fetch", f"Could not reach GitHub: it sent the request to {host}")
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
        except (ExternalError, HttpStatus):
            raise
        except TimeoutError:
            raise ExternalError("fetch", "Could not reach GitHub: it did not answer in time") from None
        except Exception as ex:
            if not _client_error(ex):
                raise
            raise ExternalError("fetch", f"Could not reach GitHub: {str(ex) or type(ex).__name__}") from None

    return fetch


def _refusal(status: int) -> ExternalError:
    if status in (403, 429):
        return ExternalError("fetch", _LIMIT)
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
            data = await self._fetch(url, "application/vnd.github+json")
        except HttpStatus as ex:
            if ex.status in (404, 422):
                raise ExternalError("not_found", f"GitHub has no public repository {name}" + (
                    f" with a branch, tag or commit {repo.ref}" if repo.ref else "")) from None
            if ex.status == 409:
                raise ExternalError("not_found", f"GitHub's repository {name} has no commits yet") from None
            raise _refusal(ex.status) from None
        try:
            sha = json.loads(data).get("sha")
        except (ValueError, AttributeError):
            sha = None
        if not isinstance(sha, str) or not _SHA.fullmatch(sha):
            raise ExternalError("fetch", f"Could not reach GitHub: it answered without the commit of {name}")
        return sha

    async def file(self, owner: str, repo: str, sha: str, path: str) -> bytes:
        """A file at a commit; a 404 is passed up as HttpStatus for the caller to word."""
        try:
            return await self._fetch(f"https://raw.githubusercontent.com/{owner}/{repo}/{sha}/{quote(path, safe='/')}", "*/*")
        except HttpStatus as ex:
            if ex.status == 404:
                raise
            raise _refusal(ex.status) from None
