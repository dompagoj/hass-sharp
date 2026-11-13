import json
from typing import Any

from homeassistant.core import HomeAssistant
from homeassistant.components import websocket_api
import voluptuous as vol

from . import dotnet_tasks, utils
from .const import logger
from .authoring import AuthoringService

RUN_STATUSES = ("Pending", "Running", "Succeeded", "Failed", "Cancelled", "Skipped", "Interrupted", "Stopped")


def register_websocket_routes(hass_outer: HomeAssistant, hass_sharp):
    authoring = AuthoringService(hass_outer, hass_sharp)
    @websocket_api.decorators.async_response
    async def websocket_visual(hass, connection, msg):
        if not connection.user.is_admin:
            connection.send_error(msg["id"], "unauthorized", "Administrator required")
            return
        try:
            result = await authoring.visual(msg["request"], is_admin=connection.user.is_admin)
            connection.send_result(msg["id"], result)
        except Exception:
            logger.exception("Visual request failed")
            connection.send_error(msg["id"], "visual_failed", "Visual request failed; check integration logs")

    websocket_api.async_register_command(
        hass_outer, "hass_sharp/visual", websocket_visual,
        vol.Schema({vol.Required("id"): int, vol.Required("type"): "hass_sharp/visual",
                    vol.Required("request"): dict})
    )

    @websocket_api.decorators.async_response
    async def websocket_node_library(hass, connection, msg):
        if not connection.user.is_admin:
            connection.send_error(msg["id"], "unauthorized", "Administrator required")
            return
        try:
            result = await authoring.node_library(msg["request"], is_admin=connection.user.is_admin)
            connection.send_result(msg["id"], result)
        except Exception:
            logger.exception("Node library request failed")
            connection.send_error(msg["id"], "library_failed", "Node library request failed; check integration logs")

    websocket_api.async_register_command(
        hass_outer, "hass_sharp/node_library", websocket_node_library,
        vol.Schema({vol.Required("id"): int, vol.Required("type"): "hass_sharp/node_library",
                    vol.Required("request"): dict})
    )

    @websocket_api.decorators.async_response
    async def websocket_run_now(hass, connection, msg):
        if not connection.user.is_admin:
            connection.send_error(msg["id"], "unauthorized", "Administrator required")
            return
        try:
            result = await authoring.run_now(msg["automationId"], msg["className"], msg["method"], is_admin=connection.user.is_admin)
            connection.send_result(msg["id"], result)
        except Exception:
            logger.exception("Manual automation dispatch failed")
            connection.send_error(msg["id"], "run_failed", "Could not dispatch automation; check integration logs")

    @websocket_api.decorators.async_response
    async def websocket_run_history(hass, connection, msg):
        if not connection.user.is_admin:
            connection.send_error(msg["id"], "unauthorized", "Administrator required")
            return
        try:
            result = await authoring.history(msg["automationId"], msg["offset"], msg["limit"], msg.get("status"), is_admin=connection.user.is_admin)
            connection.send_result(msg["id"], result)
        except Exception:
            logger.exception("Run history read failed")
            connection.send_error(msg["id"], "history_failed", "Could not read run history")

    @websocket_api.decorators.async_response
    async def websocket_run_trace(hass, connection, msg):
        if not connection.user.is_admin:
            connection.send_error(msg["id"], "unauthorized", "Administrator required")
            return
        try:
            result = await authoring.trace(msg["runId"], is_admin=connection.user.is_admin)
        except Exception:
            logger.exception("Run trace read failed")
            connection.send_error(msg["id"], "trace_failed", "Could not read the run trace")
            return
        if result is None:
            connection.send_error(msg["id"], "not_found", "Run not found")
        else:
            connection.send_result(msg["id"], result)

    websocket_api.async_register_command(
        hass_outer, "hass_sharp/run_trace", websocket_run_trace,
        vol.Schema({vol.Required("id"): int, vol.Required("type"): "hass_sharp/run_trace",
                    vol.Required("runId"): str})
    )

    for command, handler, fields in (
        ("run_now", websocket_run_now, {
            vol.Required("className"): str, vol.Required("method"): str,
        }),
        ("run_history", websocket_run_history, {
            vol.Optional("offset", default=0): vol.All(int, vol.Range(min=0)),
            vol.Optional("limit", default=50): vol.All(int, vol.Range(min=1, max=100)),
            vol.Optional("status"): [vol.In(RUN_STATUSES)],
        }),
    ):
        websocket_api.async_register_command(
            hass_outer, f"hass_sharp/{command}", handler,
            vol.Schema({
                vol.Required("id"): int, vol.Required("type"): f"hass_sharp/{command}",
                vol.Required("automationId"): str, **fields,
            })
        )

    @websocket_api.decorators.async_response
    async def websocket_get_entities(hass, connection, msg):
        if not connection.user.is_admin:
            connection.send_error(msg["id"], "unauthorized", "Administrator required")
            return
        try:
            connection.send_result(msg["id"], utils.get_hass_entity_registry(hass))
        except Exception:
            logger.exception("Entity discovery failed")
            connection.send_error(msg["id"], "entities_failed", "Could not load entities; try again")

    websocket_api.async_register_command(
        hass_outer, "hass_sharp/get_entities", websocket_get_entities,
        vol.Schema({vol.Required("id"): int, vol.Required("type"): "hass_sharp/get_entities"})
    )

    @websocket_api.decorators.async_response
    async def websocket_reload_entities(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):
        if not connection.user.is_admin:
            connection.send_error(msg["id"], "unauthorized", "Administrator required")
            return
        try:
            await hass.async_add_executor_job(hass_sharp.GenerateHassEntities, utils.get_hass_entities(hass))
            connection.send_result(msg["id"], {"success": True})
        except Exception:
            logger.exception("Editor request failed")
            connection.send_error(msg["id"], "editor_failed", "Editor request failed; check integration logs")

    @websocket_api.decorators.async_response
    async def websocket_get_hover(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):
        if not connection.user.is_admin:
            connection.send_error(msg["id"], "unauthorized", "Administrator required")
            return
        try:
            hover = await hass.async_add_executor_job(hass_sharp.GetHoverDiagnostics, msg["source"], msg["position"])
            connection.send_result(msg["id"], hover)
        except Exception:
            logger.exception("Editor request failed")
            connection.send_error(msg["id"], "editor_failed", "Editor request failed; check integration logs")


    @websocket_api.decorators.async_response
    async def websocket_get_diagnostics(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):
        if not connection.user.is_admin:
            connection.send_error(msg["id"], "unauthorized", "Administrator required")
            return
        try:
            diagnostics = await hass.async_add_executor_job(hass_sharp.GetCompilationDiagnostics, msg["source"])
            diagnostics_py = [{
                      "startLine": d.StartLine,
                      "startColumn": d.StartColumn,
                      "endLine": d.EndLine,
                      "endColumn": d.EndColumn,
                      "message": d.Message,
                      "severity": d.Severity,
                  } for d in diagnostics]

            connection.send_result(msg["id"], diagnostics_py)
        except Exception:
            logger.exception("Editor request failed")
            connection.send_error(msg["id"], "editor_failed", "Editor request failed; check integration logs")

    @websocket_api.decorators.async_response
    async def websocket_get_completions(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):
        if not connection.user.is_admin:
            connection.send_error(msg["id"], "unauthorized", "Administrator required")
            return
        try:
            completions = await dotnet_tasks.async_run_dotnet_task(
                hass, hass_sharp.GetCodeCompletions, msg["source"], msg["position"]
            )

            completions_py = [{
                    "displayText": c.DisplayText,
                    "displayTextPrefix": c.DisplayTextPrefix,
                    "displayTextSuffix": c.DisplayTextSuffix,
                    "filterText": c.FilterText,
                    "tags": [t for t in c.Tags],
                    "textEdit": {"start": c.TextEdit.Start, "length": c.TextEdit.Length, "newText": c.TextEdit.NewText},
                    "additionalTextEdits": [
                        {"start": e.Start, "length": e.Length, "newText": e.NewText}
                        for e in c.AdditionalTextEdits
                    ],
                  } for c in completions]

            connection.send_result(msg["id"], completions_py)
        except Exception:
            logger.exception("Editor request failed")
            connection.send_error(msg["id"], "editor_failed", "Editor request failed; check integration logs")

    @websocket_api.decorators.async_response
    async def websocket_format_source(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):
        if not connection.user.is_admin:
            connection.send_error(msg["id"], "unauthorized", "Administrator required")
            return
        try:
            source = msg["source"]

            formatted = await dotnet_tasks.async_run_dotnet_task(
                hass, hass_sharp.FormatCode, source
            )
            connection.send_result(msg["id"], formatted)
        except Exception:
            logger.exception("Editor request failed")
            connection.send_error(msg["id"], "editor_failed", "Editor request failed; check integration logs")

    websocket_api.async_register_command(
          hass_outer,
          "hass_sharp/get_completions",
          websocket_get_completions,
          vol.Schema({
              vol.Required("id"): vol.Coerce(int),
              vol.Required("type"): "hass_sharp/get_completions",
              vol.Required("source"): str,
              vol.Required("position"): int,
          }, extra=vol.ALLOW_EXTRA)
    )

    websocket_api.async_register_command(
          hass_outer,
          "hass_sharp/get_hover",
          websocket_get_hover,
          vol.Schema({
              vol.Required("id"): vol.Coerce(int),
              vol.Required("type"): "hass_sharp/get_hover",
              vol.Required("source"): str,
              vol.Required("position"): int,
          }, extra=vol.ALLOW_EXTRA)
    )

    websocket_api.async_register_command(
          hass_outer,
          "hass_sharp/get_diagnostics",
          websocket_get_diagnostics,
          vol.Schema({
              vol.Required("id"): vol.Coerce(int),
              vol.Required("type"): "hass_sharp/get_diagnostics",
              vol.Required("source"): str,
          }, extra=vol.ALLOW_EXTRA)
    )

    websocket_api.async_register_command(
          hass_outer,
          "hass_sharp/reload_entities",
          websocket_reload_entities,
          vol.Schema({
              vol.Required("id"): vol.Coerce(int),
              vol.Required("type"): "hass_sharp/reload_entities",
          }, extra=vol.ALLOW_EXTRA)
    )

    websocket_api.async_register_command(
        hass_outer,
        "hass_sharp/format_source",
        websocket_format_source,
        vol.Schema({
            vol.Required("id"): vol.Coerce(int),
            vol.Required("type"): "hass_sharp/format_source",
            vol.Required("source"): str,
        }, extra=vol.ALLOW_EXTRA)
    )
