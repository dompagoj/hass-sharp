"""Shared administrator authoring services; managed gates/store remain authoritative."""
import json

from . import dotnet_tasks, native_triggers


def error(code, detail):
    return {"version": 1, "ok": False, "error": {"code": code, "detail": detail}}


def runnable_triggers(document):
    return [{"triggerId": node["id"], "method": "Run_" + node["id"].encode().hex().upper(),
             "manual": node.get("config", {}).get("manual", True),
             "runnable": bool(document.get("currentRevisionActive") and node.get("config", {}).get("manual", True))}
            for node in document.get("graph", {}).get("nodes", []) if node.get("kind") == "trigger"]


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
            nodes = graph.get("nodes")
            # deriveSignatures: the managed side writes each Custom C# node's pins from its code first.
            derive = request.get("deriveSignatures") is True
            preview = await self._request(self.manager.VisualRequest, {"operation": "preview", "graph": graph, **({"deriveSignatures": True} if derive else {})}, is_admin=True)
            if not preview.get("ok"):
                return preview
            if isinstance(nodes, list) and all(isinstance(n, dict) and isinstance(n.get("config", {}), dict) for n in nodes):
                diagnostics = await native_triggers.async_validate_graph_triggers(self.hass, graph)
                preview["result"]["diagnostics"].extend({**d, "severity": 3 if d.get("severity") == "error" else d.get("severity")} for d in diagnostics)
            preview["result"]["valid"] = not any(d.get("severity") in (3, "error") for d in preview["result"]["diagnostics"])
            if operation == "preview":
                return preview
            if not preview["result"]["valid"]:
                return error("validation", preview["result"])
        result = await self._request(self.manager.VisualRequest, request, is_admin=True)
        if result.get("ok"):
            data = result["result"]
            documents = data if operation == "list" else [data] if operation == "read" else [data["document"]] if operation == "save" else []
            # The automations page's list (view "home") sends outlines, not graphs: the page works its triggers out itself.
            for document in documents:
                if "graph" in document:
                    document["triggers"] = runnable_triggers(document)
        return result

    async def node_library(self, request, *, is_admin):
        return await self._request(self.manager.NodeLibraryRequest, request, is_admin=is_admin)

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
