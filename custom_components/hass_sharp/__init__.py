import os
import sys
from typing import override
from homeassistant.components.sensor import SensorDeviceClass, SensorEntity
from homeassistant.helpers.entity_platform import AddEntitiesCallback
import pythonnet as pynet
import json
from clr_loader import get_coreclr

from .import http, utils, websocket
from .const import logger, DOMAIN

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import  HomeAssistant, callback, Event, EventStateChangedData
from homeassistant.helpers.event import async_track_state_change_event
from homeassistant.helpers.typing import ConfigType
from homeassistant.components import frontend
from homeassistant.components.http import StaticPathConfig


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
    import clr
    clr.AddReference("HassSharp")
    from HassSharp import HassSharpManager

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
    hassSharp = HassSharpManager(hass.config.path(''))
    utils.set_hass_sharp_manager(hass, hassSharp)

    websocket.register_websocket_routes(hass, hassSharp)
    http.register_http_routes(hass)
   
    return True

async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry):
    from HassSharp import PyInterop, HasEntityState
    from System import Action, String, Func, Object, Int32
    from System.Collections.Generic import Dictionary

    logger.info('Async setup entry')
    
    hass_sharp = utils.get_hass_sharp_manager(hass)

    await hass.config_entries.async_forward_entry_setups(entry, ['sensor'])

    def entity(entity_id: str) :
        entity_state = hass.states.get(entity_id)
        if entity_state is None: return

        attributes_dict = Dictionary[String, Object]()

        for k,v in entity_state.attributes.items():
          attributes_dict[k] = v

        ref = HasEntityState()
        ref.EntityId = entity_state.entity_id
        ref.Domain = entity_state.domain
        ref.ObjectId = entity_state.object_id
        ref.State = entity_state.state
        ref.Attributes = attributes_dict
        ref.LastChanged = entity_state.last_changed_timestamp
        ref.LastReported = entity_state.last_reported_timestamp

        return ref

    def call_service(domain: str, service: str, data_json: str):
        data = json.loads(data_json) if data_json else None
        # Use hass.add_job to safely schedule the service call from a background thread
        hass.add_job(hass.services.async_call(domain, service, data))

    PyInterop.Log = Action[Int32, String](python_log)
    PyInterop.Entity = Func[String, HasEntityState](entity)
    PyInterop.CallService = Action[String, String, String](call_service)

    await hass.async_add_executor_job(hass_sharp.Init, utils.get_hass_entities(hass))

    dependencies = hass_sharp.GetScriptEntityDependencies()

    unsubs = []
    hass.data.setdefault(DOMAIN, {})
    hass.data[DOMAIN][entry.entry_id] = {
        "unsubs": unsubs,
    }

    @callback
    async def on_entity_change(event: Event[EventStateChangedData]):
      found_entity_id = event.data.get("entity_id")
      dep_entries = dependencies[found_entity_id]

      if dep_entries is not None:
        await hass.async_add_executor_job(hass_sharp.RunEntries, dep_entries)

    for entity_id in dependencies.Keys:
      unsub = async_track_state_change_event(hass, entity_id, on_entity_change)
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
