from logging import INFO
from re import I
from typing import Dict, List
from .const import DOMAIN, logger
import os
import sys
import pythonnet as pynet
import asyncio
from clr_loader import get_coreclr

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant, State, callback, Event, EventStateChangedData
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
    from HassSharp import CodeCompiler, PyInterop, HasEntityState
    from System import Action, String, Func, Object, Int32
    from System.Collections.Generic import Dictionary


    def entity(entityId: str, csharpFunc: str) :
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

    PyInterop.Log = Action[Int32, String](python_log)
    PyInterop.Entity = Func[String, String, HasEntityState](entity)

    csharp_source = """
using HassSharp;

public class TestAutomation : Automation
{
  public void OnNumberChange()
  {
      var numberValue = Entity<float>("input_number.test").Value;
      PyLogger.Info($"Value changed! from c# {numberValue}");
  }

  public void OnButtonPress()
  {
    var buttonState = Entity("input_button.test").Value;
    if (Initializing) return;

    PyLogger.Info($"Button pressed? {buttonState}");
  }
}
"""
    runner = CodeCompiler.Compile([csharp_source])
    runner.RunAll()

    dependencies = runner.DependencyTracking

    @callback
    def on_entity_change(event: Event[EventStateChangedData]):
      entity_id = event.data.get("entity_id")
      if entity_id in dependencies:
        for func in dependencies[entity_id]:
            runner.RunMethod(func)

    for entityId in dependencies.Keys:
      unsub = async_track_state_change_event(hass, entityId, on_entity_change)
    
    # If your component supports config entries, this should NOT be relied on
    # Use async_setup_entry instead
    return True

async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry):
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
      {"hello": "asd"},
      False,
    )

    frontend.add_extra_js_url(hass, "/hass-sharp-static/hass-sharp.js")

    return True

async def async_reload_entry(hass: HomeAssistant, entry: ConfigEntry) -> None:
    logger.info("Unloading...")