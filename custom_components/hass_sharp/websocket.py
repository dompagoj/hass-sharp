from typing import Any

from homeassistant.core import HomeAssistant
from homeassistant.components import websocket_api
import voluptuous as vol

from . import utils

def register_websocket_routes(hass_outer: HomeAssistant, hass_sharp):
    @websocket_api.decorators.async_response
    async def websocket_reload_entities(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):

          await hass.async_add_executor_job(hass_sharp.GenerateHassEntities, utils.get_hass_entities(hass))
          connection.send_result(msg["id"], {"success": True})

    @websocket_api.decorators.async_response
    async def websocket_get_hover(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):
          hover = await hass.async_add_executor_job(hass_sharp.GetHoverDiagnostics, msg["source"], msg["position"])
          connection.send_result(msg["id"], hover)


    @websocket_api.decorators.async_response
    async def websocket_get_diagnostics(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):
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

    @websocket_api.decorators.async_response
    async def websocket_get_completions(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):
      completions = await hass.async_add_executor_job(hass_sharp.GetCodeCompletions, msg["source"], msg["position"])

      completions_py = [{
              "displayText": c.DisplayText,
              "displayTextPrefix": c.DisplayTextPrefix,
              "displayTextSuffix": c.DisplayTextSuffix,
              "filterText": c.FilterText,
              "tags": [t for t in c.Tags],
            } for c in completions]

      connection.send_result(msg["id"], completions_py)

    # @websocket_api.decorators.async_response
    # async def websocket_save_script(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):
    #     hassSharp = utils.get_hass_sharp_manager(hass)
    #     path = msg["path"]
    #     source = msg["source"]
    #
    #     # Save to disk
    #     def save_file():
    #         with open(path, "w") as f:
    #             f.write(source)
    #
    #     await hass.async_add_executor_job(save_file)
    #     await hass.async_add_executor_job(hassSharp.SaveScript, path, source)
    #
    #     connection.send_result(msg["id"], {"success": True})
    #
    # websocket_api.async_register_command(
    #     hass,
    #     "hass_sharp/save_script",
    #     websocket_save_script,
    #     vol.Schema({
    #         vol.Required("id"): vol.Coerce(int),
    #         vol.Required("type"): "hass_sharp/save_script",
    #         vol.Required("path"): str,
    #         vol.Required("source"): str,
    #     }, extra=vol.ALLOW_EXTRA)
    # )

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
