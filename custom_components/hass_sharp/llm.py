"""Administrator authoring API served by Home Assistant's built-in MCP server."""
from dataclasses import dataclass
import json
from pathlib import Path

import probatio as p
from homeassistant.helpers import area_registry, device_registry, llm, trigger
from homeassistant.helpers.json import JSONEncoder

from .authoring import AuthoringService, error
from .const import logger
from .websocket import RUN_STATUSES

API_ID = "hass_sharp"
MINIMUM_HA = "2026.11.0"
PROMPT = """HassSharp builds Home Assistant automations as typed node graphs (version 5). Call get_capabilities once for the rules and examples, get_node_schema for each node kind you use (for service, pass the service id), and discover for real entity, service and trigger ids; never guess them. Read before editing, preview until there are no errors, then save with expectedRevision. Save, run, delete and library changes can attach triggers or control devices: get the user's consent first. After a run, check history, then trace. An entity_id:light input accepts only light ids: a plain entity_id literal or wire into it (such as the light.turn_on target) is an error; write the literal with type "entity_id:light" or wire it through a Convert node with that type. Library saves advance every graph's revision: read again before editing."""
# Returned by get_capabilities; the rules a model needs beyond the catalog itself.
SEMANTICS = {
    "execution": "Execution edges {source, target, port} schedule nodes; port is one of the source node's execution ports (next by default). Data edges {kind: data, source, sourcePort, target, targetPort} read a value that an already executed node produced; they never run the producer. Outputs produced inside a parallel branch, loop body or loop condition are not visible outside it; a loop publishes values only through its declared exports. An input is a literal {kind: literal, type, value}, a variable {kind: variable, name} naming another node's config.output, {kind: unset}, or a data edge, which takes precedence.",
    "types": "Type ids are keys of catalog.types, or parametric ids <base>:<param> built from catalog.parametricTypes, such as entity_id:light (values matching ^light\\.[a-z0-9_]+$). A type with refines narrows its parent: entity_id:light refines entity_id, which refines string. A value of type S fits an input of type T when S is T; when T refines another type, S must be T or refine it (entity_id:light fits entity_id and string, entity_id does not fit entity_id:light); otherwise S's root type must be T or be listed in that root's assignableTo (CLR types are checked by the server). A literal must match the pattern of every type in its chain. Convert narrows explicitly: its config.type accepts any resolvable id and the value is checked at run time.",
    "triggers": "A trigger's data outputs come from catalog.triggerOutputs: the '*' row (data: the whole normalised payload) plus every row whose |-separated key lists config.type (entity, daily, once) or, for type ha, 'ha:' + config.trigger.trigger (or platform). An entity_id output is typed entity_id:<domain> when the trigger names entities of one domain (config.entity, or trigger.entity_id). A value missing from the payload is not produced, and a node reading it fails at run time. Payloads: native HA triggers give Home Assistant's trigger variables plus context; entity {platform: entity, entity_id, from_state, to_state}; schedule {platform: schedule, schedule_id, planned}; manual and startup {platform}.",
    "service": "Call service (kind service): config.service is 'domain.service'; config.target is an operand holding an entity id or a target object {entity_id, device_id, area_id, floor_id, label_id}; config.fields maps each field you set to an operand and every key becomes the input field:<key>; config.returnResponse true adds the response output. For a service Home Assistant knows, fields are typed and constrained by its schema: an unknown field, a missing required field, a target on a service without one, and a returnResponse that contradicts the service's response mode are errors. An unknown service is a warning and its fields accept any type.",
    "runs": "history returns run history version 2, newest first, optionally filtered by status: Pending, Running, Succeeded, Failed, Cancelled, Skipped, Interrupted or Stopped (a Stop node ended the run). A failed run's error is its message; a cancelled run's error is the reason. Runs with hasTrace have a per-node trace: call trace with the runId.",
    "csharp": "Custom C# nodes run statements with values, lastError, TriggerData (the normalised trigger payload) and normal Automation members; graph imports and members add usings and class members. A library source is namespace contents (wrapped in namespace NodeLib) whose public static [Node] methods become node kinds. Preview compiles without running user code.",
}


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
                 p.Optional("label"): str})
EDGE = p.Schema({p.Required("id"): str, p.Required("source"): str, p.Required("target"): str,
                 p.Optional("kind"): p.In(["data", "execution"]), p.Optional("port"): str,
                 p.Optional("sourcePort"): str, p.Optional("targetPort"): str})
GRAPH = p.Schema({p.Required("version"): 5, p.Required("name"): str, p.Required("nodes"): [NODE], p.Required("edges"): [EDGE],
                  p.Optional("imports"): str, p.Optional("members"): str})
ID = p.All(str, p.Match(r"^[a-z0-9][a-z0-9_-]{0,79}$"))
REVISION = p.All(int, p.Range(min=0))
PAGE = {p.Optional("offset", default=0): p.All(int, p.Range(min=0)), p.Optional("limit", default=50): p.All(int, p.Range(min=1, max=100))}
ID_FIELD = {p.Required("id"): ID}
REV_FIELDS = {**ID_FIELD, p.Required("expectedRevision"): REVISION}
SERVICE_ID = p.All(str, p.Match(r"^[a-z0-9_]+\.[a-z0-9_]+$"))
SPECS = {
    "get_capabilities": ({}, "Start here, once per session. Returns the supported graph version (5), the full node catalog, the authoring rules (semantics: execution and data edges, the type refinement rule, trigger outputs, the Call service node, run history) and complete example graphs that validate and compile. Copy their shapes instead of inventing node or operand formats."),
    "get_node_schema": ({p.Optional("kind"): str, p.Optional("service"): SERVICE_ID}, "Ports and config of one node kind, with the type table and parametric types; omit kind for the whole catalog. Call it for every kind you add. For kind service, pass service ('domain.service') to get that service's field:<name> inputs with types and constraints, its target types and whether returnResponse must be on or off; ports come only from the fields you put in config.fields. For kind trigger the result includes triggerOutputs, which says which outputs each trigger type produces."),
    "discover": ({p.Required("kind"): p.In(["entities", "services", "devices", "areas", "triggers"]), p.Optional("query", default=""): str,
                  p.Optional("domain"): str, p.Optional("service"): SERVICE_ID, **PAGE},
                 "Find real ids in this Home Assistant before writing them into a graph; filtering happens before stable sorting and paging. entities, devices, areas, triggers: matched by query (entities also by domain). services: without service, one line per service (id, name, description, hasTarget, response), narrowed by domain and query; with service ('domain.service'), that one service's full schema with fields in Home Assistant's order. Ask for one service at a time: the full set is very large. Never calls a service or attaches a trigger."),
    "list": ({}, "List saved visual automations with id, name, revision, activation state and triggers [{triggerId, manual, runnable}]. Omits the generated source; read one document for that."),
    "read": (ID_FIELD, "Read one saved visual automation: its complete graph, revision (pass it as expectedRevision when saving), generated source and activation state (currentRevisionActive is true when the saved revision is the one running). Read before every edit; do not rebuild a graph from memory."),
    "preview": ({p.Required("graph"): GRAPH}, "Validate and compile a complete graph without saving, attaching triggers or running anything. Returns valid, diagnostics (nodeId, inputId, message; severity 3 is an error, 2 a warning) and the generated source. It checks service fields against Home Assistant's schemas and native triggers against Home Assistant: fix every error here before saving."),
    "save": ({**REV_FIELDS, p.Required("graph"): GRAPH}, "Save and activate a complete graph. A new id uses expectedRevision 0, an existing one the revision from read; a conflict means it changed meanwhile, so read again rather than retrying. Errors block the save, warnings do not. Activation can attach native, scheduled or startup triggers that control devices: get the user's consent first. The result reports saved and active separately, with activationError."),
    "delete": (REV_FIELDS, "Delete a saved visual automation at its exact revision and stop it. Ask the user first."),
    "run": ({**ID_FIELD, p.Required("triggerId"): str}, "Run one trigger of the saved, active revision now. triggerId is the trigger node id; list and read show which triggers are runnable. It can control devices: ask the user first. Returns runId; then call history and trace."),
    "history": ({**ID_FIELD, p.Optional("status"): [p.In(RUN_STATUSES)], **PAGE}, "Run history (version 2) of one saved visual automation, newest first, optionally only runs with the given statuses. Each run has runId, status, error (the failure message, or why it was cancelled), trigger (what fired it), actionLabel (the trigger node), timing and hasTrace."),
    "trace": ({p.Required("runId"): p.All(str, p.Length(min=1, max=64)), p.Optional("node"): str, p.Optional("graph", default=False): bool},
              "Per-node trace of one run (runId from run or history). By default: the run, live (still running), whether the trace is complete or truncated, a summary per node and the events in order (node, frame, status, port taken, t and ms timing, error) without values. Pass node for that node's events with their input and output values; pass graph true for the graph revision that ran. Use it to find which node failed and with what value."),
    "library_list": ({}, "List saved node library (C# NodeLib) sources and the saved catalog generation."),
    "library_read": (ID_FIELD, "Read one saved library source and its revision."),
    "library_preview": ({**ID_FIELD, p.Required("source"): str, p.Required("workspaceVersion"): str,
                         p.Optional("generation"): str, p.Optional("sources"): [{**ID_FIELD, p.Required("source"): str}]}, "Compile a candidate library source together with the saved ones without running it. Returns the candidate catalog with the custom node kinds and their CLR port types, and diagnostics."),
    "library_save": ({**REV_FIELDS, p.Required("name"): str, p.Required("source"): str}, "Save or rename a library at its revision. Every saved graph is revalidated and rebuilt: the save is refused if any would break, and on success every graph's revision advances and each activation is reported. Ask the user first."),
    "library_delete": (REV_FIELDS, "Delete a library at its revision, only if the remaining libraries and every graph still compile; rebuilds and activates all graphs. Ask the user first."),
}
MUTATIONS = {"save", "delete", "run", "library_save", "library_delete"}
# Input constraints a service field contributes to its field:<name> port (VisualNodeCatalog.ServiceFieldInput).
FIELD_CONSTRAINTS = ("min", "max", "integer", "enum", "enumLabels", "pattern", "minItems", "maxItems", "step", "unit", "selector", "advanced", "section", "example")


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
                return await node_schema(service_api, catalog, response["result"].get("generation"), args) if args else response
            examples = json.loads(await hass.async_add_executor_job(Path(__file__).with_name("authoring_examples.json").read_text))
            return {"version": 1, "ok": True, "result": {"graphVersions": [5], "envelopeVersion": 1, "minimumHomeAssistant": MINIMUM_HA,
                    "catalog": catalog, "generation": response["result"]["generation"], "guidance": PROMPT,
                    "semantics": SEMANTICS, "examples": examples}}
        if operation == "discover":
            return await discover(hass, service_api, **args)
        if operation == "history":
            document = await service_api.visual({"operation": "read", "id": args["id"]}, is_admin=True)
            if not document.get("ok"):
                return document
            history = await service_api.history(document["result"]["automationId"], args["offset"], args["limit"], args.get("status"), is_admin=True)
            return {"version": 1, "ok": True, "result": {"id": args["id"], **history}}
        if operation == "trace":
            data = await service_api.trace(args["runId"], is_admin=True)
            if data is None:
                return error("not_found", "Run not found")
            return {"version": 1, "ok": True, "result": trace_view(data, args.get("node"), args["graph"])}
        result = await service_api.visual({"operation": operation, **args}, is_admin=True)
        if result.get("ok"):
            data = result["result"]
            for document in data if operation == "list" else [data] if operation == "read" else [data.get("document")] if operation == "save" else []:
                drop_node_sizes(document)
        return result


def drop_node_sizes(document):
    """Documents saved before node size was removed still carry it; GRAPH rejects it, so a read-edit-save must not see it."""
    for node in ((document or {}).get("graph") or {}).get("nodes") or []:
        if isinstance(node, dict):
            node.pop("size", None)


async def node_schema(service_api, catalog, generation, args):
    kind = args.get("kind")
    if kind is None:
        return error("invalid_request", "service needs kind service")
    node = catalog["nodes"].get(kind)
    if node is None:
        return error("not_found", "Node kind not found")
    result = {"kind": kind, "node": node, "types": catalog["types"], "parametricTypes": catalog.get("parametricTypes", {}),
              "limits": catalog["limits"], "generation": generation}
    if node.get("dynamicOutputs") == "trigger":
        result["triggerOutputs"] = catalog.get("triggerOutputs", {})
    if "service" in args:
        if node.get("dynamicInputs") != "service":
            return error("invalid_request", "service applies only to kind service")
        services = await service_api.node_library({"operation": "services"}, is_admin=True)
        if not services.get("ok"):
            return services
        schema = services["result"]["items"].get(args["service"])
        result["service"] = service_ports(node, args["service"], schema) if schema is not None else {
            "id": args["service"], "known": False,
            "note": "Home Assistant has no such service: preview warns and its fields accept any type. Check the id with discover."}
    return {"version": 1, "ok": True, "result": result}


def service_target_domains(schema):
    """Entity domains the target accepts; empty when unrestricted (VisualNodeCatalog.ServiceTargetDomains)."""
    filters = [f for f in ((schema.get("target") or {}).get("entity") or []) if isinstance(f, dict)]
    domains = [[d for d in f.get("domain") or [] if isinstance(d, str) and d] for f in filters]
    if not domains or any(not d for d in domains):
        return []
    return list(dict.fromkeys(d for group in domains for d in group))


def service_ports(node, service, schema):
    """The Call service ports one known service gives (contract C5), mirroring VisualNodeCatalog.DataInputs."""
    domains = service_target_domains(schema)
    target = None if schema.get("target") is None else {
        **node["inputs"]["target"], "types": ["target", *("entity_id:" + d for d in domains)] if domains else ["target", "entity_id"]}
    fields = {}
    for key, field in sorted((schema.get("fields") or {}).items(), key=lambda item: item[1].get("order", 0)):
        port = {"types": field.get("types", []), "required": field.get("required") is True,
                "label": field.get("label") or key, "description": field.get("description") or "Service field " + key}
        port.update({name: field[name] for name in FIELD_CONSTRAINTS if field.get(name) is not None})
        fields["field:" + key] = port
    response = schema.get("response", "none")
    return {"id": service, "known": True, "name": schema.get("name"), "description": schema.get("description"),
            "target": target, "fields": fields, "response": response,
            "returnResponse": {"none": False, "only": True}.get(response, "either"),
            "outputs": {} if response == "none" else {"response": node["outputs"]["response"]},
            "note": "Put only the fields you set into config.fields; each key becomes input field:<key>. Required fields must be present. "
                    + ("Leave target unset: this service takes none." if target is None else "Write a literal target with one of the target types.")}


def trace_view(data, node, include_graph):
    """run_trace without the catalog and, unless asked, without values or the graph: full traces are large."""
    snapshot = data.get("snapshot")
    if snapshot is not None:
        snapshot = {key: snapshot.get(key) for key in ("revision", "visualId", "visualRevision", *(("graph",) if include_graph else ()))}
    result = {"run": data.get("run"), "live": data.get("live"), "snapshot": snapshot, "trace": None}
    trace = data.get("trace")
    if trace is not None:
        events = trace.get("events") or []
        view = {key: trace.get(key) for key in ("rootNodeId", "startedUtc", "complete", "truncated", "dropped", "summary")}
        if node is None:
            view["events"] = [{key: value for key, value in event.items() if key not in ("inputs", "outputs")} for event in events]
        else:
            view["node"] = node
            view["events"] = [event for event in events if event.get("node") == node]
        result["trace"] = view
    return result


async def discover(hass, service_api, kind, query, offset, limit, domain=None, service=None):
    if service is not None and kind != "services":
        return error("invalid_request", "service applies only to kind services")
    if domain is not None and kind not in ("entities", "services"):
        return error("invalid_request", "domain applies only to kinds entities and services")
    if kind == "entities":
        items = [{"id": s.entity_id, "state": s.state, "attributes": s.attributes} for s in hass.states.async_all()
                 if domain is None or s.domain == domain]
    elif kind == "services":
        # The normalised schemas the validator uses (contract C4), not Home Assistant's raw descriptions.
        response = await service_api.node_library({"operation": "services"}, is_admin=True)
        if not response.get("ok"):
            return response
        services = response["result"]["items"]
        if service is not None:
            entry = services.get(service)
            if entry is None:
                return error("not_found", "Service not found; discover services without service to list them")
            entry = {**entry, "fields": dict(sorted((entry.get("fields") or {}).items(), key=lambda item: item[1].get("order", 0)))}
            return {"version": 1, "ok": True, "result": {"kind": kind, "service": {"id": service, **entry}}}
        items = [{"id": key, "name": entry.get("name"), "description": entry.get("description"),
                  "hasTarget": entry.get("target") is not None, "response": entry.get("response")}
                 for key, entry in services.items() if domain is None or entry.get("domain") == domain]
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
    result = {"kind": kind, "query": query, "offset": offset, "limit": limit, "total": len(items), "items": items[offset:offset + limit]}
    if domain is not None:
        result["domain"] = domain
    return {"version": 1, "ok": True, "result": result}


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
