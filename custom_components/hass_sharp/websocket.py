import json
from typing import Any

from homeassistant.core import HomeAssistant
from homeassistant.components import websocket_api
import voluptuous as vol

from . import utils
from .const import logger
from .authoring import AuthoringService

RUN_STATUSES = ("Pending", "Running", "Succeeded", "Failed", "Cancelled", "Skipped", "Interrupted", "Stopped")
NOT_READY = "HassSharp is not running: wait for it to finish starting, or turn the integration on under Settings → Devices & services"
# The requests Manager.RunNow refuses, by their exact .NET messages (an InvalidOperationException or ArgumentException).
# Those exception types are also what a defect below the dispatch throws (JSON access, LINQ, EF Core), so the type alone
# says nothing: only these messages are refusals. If the C# side gives refusals their own exception type, this becomes a
# type check on it. tests/python/test_visual.py reads them from the C# source (RunNow, DispatchVisual and
# UserScriptManager.NotRunning), so a refusal added there is added here.
REFUSALS = frozenset({
    "Automation is not active", "Saved visual document not found", "Invalid visual class", "Trigger not found",
    "The saved visual revision is not active", "Trigger does not permit manual execution",
    "The automation is not active right now; try again in a moment",
})


def ready_state(hass):
    """The entry state that takes commands, or None. The commands are registered once; the automations exist only
    while an entry is set up and not unloading."""
    return next((state for state in hass.data.get("hass_sharp_llm", {}).values() if state.ready), None)


def loaded(hass):
    return ready_state(hass) is not None


def refusal(err):
    """The message of a request RunNow refuses, as the user may see it; None for anything else, which is a defect."""
    message = getattr(err, "Message", None)
    return message if isinstance(message, str) and message in REFUSALS else None


def admin_command(handler):
    """An administrator's command while the entry is ready, counted as in flight until it answers: unload waits for it."""
    async def run(hass, connection, msg):
        if not connection.user.is_admin:
            connection.send_error(msg["id"], "unauthorized", "Administrator required")
            return
        if (state := ready_state(hass)) is None:
            connection.send_error(msg["id"], "not_ready", NOT_READY)
            return
        with state.command():
            await handler(hass, connection, msg)
    return run


def register_websocket_routes(hass_outer: HomeAssistant, hass_sharp):
    authoring = AuthoringService(hass_outer, hass_sharp)
    @websocket_api.decorators.async_response
    @admin_command
    async def websocket_visual(hass, connection, msg):
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
    @admin_command
    async def websocket_node_library(hass, connection, msg):
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
    @admin_command
    async def websocket_run_now(hass, connection, msg):
        try:
            result = await authoring.run_now(msg["automationId"], msg["className"], msg["method"], is_admin=connection.user.is_admin)
            connection.send_result(msg["id"], result)
        except Exception as err:
            if (message := refusal(err)) is not None:
                connection.send_error(msg["id"], "invalid_request", message)
                return
            logger.exception("Manual automation dispatch failed")
            connection.send_error(msg["id"], "run_failed", "Could not dispatch automation; check integration logs")

    @websocket_api.decorators.async_response
    @admin_command
    async def websocket_run_history(hass, connection, msg):
        try:
            result = await authoring.history(msg["automationId"], msg["offset"], msg["limit"], msg.get("status"), is_admin=connection.user.is_admin)
            connection.send_result(msg["id"], result)
        except Exception:
            logger.exception("Run history read failed")
            connection.send_error(msg["id"], "history_failed", "Could not read run history")

    @websocket_api.decorators.async_response
    @admin_command
    async def websocket_run_trace(hass, connection, msg):
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
