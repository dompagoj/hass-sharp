"""Administrator authoring API served by Home Assistant's built-in MCP server."""
from dataclasses import dataclass
import json
from pathlib import Path

import probatio as p
from homeassistant.helpers import area_registry, device_registry, llm, service, trigger
from homeassistant.helpers.json import JSONEncoder

from .authoring import AuthoringService, error
from .const import logger

API_ID = "hass_sharp"
MINIMUM_HA = "2026.11.0"
PROMPT = """Discover entities, services and installed triggers; fetch capabilities and the current node schema before authoring graph v4. Read the current document/revision before editing. Preview complete graphs or NodeLib source, fix errors, then save using expectedRevision. Saving activates automations and can enable startup/native/scheduled effects: obtain the user's authorization for those effects. Run only a saved current active trigger with user authorization, then inspect history. Library changes rebuild every graph and advance graph revisions; refresh open documents. Never guess CLR assignability, trigger IDs or service data. Old graph formats are unsupported and must be recreated."""


@dataclass
class EntryState:
    service: AuthoringService
    ready: bool = False
    unregister: object = None

    def close(self):
        self.ready = False
        if self.unregister is not None:
            self.unregister()
            self.unregister = None


async def require_admin(hass, context):
    user_id = context.context.user_id if context and context.context else None
    user = await hass.auth.async_get_user(user_id) if user_id else None
    if user is None or not user.is_admin:
        raise PermissionError("Administrator required")


# The graph remains the shared managed validator's contract, including rich custom
# CLR kinds. Config/input fields are intentionally open objects, never a second catalog.
OBJECT = p.Schema({}, extra=p.ALLOW_EXTRA)
NODE = p.Schema({p.Required("id"): str, p.Required("kind"): str,
                 p.Required("config"): OBJECT, p.Optional("position"): {p.Required("x"): p.Any(int, float), p.Required("y"): p.Any(int, float)},
                 p.Optional("size"): {p.Required("width"): p.Any(int, float), p.Required("height"): p.Any(int, float)},
                 p.Optional("label"): str})
EDGE = p.Schema({p.Required("id"): str, p.Required("source"): str, p.Required("target"): str,
                 p.Optional("kind"): p.In(["data", "execution"]), p.Optional("port"): str,
                 p.Optional("sourcePort"): str, p.Optional("targetPort"): str})
GRAPH = p.Schema({p.Required("version"): 4, p.Required("name"): str, p.Required("nodes"): [NODE], p.Required("edges"): [EDGE],
                  p.Optional("imports"): str, p.Optional("members"): str})
ID = p.All(str, p.Match(r"^[a-z0-9][a-z0-9_-]{0,79}$"))
REVISION = p.All(int, p.Range(min=0))
PAGE = {p.Optional("offset", default=0): p.All(int, p.Range(min=0)), p.Optional("limit", default=50): p.All(int, p.Range(min=1, max=100))}
ID_FIELD = {p.Required("id"): ID}
REV_FIELDS = {**ID_FIELD, p.Required("expectedRevision"): REVISION}
SPECS = {
    "get_capabilities": ({}, "Graph versions, constraints, typed scheduling semantics, C# guidance and complete harmless examples."),
    "get_node_schema": ({p.Optional("kind"): str}, "Current merged built-in/custom catalog, or one node with server-derived CLR types and assignability."),
    "discover": ({p.Required("kind"): p.In(["entities", "services", "devices", "areas", "triggers"]), p.Optional("query", default=""): str, **PAGE}, "Filter installed HA authoring capabilities before stable sorting/pagination; no trigger attachment."),
    "list": ({}, "List saved visual documents, revisions, activation status and stable trigger identities."),
    "read": (ID_FIELD, "Read a complete saved visual document, layout, revision and current activation identity."),
    "preview": ({p.Required("graph"): GRAPH}, "Validate graph and native HA triggers and compile without executing or attaching; return source/maps and diagnostics."),
    "save": ({**REV_FIELDS, p.Required("graph"): GRAPH}, "Persist and activate a complete graph at the expected revision. saved and active are distinct; startup/native effects may occur."),
    "delete": (REV_FIELDS, "Delete a saved graph at the exact expected revision and stop its runtime."),
    "run": ({**ID_FIELD, p.Required("triggerId"): str}, "Atomically resolve and dispatch a manually eligible trigger on the current saved active revision; may affect devices. Returns runId."),
    "history": ({**ID_FIELD, **PAGE}, "Read bounded run history for a saved visual document."),
    "library_list": ({}, "List saved NodeLib source documents and saved catalog generation."),
    "library_read": (ID_FIELD, "Read one saved library source and revision."),
    "library_preview": ({**ID_FIELD, p.Required("source"): str, p.Required("workspaceVersion"): str,
                         p.Optional("generation"): str, p.Optional("sources"): [{**ID_FIELD, p.Required("source"): str}]}, "Compile an immutable candidate library workspace without executing custom code. Returns candidate catalog, diagnostics and generation metadata."),
    "library_save": ({**REV_FIELDS, p.Required("name"): str, p.Required("source"): str}, "Save/rename a library at its revision, validate and rebuild all dependent graphs, advance their revisions and report each activation separately."),
    "library_delete": (REV_FIELDS, "Delete a library at its revision only if remaining libraries and all graphs compile; rebuild and activate all graphs."),
}
MUTATIONS = {"save", "delete", "run", "library_save", "library_delete"}


class HassSharpTool(llm.Tool):
    integration = "hass_sharp"

    def __init__(self, operation, state):
        self.operation = operation
        self.state = state
        self.name = "hass_sharp__" + operation
        schema, self.description = SPECS[operation]
        self.parameters = p.Schema(schema)
        readonly = operation not in MUTATIONS
        self.annotations = llm.ToolAnnotations(read_only=readonly, destructive=not readonly,
                                               idempotent=readonly, open_world=not readonly)

    async def async_call(self, hass, tool_input, llm_context):
        try:
            await require_admin(hass, llm_context)
            if not self.state.ready:
                return llm.ToolResult(error("not_ready", "HassSharp is not ready"), error=True)
            # HA APIInstance dispatch does not apply parameters; cached/direct calls
            # must validate too. Python bool is int, so reject it explicitly.
            for key in ("expectedRevision", "offset", "limit"):
                if key in tool_input.tool_args and type(tool_input.tool_args[key]) is not int:
                    raise ValueError(f"{key} must be an integer")
            args = self.parameters(tool_input.tool_args)
            result = await self._call(hass, args)
            return llm.ToolResult(result, error=result.get("ok") is False)
        except PermissionError:
            return llm.ToolResult(error("unauthorized", "Administrator required"), error=True)
        except (p.Invalid, ValueError, TypeError):
            return llm.ToolResult(error("invalid_request", "Arguments do not match the tool parameter schema"), error=True)
        except Exception:
            logger.exception("HassSharp MCP %s failed", self.operation)
            return llm.ToolResult(error("internal", "Request failed; check integration logs"), error=True)

    async def _call(self, hass, args):
        service_api = self.state.service
        operation = self.operation
        if operation.startswith("library_"):
            return await service_api.node_library({"operation": operation.removeprefix("library_"), **args}, is_admin=True)
        if operation in ("get_capabilities", "get_node_schema"):
            response = await service_api.node_library({"operation": "catalog"}, is_admin=True)
            if not response.get("ok"):
                return response
            catalog = response["result"]["catalog"]
            if operation == "get_node_schema":
                if "kind" not in args:
                    return response
                node = catalog["nodes"].get(args["kind"])
                if node is None:
                    return error("not_found", "Node kind not found")
                return {"version": 1, "ok": True, "result": {"kind": args["kind"], "node": node, "types": catalog["types"], "limits": catalog["limits"], "generation": response["result"]["generation"]}}
            examples = json.loads(await hass.async_add_executor_job(Path(__file__).with_name("authoring_examples.json").read_text))
            return {"version": 1, "ok": True, "result": {"graphVersions": [4], "envelopeVersion": 1, "minimumHomeAssistant": MINIMUM_HA,
                    "catalog": catalog, "generation": response["result"]["generation"], "guidance": PROMPT,
                    "semantics": "Control edges schedule execution; data edges read values and never execute producers. Branch/loop/try scopes restrict output visibility. Loop iterations use isolated frames; body outputs do not escape. Use server CLR assignableTo and nullable metadata; no implicit object downcasts. Custom methods see an invocation scope snapshot. C# custom code supports values, lastError and scoped outputs; libraries use Node/Port attributes in NodeLib. Preview compiles without running user code.",
                    "examples": examples}}
        if operation == "discover":
            return {"version": 1, "ok": True, "result": await discover(hass, **args)}
        if operation == "history":
            document = await service_api.visual({"operation": "read", "id": args["id"]}, is_admin=True)
            if not document.get("ok"):
                return document
            history = await service_api.history(document["result"]["automationId"], args["offset"], args["limit"], is_admin=True)
            return {"version": 1, "ok": True, "result": {"id": args["id"], **history}}
        return await service_api.visual({"operation": operation, **args}, is_admin=True)


async def discover(hass, kind, query, offset, limit):
    if kind == "entities":
        items = [{"id": s.entity_id, "state": s.state, "attributes": s.attributes} for s in hass.states.async_all()]
    elif kind == "services":
        descriptions = await service.async_get_all_descriptions(hass)
        items = [{"id": f"{domain}.{name}", "domain": domain, "service": name, **description}
                 for domain, services in descriptions.items() for name, description in services.items()]
    elif kind == "devices":
        items = [{"id": d.id, "name": d.name_by_user or d.name, "area_id": d.area_id, "manufacturer": d.manufacturer, "model": d.model}
                 for d in device_registry.async_get(hass).async_get_devices()]
    elif kind == "areas":
        items = [{"id": a.id, "name": a.name} for a in area_registry.async_get(hass).async_list_areas()]
    else:
        descriptions = await trigger.async_get_all_descriptions(hass)
        items = [{"id": key, "description": value} for key, value in descriptions.items()]
        existing = {i["id"] for i in items}
        for alias in ("state", "numeric_state", "event", "time", "time_pattern", "homeassistant", "template", "sun", "zone", "device", "tag"):
            if alias not in existing:
                items.append({"id": alias, "description": "Common Home Assistant trigger alias; preview validates configuration against this installation."})
    # Normalize registry values to JSON before matching/pagination.
    items = json.loads(json.dumps(items, cls=JSONEncoder))
    items = sorted((i for i in items if query.casefold() in json.dumps(i, ensure_ascii=False).casefold()), key=lambda i: i["id"])
    return {"kind": kind, "query": query, "offset": offset, "limit": limit, "total": len(items), "items": items[offset:offset + limit]}


class HassSharpAPI(llm.API):
    def __init__(self, hass, state):
        super().__init__(hass=hass, id=API_ID, name="HassSharp", requires_admin=True)
        self.state = state

    async def async_get_api_instance(self, llm_context):
        await require_admin(self.hass, llm_context)
        if not self.state.ready:
            raise RuntimeError("HassSharp is not ready")
        return llm.APIInstance(api=self, api_prompt=PROMPT, llm_context=llm_context,
                               tools=[HassSharpTool(name, self.state) for name in SPECS])


def register(hass, state):
    if not state.ready:
        raise RuntimeError("Cannot register HassSharp before initialization")
    state.unregister = llm.async_register_api(hass, HassSharpAPI(hass, state))
