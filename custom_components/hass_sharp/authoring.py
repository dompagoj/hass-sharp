"""Shared administrator authoring services; managed gates/store remain authoritative."""
import json

from aiohttp import ClientTimeout
from homeassistant.helpers import entity_registry as er
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from . import dotnet_tasks, external, native_triggers
from .const import DOMAIN


def error(code, detail):
    return {"version": 1, "ok": False, "error": {"code": code, "detail": detail}}


def answered_documents(operation, data):
    """The documents in the result of a `list`, `read` or `save` answer; none for another operation."""
    return data if operation == "list" else [data] if operation == "read" else [data["document"]] if operation == "save" else []


def definition_graph(request):
    """The graph of the request's definition when its logic is a graph, else None: a request of another shape goes to
    .NET as it is, which answers what is wrong with it."""
    definition = request.get("definition")
    logic = definition.get("logic") if isinstance(definition, dict) else None
    graph = logic.get("graph") if isinstance(logic, dict) and logic.get("kind") == "graph" else None
    return graph if isinstance(graph, dict) else None


def ready_entry(hass):
    """The config entry that takes commands, as (its id, its state), or (None, None). The commands are registered once;
    the automations exist only while an entry is set up and not unloading."""
    return next(((entry_id, state) for entry_id, state in hass.data.get("hass_sharp_llm", {}).items() if state.ready), (None, None))


def registered_entities(hass, entry_id):
    """On the loop: the config entry's entity registry entries of definitions, by definition id, each list sorted by
    unique id. A definition's entity has the unique id `<definition id>:<entity key>`, or `<definition id>:<device
    key>:<entity key>` for one a C# device's code added, and a definition id has no colon, so the part before the first
    one is the definition (`plug2:relay` is not `plug`'s); what follows it is not read here. One pass for a whole answer."""
    found = {}
    if entry_id is None:
        return found
    for entry in er.async_entries_for_config_entry(er.async_get(hass), entry_id):
        definition_id, colon, _ = entry.unique_id.partition(":")
        if entry.platform != DOMAIN or not colon:
            continue
        found.setdefault(definition_id, []).append({
            "uniqueId": entry.unique_id, "entityId": entry.entity_id, "domain": entry.entity_id.split(".", 1)[0],
            "name": entry.name or entry.original_name or None, "disabled": entry.disabled_by is not None})
    for entries in found.values():
        entries.sort(key=lambda e: e["uniqueId"])
    return found


def runnable_triggers(document, graph=None):
    """The Trigger steps of the document's graph (an automation's `graph` unless another is given), each with whether
    Run now may run it."""
    graph = document.get("graph", {}) if graph is None else graph
    return [{"triggerId": node["id"], "method": "Run_" + node["id"].encode().hex().upper(),
             "manual": node.get("config", {}).get("manual", True),
             "runnable": bool(document.get("currentRevisionActive") and node.get("config", {}).get("manual", True))}
            for node in graph.get("nodes", []) if node.get("kind") == "trigger"]


def definition_triggers(document):
    """A definition's Trigger steps as an automation's are given (`runnable_triggers`), read from its `logic.graph`;
    none for a document without one. One runs only while the definition runs its saved revision and is running (not still
    in setup, nor failed), as .NET's `run` requires."""
    logic = document.get("logic")
    graph = logic.get("graph") if isinstance(logic, dict) and logic.get("kind") == "graph" else None
    running = bool(document.get("currentRevisionActive") and document.get("status") == "running")
    return runnable_triggers({**document, "currentRevisionActive": running}, graph) if isinstance(graph, dict) else []


class ExternalBackend:
    """external.Backend over the service: .NET's library and definition stores, read as they are, and each save made
    through `entities`, so Home Assistant's trigger check and `registeredEntities` apply as for any save."""

    def __init__(self, service):
        self.service = service

    async def _result(self, method, request):
        answer = await self.service._request(method, request, is_admin=True)
        if not answer.get("ok"):
            raise external.ExternalError(answer["error"]["code"], answer["error"]["detail"])
        return answer

    async def libraries(self):
        answer = await self._result(self.service.manager.NodeLibraryRequest, {"operation": "list"})
        return [{"id": d["id"], "name": d["name"], "revision": d["revision"]} for d in answer["result"]["documents"]]

    async def definitions(self):
        answer = await self._result(self.service.manager.EntityRequest, {"operation": "list"})
        return ([{"id": d["id"], "name": d["name"], "revision": d["revision"]} for d in answer["result"]],
                [d["id"] for d in answer.get("unreadable") or []])

    async def read_definition(self, id):
        return (await self._result(self.service.manager.EntityRequest, {"operation": "read", "id": id}))["result"]

    async def import_libraries(self, libraries):
        return await self.service._request(self.service.manager.NodeLibraryRequest, {"operation": "import", "libraries": libraries}, is_admin=True)

    async def save_definition(self, request):
        return await self.service.entities(request, is_admin=True)

    async def catalog(self):
        return (await self._result(self.service.manager.NodeLibraryRequest, {"operation": "catalog"}))["result"]["catalog"]


class AuthoringService:
    def __init__(self, hass, manager):
        self.hass = hass
        self.manager = manager

    async def _request(self, method, request, *, is_admin):
        if not is_admin:
            raise PermissionError("Administrator required")
        if not isinstance(request, dict):
            raise ValueError("Authoring request must be an object")
        result = await dotnet_tasks.async_run_dotnet_task(self.hass, method, json.dumps(request))
        return json.loads(result)

    async def _checked_preview(self, method, operation, preview_request, graph):
        """A preview or save of a graph, checked as Home Assistant checks its triggers: .NET previews it
        (`preview_request`), then `native_triggers` validates the graph's Home Assistant triggers, whose diagnostics
        follow the preview's (an error as severity 3). Returns the answer to give, which is the preview, a refusal of
        .NET's, or a save refused in the `validation` form before .NET stores anything; None when the save may go on.
        A definition refused for listing `entities` (`definition.entitiesNotAllowed`), or for sending a device
        (`definition.deviceNotAllowed`), has nothing else checked, its triggers included."""
        preview = await self._request(method, preview_request, is_admin=True)
        if not preview.get("ok"):
            return preview
        nodes = graph.get("nodes")
        listed = any(isinstance(d, dict) and d.get("code") in ("definition.entitiesNotAllowed", "definition.deviceNotAllowed") for d in preview["result"].get("diagnostics", []))
        if not listed and isinstance(nodes, list) and all(isinstance(n, dict) and isinstance(n.get("config", {}), dict) for n in nodes):
            diagnostics = await native_triggers.async_validate_graph_triggers(self.hass, graph)
            preview["result"]["diagnostics"].extend({**d, "severity": 3 if d.get("severity") == "error" else d.get("severity")} for d in diagnostics)
        preview["result"]["valid"] = not any(d.get("severity") in (3, "error") for d in preview["result"]["diagnostics"])
        if operation == "preview":
            return preview
        if not preview["result"]["valid"]:
            return error("validation", preview["result"])
        return None

    async def visual(self, request, *, is_admin):
        if not is_admin:
            raise PermissionError("Administrator required")
        # A JSON snapshot prevents caller edits during awaited HA validation from
        # changing the document subsequently passed to the managed revision gate.
        request = json.loads(json.dumps(request))
        operation = request.get("operation")
        if operation in ("preview", "save"):
            graph = request.get("graph")
            if not isinstance(graph, dict):
                return error("invalid_request", "graph must be an object")
            # deriveSignatures: the managed side writes each Custom C# node's pins from its code first.
            derive = request.get("deriveSignatures") is True
            preview_request = {"operation": "preview", "graph": graph, **({"deriveSignatures": True} if derive else {})}
            if (answer := await self._checked_preview(self.manager.VisualRequest, operation, preview_request, graph)) is not None:
                return answer
        result = await self._request(self.manager.VisualRequest, request, is_admin=True)
        if result.get("ok"):
            documents = answered_documents(operation, result["result"])
            # The automations page's list (view "home") sends outlines, not graphs: the page works its triggers out itself.
            for document in documents:
                if "graph" in document:
                    document["triggers"] = runnable_triggers(document)
        return result

    async def node_library(self, request, *, is_admin):
        return await self._request(self.manager.NodeLibraryRequest, request, is_admin=is_admin)

    async def entities(self, request, *, is_admin):
        """An entity definition request, answered by .NET (`run` too, which runs a Trigger step by hand). A
        definition's Home Assistant triggers are checked on preview and save as an automation's are (`visual`), with
        `deriveSignatures` passed on to that preview, and every definition a list, read or save answers gains
        `registeredEntities`, its entities in the entity registry, which has them while it is off or failed too, and
        `triggers`, its Trigger steps as an automation's are given."""
        if not is_admin:
            raise PermissionError("Administrator required")
        if not isinstance(request, dict):
            raise ValueError("Authoring request must be an object")
        # Before the first await: the entry whose ready state let the command in. An unload that starts meanwhile
        # waits for the command but no longer calls the entry ready.
        entry_id, _ = ready_entry(self.hass)
        # As visual(): a snapshot, so caller edits while Home Assistant validates do not change what .NET saves.
        request = json.loads(json.dumps(request))
        operation = request.get("operation")
        if operation in ("preview", "save") and (graph := definition_graph(request)) is not None:
            # deriveSignatures: the managed side writes each Custom C# step's pins from its code first, as for an automation.
            derive = request.get("deriveSignatures") is True
            preview_request = {"operation": "preview", "definition": request["definition"], **({"deriveSignatures": True} if derive else {})}
            if (answer := await self._checked_preview(self.manager.EntityRequest, operation, preview_request, graph)) is not None:
                return answer
        result = await self._request(self.manager.EntityRequest, request, is_admin=True)
        if result.get("ok") and operation in ("list", "read", "save"):
            # Back on the loop once .NET has answered: Home Assistant's registry belongs to it.
            registered = registered_entities(self.hass, entry_id)
            for document in answered_documents(operation, result["result"]):
                document["registeredEntities"] = registered.get(document.get("id"), [])
                document["triggers"] = definition_triggers(document)
        return result

    async def external(self, request, *, is_admin):
        """The Add external dialog's request: `inspect {url}` a public GitHub repository's hass_sharp.toml, or `import
        {owner, repo, sha, picks}` what was picked from it (external.py), answered in the envelope, a refusal included."""
        if not is_admin:
            raise PermissionError("Administrator required")
        if not isinstance(request, dict):
            raise ValueError("Authoring request must be an object")
        operation = request.get("operation")
        if operation not in ("inspect", "import"):
            return error("invalid_request", "operation must be inspect or import")
        github = external.GitHub(external.aiohttp_fetch(async_get_clientsession(self.hass), ClientTimeout(total=30)))
        backend = ExternalBackend(self)
        try:
            if operation == "inspect":
                result = await external.inspect(github, backend, request.get("url"))
            else:
                result = await external.import_items(github, backend, request)
        except external.ExternalError as ex:
            return ex.answer()
        return {"version": 1, "ok": True, "result": result}

    async def device_ui(self, request, *, is_admin):
        """The Device tab's and the Stored data dialog's request (`read`, `image`, `act`, `counts`, `stores`,
        `delete_store`), answered by .NET in the envelope as it is, a refusal included. Nothing of it is logged here: an
        image answer carries the JPEG, an `act` the values typed into a card."""
        if not is_admin:
            raise PermissionError("Administrator required")
        if not isinstance(request, dict):
            raise ValueError("Device page request must be an object")
        return await self._request(self.manager.DeviceUiRequest, request, is_admin=True)

    async def run_now(self, automation_id, class_name, method, *, is_admin):
        if not is_admin:
            raise PermissionError("Administrator required")
        return json.loads(await self.hass.async_add_executor_job(self.manager.RunNow, automation_id, class_name, method))

    async def history(self, automation_id, offset, limit, status=None, *, is_admin):
        if not is_admin:
            raise PermissionError("Administrator required")
        statuses = list(status) if status else None
        return json.loads(await self.hass.async_add_executor_job(self.manager.GetRunHistory, automation_id, offset, limit, statuses))

    async def trace(self, run_id, *, is_admin):
        """Run trace v1 (run, live flag, graph snapshot, stored trace), or None for an unknown run."""
        if not is_admin:
            raise PermissionError("Administrator required")
        return json.loads(await self.hass.async_add_executor_job(self.manager.GetRunTrace, run_id))
