from logging import INFO
from re import I
from typing import Dict, List
from .const import DOMAIN, logger
import os
import sys
import pythonnet as pynet
import asyncio
import json
from clr_loader import get_coreclr

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant, State, callback, Event, EventStateChangedData
from homeassistant.helpers.event import async_track_state_change_event
from homeassistant.helpers.typing import ConfigType
from homeassistant.components import frontend, websocket_api
import homeassistant.components.websocket_api
from homeassistant.components.http import StaticPathConfig
import voluptuous as vol


os.environ['DOTNET_SYSTEM_GLOBALIZATION_INVARIANT'] = 'true'

# --- Path definitions (as before) ---
SCRIPT_DIR = os.path.dirname(os.path.realpath(__file__))
DOTNET_ROOT_DIR = os.path.join(SCRIPT_DIR, "dotnet")

RUNTIME_CONFIG = os.path.join(SCRIPT_DIR, "dotnet.runtimeconfig.json")

# --- 1. SET CORECLR_RUNTIME_CONFIG (The environment approach) ---
SDK_VERSION = '10.0.101'
SHARED_VERSION = '10.0.1'
SDK_PATH = os.path.join(DOTNET_ROOT_DIR, 'sdk', SDK_VERSION)
SHARED_PATH = os.path.join(DOTNET_ROOT_DIR, 'shared', 'Microsoft.NETCore.App', SHARED_VERSION)

HASS_SHARP_DLL_PATH = os.path.join(SCRIPT_DIR, "out")

USER_SCRIPTS_DIR = os.path.join(SCRIPT_DIR, "user_scripts")


sys.path.append(HASS_SHARP_DLL_PATH)

rt = get_coreclr(
    dotnet_root=DOTNET_ROOT_DIR, 
    runtime_config=RUNTIME_CONFIG, 
    properties={ 
        "System.Globalization.Invariant":"true", 
    }
)
pynet.set_runtime(rt)


def python_log(level: int, message: str):
    logger.log(level, "[C#] %s", message)

async def async_setup(hass: HomeAssistant, config: ConfigType):
    await hass.http.async_register_static_paths([
       StaticPathConfig(
          "/hass-sharp-static",
          hass.config.path("custom_components/hass_sharp/www/dist")
        )
    ])
    frontend.async_register_built_in_panel(
      hass,
      "hass-sharp-view",
      "HassSharp",
      "mdi:language-csharp",
      True,
      "hass-sharp",
      {},
      False,
    )

    frontend.add_extra_js_url(hass, "/hass-sharp-static/hass-sharp.js")

    @websocket_api.decorators.async_response
    async def websocket_get_completions(hass: HomeAssistant, connection: websocket_api.connection.ActiveConnection, msg):
        import clr
        clr.AddReference("HassSharp")
        from HassSharp import CodeCompiler
        
        completions = await hass.async_add_executor_job(CodeCompiler.GetCompletions, msg["source"], msg["position"])
        connection.send_result(msg["id"], json.loads(completions))

    @websocket_api.decorators.async_response
    async def websocket_get_diagnostics(hass, connection, msg):
        import clr
        clr.AddReference("HassSharp")
        from HassSharp import CodeCompiler
        
        diagnostics = await hass.async_add_executor_job(CodeCompiler.GetDiagnostics, msg["source"])
        # Convert C# objects to dictionaries for JSON serialization
        results = []
        for d in diagnostics:
            results.append({
                "startLine": d.StartLine,
                "startColumn": d.StartColumn,
                "endLine": d.EndLine,
                "endColumn": d.EndColumn,
                "message": d.Message,
                "severity": d.Severity
            })
        connection.send_result(msg["id"], results)

    websocket_api.async_register_command(
        hass, 
        "hass_sharp/get_completions",
        websocket_get_completions,
        vol.Schema({
            vol.Required("id"): vol.Coerce(int),
            vol.Required("type"): "hass_sharp/get_completions",
            vol.Required("source"): str,
            vol.Required("position"): int,
        })
    )
    websocket_api.async_register_command(
        hass, 
        "hass_sharp/get_diagnostics",
        websocket_get_diagnostics,
        vol.Schema({
            vol.Required("id"): vol.Coerce(int),
            vol.Required("type"): "hass_sharp/get_diagnostics",
            vol.Required("source"): str,
        })
    )
    return True

async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry):
    import clr
    clr.AddReference("HassSharp")
    from HassSharp import CodeCompiler, PyInterop, HasEntityState
    from System import Action, String, Func, Object, Int32
    from System.Collections.Generic import Dictionary

    def entity(entityId: str) :
      entityState = hass.states.get(entityId)
      if entityState is None: return

      attributesDict = Dictionary[String, Object]()

      for k,v in entityState.attributes.items():
        attributesDict[k] = v

      ref = HasEntityState()
      ref.EntityId = entityState.entity_id
      ref.Domain = entityState.domain
      ref.ObjectId = entityState.object_id
      ref.State = entityState.state
      ref.Attributes = attributesDict
      ref.LastChanged = entityState.last_changed_timestamp
      ref.LastReported = entityState.last_reported_timestamp

      return ref

    def call_service(domain: str, service: str, data_json: str):
      data = json.loads(data_json) if data_json else None
      # Use hass.add_job to safely schedule the service call from a background thread
      hass.add_job(hass.services.async_call(domain, service, data))

    PyInterop.Log = Action[Int32, String](python_log)
    PyInterop.Entity = Func[String, HasEntityState](entity)
    PyInterop.CallService = Action[String, String, String](call_service)

    runner = await hass.async_add_executor_job(CodeCompiler.CompileFromFolder, USER_SCRIPTS_DIR)
    await hass.async_add_executor_job(runner.RunAll)

    dependencies = runner.DependencyTracking

    unsubs = []
    hass.data.setdefault(DOMAIN, {})
    hass.data[DOMAIN][entry.entry_id] = {
        "unsubs": unsubs,
        "runner": runner,
    }

    @callback
    def on_entity_change(event: Event[EventStateChangedData]):
      entity_id = event.data.get("entity_id")
      if entity_id in dependencies:
        for func in dependencies[entity_id]:
            hass.async_add_executor_job(runner.RunMethod, func)

    for entityId in dependencies.Keys:
      unsub = async_track_state_change_event(hass, entityId, on_entity_change)
      unsubs.append(unsub)


    return True

async def async_unload_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Unload a config entry."""
    entry_data = hass.data[DOMAIN].pop(entry.entry_id)
    
    # Clean up event listeners
    for unsub in entry_data["unsubs"]:
        unsub()
    
    return True

async def async_reload_entry(hass: HomeAssistant, entry: ConfigEntry) -> None:
    """Reload config entry."""
    await hass.config_entries.async_reload(entry.entry_id)
