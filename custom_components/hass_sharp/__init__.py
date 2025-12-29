import os
import sys
from typing import Dict

import pythonnet as pynet
import json
from clr_loader import get_coreclr

from . import http, utils, websocket
from .const import logger, DOMAIN

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant, callback, Event, EventStateChangedData
from homeassistant.helpers.event import async_track_state_change_event
from homeassistant.helpers.typing import ConfigType
from homeassistant.components import frontend
from homeassistant.components.http import StaticPathConfig

os.environ['DOTNET_SYSTEM_GLOBALIZATION_INVARIANT'] = 'true'

ROOT_DIR = os.path.dirname(os.path.realpath(__file__))
DOTNET_ROOT_DIR = os.path.join(ROOT_DIR, "dotnet")

RUNTIME_CONFIG = os.path.join(ROOT_DIR, "dotnet.runtimeconfig.json")

SDK_VERSION = '10.0.101'
SHARED_VERSION = '10.0.1'
SDK_PATH = os.path.join(DOTNET_ROOT_DIR, 'sdk', SDK_VERSION)
SHARED_PATH = os.path.join(DOTNET_ROOT_DIR, 'shared', 'Microsoft.NETCore.App', SHARED_VERSION)

HASS_SHARP_DLL_PATH = os.path.join(ROOT_DIR, "out")
sys.path.append(HASS_SHARP_DLL_PATH)

rt = get_coreclr(
    dotnet_root=DOTNET_ROOT_DIR,
    runtime_config=RUNTIME_CONFIG,
    properties={
        "System.Globalization.Invariant": "true",
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
            hass.config.path("custom_components/hass_sharp/www")
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
        True,
    )

    frontend.add_extra_js_url(hass, "/hass-sharp-static/hass-sharp.js")
    hass_sharp = HassSharpManager(hass.config.path(''))
    utils.set_hass_sharp_manager(hass, hass_sharp)

    websocket.register_websocket_routes(hass, hass_sharp)
    http.register_http_routes(hass)

    return True

unsubs: Dict[str, callable] = {}

async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry):
    from HassSharp import PyInterop, HasEntityState
    from System import Action, String, Func, Object, Int32
    from System.Collections.Generic import Dictionary

    from . import csharp_converters

    logger.info('Async setup entry')


    hass_sharp = utils.get_hass_sharp_manager(hass)

    await hass.config_entries.async_forward_entry_setups(entry, ['sensor'])

    @callback
    async def on_entity_change(event: Event[EventStateChangedData]):
        entity_id = event.data.get("entity_id")
        new_state = csharp_converters.to_has_entity_state(event.data.get("new_state"))
        old_state = csharp_converters.to_has_entity_state(event.data.get("old_state"))

        await hass.async_add_executor_job(hass_sharp.OnTrackedEntityChange, entity_id, new_state, old_state)
        return True

    def entity(entity_id: str):
        entity_state = hass.states.get(entity_id)
        return csharp_converters.to_has_entity_state(entity_state)

    def call_service(domain: str, service: str, data_json: str):
        data = json.loads(data_json) if data_json else None
        # Use hass.add_job to safely schedule the service call from a background thread
        hass.add_job(hass.services.async_call(domain, service, data))

    def unsub_from_entity(entity_id: str):
        unsubs.pop(entity_id)()

    def unbsub_all():
        for unsub in unsubs.values():
            unsub()

    def subscribe_to_entity_change(entity_id: str):
        if entity_id in unsubs:
            return

        unsubs[entity_id] = async_track_state_change_event(hass, entity_id, on_entity_change)

    PyInterop.Log = Action[Int32, String](python_log)
    PyInterop.Entity = Func[String, HasEntityState](entity)
    PyInterop.CallService = Action[String, String, String](call_service)
    PyInterop.UnSubscribeFromEntityTracking = Action[String](unsub_from_entity)
    PyInterop.UnSubscribeAllFromEntityTracking = Action(unbsub_all)
    PyInterop.SubscribeToEntityTracking = Action[String](subscribe_to_entity_change)

    await hass.async_add_executor_job(hass_sharp.Init, utils.get_hass_entities(hass))

    return True

async def async_unload_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    hass_sharp = utils.get_hass_sharp_manager(hass)

    await hass.async_add_executor_job(hass_sharp.Unload)

    return True


async def async_reload_entry(hass: HomeAssistant, entry: ConfigEntry) -> None:
    """Reload config entry."""
    await hass.config_entries.async_reload(entry.entry_id)
