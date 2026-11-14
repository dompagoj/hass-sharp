import base64
import json
import os
import sys

from awesomeversion import AwesomeVersion
from homeassistant.const import EVENT_HOMEASSISTANT_STOP, __version__ as HA_VERSION

import pythonnet as pynet
from clr_loader import get_coreclr
from homeassistant.components import frontend
from homeassistant.components.http import StaticPathConfig
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import Event, EventStateChangedData, HomeAssistant, callback
from homeassistant.helpers.event import async_track_state_change_event
from homeassistant.helpers.typing import ConfigType

from . import scheduling, dotnet_downloader, dotnet_tasks, memory_report, native_triggers, services, utils, websocket
from .const import logger

os.environ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "true"

ROOT_DIR = os.path.dirname(os.path.realpath(__file__))
DOTNET_ROOT_DIR = os.path.join(ROOT_DIR, "dotnet")

RUNTIME_CONFIG = os.path.join(ROOT_DIR, "dotnet.runtimeconfig.json")

SDK_VERSION = dotnet_downloader.DOTNET_VERSION

HASS_SHARP_DLL_PATH = os.path.join(ROOT_DIR, "out")
if HASS_SHARP_DLL_PATH not in sys.path:
    sys.path.append(HASS_SHARP_DLL_PATH)

_runtime_initialized = False


def initialize_dotnet_runtime():
    global _runtime_initialized
    if _runtime_initialized:
        return True

    if not dotnet_downloader.install_dotnet(DOTNET_ROOT_DIR):
        logger.error("Failed to install .NET SDK")
        return False

    try:
        rt = get_coreclr(
            dotnet_root=DOTNET_ROOT_DIR,
            runtime_config=RUNTIME_CONFIG,
            properties={
                "System.Globalization.Invariant": "true",
            },
        )
        pynet.set_runtime(rt)
        _runtime_initialized = True
        return True
    except Exception as e:
        logger.error("Failed to initialize .NET runtime: %s", e)
        return False


def python_log(level: int, message: str):
    logger.log(level, "[C#] %s", message)


def serialize_service_response(value):
    if isinstance(value, (bytes, bytearray, memoryview)):
        return base64.b64encode(value).decode()
    if hasattr(value, "isoformat"):
        return value.isoformat()
    if hasattr(value, "value"):
        return value.value
    raise TypeError(f"Cannot serialize {type(value).__name__}")


async def async_setup(hass: HomeAssistant, config: ConfigType):
    if AwesomeVersion(HA_VERSION) < AwesomeVersion("2026.11.0"):
        logger.error("HassSharp requires Home Assistant 2026.11 or newer")
        return False
    if not await hass.async_add_executor_job(initialize_dotnet_runtime):
        return False

    import clr

    clr.AddReference("HassSharp")
    from HassSharp import HassSharpManager

    await hass.http.async_register_static_paths(
        [
            StaticPathConfig(
                "/hass-sharp-static",
                hass.config.path("custom_components/hass_sharp/www"),
            )
        ]
    )

    # HA and browsers cache this fixed URL across local frontend builds.
    frontend_asset = os.path.join(ROOT_DIR, "www", "hass-sharp.js")
    frontend.add_extra_js_url(
        hass, f"/hass-sharp-static/hass-sharp.js?v={os.stat(frontend_asset).st_mtime_ns}"
    )
    hass_sharp = await hass.async_add_executor_job(HassSharpManager, hass.config.path(""))
    utils.set_hass_sharp_manager(hass, hass_sharp)

    websocket.register_websocket_routes(hass, hass_sharp)

    return True


def register_panel(hass):
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

unsubs: dict[str, callable] = {}


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry):
    from System import Action, Func, Int32, String

    from HassSharp import HasEntityState, PyInterop

    from . import csharp_converters, llm
    from .authoring import AuthoringService

    logger.info("Async setup entry")

    hass_sharp = utils.get_hass_sharp_manager(hass)

    @callback
    async def on_entity_change(event: Event[EventStateChangedData]):
        entity_id = event.data.get("entity_id")
        new_state = csharp_converters.to_has_entity_state(event.data.get("new_state"))
        old_state = csharp_converters.to_has_entity_state(event.data.get("old_state"))

        await dotnet_tasks.async_run_dotnet_task(
            hass,
            hass_sharp.OnTrackedEntityChange,
            entity_id,
            new_state,
            old_state,
        )
        return True

    def entity(entity_id: str):
        entity_state = hass.states.get(entity_id)
        return csharp_converters.to_has_entity_state(entity_state)

    # TODO: See if this can be awaited on the c# side by using blocking=True in .async_call
    def call_service(
        domain: str,
        service: str,
        data_json: str,
        callback_on_done: Action | None,
        callback_on_error: Action | None,
    ):
        data = json.loads(data_json) if data_json else None

        async def call_and_callback():
            try:
                _ = await hass.services.async_call(domain, service, data, blocking=callback_on_done is not None)
                if callback_on_done:
                    callback_on_done()
            except Exception as error:
                if callback_on_error:
                    callback_on_error(str(error))

        hass.add_job(call_and_callback())

    def call_service_with_response(
        domain: str,
        service: str,
        data_json: str,
        callback_on_done: Action,
        callback_on_error: Action,
    ):
        data = json.loads(data_json) if data_json else None

        async def call_and_callback():
            try:
                response = await hass.services.async_call(
                    domain,
                    service,
                    data,
                    blocking=True,
                    return_response=True,
                )
                response_json = json.dumps(
                    response,
                    default=serialize_service_response,
                )
                await hass.async_add_executor_job(callback_on_done, response_json)
            except Exception as error:
                await hass.async_add_executor_job(callback_on_error, str(error))

        hass.add_job(call_and_callback())

    def unsub_from_entity(entity_id: str):
        unsub = unsubs.pop(entity_id, None)
        if unsub is not None:
            unsub()

    def unbsub_all():
        callbacks = list(unsubs.values())
        unsubs.clear()
        for unsub in callbacks:
            unsub()

    def subscribe_to_entity_change(entity_id: str):
        if entity_id in unsubs:
            return

        unsubs[entity_id] = async_track_state_change_event(hass, entity_id, on_entity_change)

    def register_schedule(rule_json, dispatch):
        def fire(planned):
            hass.async_create_task(dotnet_tasks.async_run_dotnet_task(hass, dispatch, planned))
        return Action(scheduling.register_ha(hass, rule_json, fire))

    from System.Threading.Tasks import Task
    native_bridge = native_triggers.NativeTriggerBridge(hass)
    hass.data.setdefault("hass_sharp_native_triggers", {})[entry.entry_id] = native_bridge

    state = llm.EntryState(AuthoringService(hass, hass_sharp))
    hass.data.setdefault("hass_sharp_llm", {})[entry.entry_id] = state
    try:
        def register_native(generation, configuration_json, dispatch, success, error):
            async def fire(payload):
                await dotnet_tasks.async_run_dotnet_task(hass, dispatch, payload)
            native_bridge.begin_register(generation, configuration_json, fire, success, error)

        PyInterop.BeginRegisterHaTriggers = Action[String, String, Func[String, Task], Action[String], Action[String]](register_native)
        PyInterop.BeginRemoveHaTriggers = Action[String, Action, Action[String]](native_bridge.begin_remove)
        PyInterop.RegisterSchedule = Func[String, Func[String, Task], Action](register_schedule)
        PyInterop.Log = Action[Int32, String](python_log)
        PyInterop.LogLevel = logger.level
        PyInterop.Entity = Func[String, HasEntityState](entity)
        PyInterop.CallService = Action[String, String, String, Action, Action[String]](call_service)
        PyInterop.CallServiceWithResponse = Action[String, String, String, Action[String], Action[String]](
            call_service_with_response
        )
        PyInterop.UnSubscribeFromEntityTracking = Action[String](unsub_from_entity)
        PyInterop.UnSubscribeAllFromEntityTracking = Action(unbsub_all)
        PyInterop.SubscribeToEntityTracking = Action[String](subscribe_to_entity_change)

        await hass.config_entries.async_forward_entry_setups(entry, ["sensor"])
        hass.data.setdefault("hass_sharp_services", {})[entry.entry_id] = await services.async_start(hass, hass_sharp)

        async def on_stop(event):
            # Home Assistant does not unload config entries when it stops: cancel the runs here, so they
            # record why. The entry stays loaded; an unload after this still cleans it up.
            try:
                await hass.async_add_executor_job(hass_sharp.CancelRunsForShutdown)
            except Exception:
                logger.exception("Cannot cancel automation runs on stop")

        hass.data.setdefault("hass_sharp_stop", {})[entry.entry_id] = hass.bus.async_listen(EVENT_HOMEASSISTANT_STOP, on_stop)
        await dotnet_tasks.async_run_dotnet_task(hass, hass_sharp.Init)
        register_panel(hass)
        state.ready = True
        llm.register(hass, state)
        if memory_report.enabled():
            try:
                await hass.async_add_executor_job(memory_report.log, hass_sharp)
            except Exception:
                logger.debug("Cannot report memory after setup", exc_info=True)
    except BaseException:
        state.close()
        hass.data["hass_sharp_llm"].pop(entry.entry_id, None)
        stop_services = hass.data.get("hass_sharp_services", {}).pop(entry.entry_id, None)
        if stop_services is not None:
            stop_services()
        stop_listener = hass.data.get("hass_sharp_stop", {}).pop(entry.entry_id, None)
        if stop_listener is not None:
            stop_listener()
        frontend.async_remove_panel(hass, "hass-sharp")
        native_bridge.close()
        try:
            await dotnet_tasks.async_run_dotnet_task(hass, hass_sharp.UnloadAsync, hass.is_stopping)
        finally:
            try:
                await native_bridge.async_shutdown()
            finally:
                hass.data["hass_sharp_native_triggers"].pop(entry.entry_id, None)
                await hass.config_entries.async_unload_platforms(entry, ["sensor"])
        raise

    return True


async def async_unload_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    hass_sharp = utils.get_hass_sharp_manager(hass)
    state = hass.data.get("hass_sharp_llm", {}).get(entry.entry_id)
    if state is not None:
        state.ready = False
    if not await hass.config_entries.async_unload_platforms(entry, ["sensor"]):
        if state is not None:
            state.ready = True
        return False
    if state is not None:
        state.close()
        hass.data["hass_sharp_llm"].pop(entry.entry_id, None)
    stop_services = hass.data.get("hass_sharp_services", {}).pop(entry.entry_id, None)
    if stop_services is not None:
        stop_services()
    stop_listener = hass.data.get("hass_sharp_stop", {}).pop(entry.entry_id, None)
    if stop_listener is not None:
        stop_listener()

    native_bridge = hass.data.get("hass_sharp_native_triggers", {}).pop(entry.entry_id, None)
    if native_bridge is not None:
        native_bridge.close()
    try:
        # A reload or removal unless Home Assistant is stopping; cancelled runs record which.
        await dotnet_tasks.async_run_dotnet_task(hass, hass_sharp.UnloadAsync, hass.is_stopping)
    finally:
        try:
            if native_bridge is not None:
                await native_bridge.async_shutdown()
        finally:
            frontend.async_remove_panel(hass, "hass-sharp")

    return True


async def async_reload_entry(hass: HomeAssistant, entry: ConfigEntry) -> None:
    """Reload config entry."""
    await hass.config_entries.async_reload(entry.entry_id)
