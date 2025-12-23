from typing import Any

from homeassistant.core import HomeAssistant
from homeassistant.components import websocket_api
import voluptuous as vol

from . import utils

def register_websocket_routes(hass: HomeAssistant, hassSharp):

  @websocket_api.decorators.async_response
  async def websocket_reload_entities(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):

        await hass.async_add_executor_job(hassSharp.GenerateHassEntities, utils.get_hass_entities(hass))
        connection.send_result(msg["id"], {"success": True})

  @websocket_api.decorators.async_response
  async def websocket_get_hover(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):
        hassSharp = utils.get_hass_sharp_manager(hass)
        hover = await hass.async_add_executor_job(hassSharp.GetHoverDiagnostics, msg["source"], msg["position"])
        connection.send_result(msg["id"], hover)


  @websocket_api.decorators.async_response
  async def websocket_get_diagnostics(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):
      diagnostics = await hass.async_add_executor_job(hassSharp.GetCompilationDiagnostics, msg["source"])
      diagnosticsPy = [{
                "startLine": d.StartLine,
                "startColumn": d.StartColumn,
                "endLine": d.EndLine,
                "endColumn": d.EndColumn,
                "message": d.Message,
                "severity": d.Severity,
            } for d in diagnostics]

      connection.send_result(msg["id"], diagnosticsPy)

  @websocket_api.decorators.async_response
  async def websocket_get_completions(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):
    completions = await hass.async_add_executor_job(hassSharp.GetCodeCompletions, msg["source"], msg["position"])

    completionsPy = [{
             "displayText": c.DisplayText,
             "displayTextPrefix": c.DisplayTextPrefix,
             "displayTextSuffix": c.DisplayTextSuffix,
             "filterText": c.FilterText,
             "tags": [t for t in c.Tags],
          } for c in completions]

    connection.send_result(msg["id"], completionsPy)

  websocket_api.async_register_command(
        hass, 
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
        hass, 
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
        hass, 
        "hass_sharp/get_diagnostics",
        websocket_get_diagnostics,
        vol.Schema({
            vol.Required("id"): vol.Coerce(int),
            vol.Required("type"): "hass_sharp/get_diagnostics",
            vol.Required("source"): str,
        }, extra=vol.ALLOW_EXTRA)
   )

  websocket_api.async_register_command(
        hass, 
        "hass_sharp/reload_entities",
        websocket_reload_entities,
        vol.Schema({
            vol.Required("id"): vol.Coerce(int),
            vol.Required("type"): "hass_sharp/reload_entities",
        }, extra=vol.ALLOW_EXTRA)
   )
