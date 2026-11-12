"""Real Python.NET Task/native-trigger boundary. No service calls.

Run in a fresh process: python .../native_trigger_bridge_smoke.py INTEGRATION_DIR [DOTNET_ROOT]
The integration must be published first. HA 2026.11.1 is the minimum acceptance runtime.
"""
import asyncio
import importlib.util
import json
import os
from pathlib import Path
import sys

root = Path(sys.argv[1]).resolve()
os.environ['DOTNET_SYSTEM_GLOBALIZATION_INVARIANT'] = 'true'
from pythonnet import load
kwargs = {'dotnet_root': sys.argv[2]} if len(sys.argv) > 2 else {}
load('coreclr', runtime_config=str(root / 'dotnet.runtimeconfig.json'), **kwargs)
sys.path.insert(0, str(root / 'out'))
import clr
clr.AddReference('HassSharp')
from HassSharp import PyInterop
from System import Action, Func, String
from System.Threading.Tasks import Task
from homeassistant import loader
from homeassistant.core import HomeAssistant

def module(name):
    spec = importlib.util.spec_from_file_location(name, root / (name + '.py'))
    result = importlib.util.module_from_spec(spec)
    sys.modules[name] = result
    spec.loader.exec_module(result)
    return result

native = module('native_triggers')
dotnet_tasks = module('dotnet_tasks')

async def main():
    hass = HomeAssistant('/tmp/hass-sharp-native-pythonnet')
    loader.async_setup(hass)
    await native.trigger.async_setup(hass)
    bridge = native.NativeTriggerBridge(hass)
    received = []
    def register(generation, configs, fire, success, error):
        async def managed_fire(payload):
            await dotnet_tasks.async_run_dotnet_task(hass, fire, payload)
        bridge.begin_register(generation, configs, managed_fire, success, error)
    PyInterop.BeginRegisterHaTriggers = Action[String, String, Func[String, Task], Action[String], Action[String]](register)
    PyInterop.BeginRemoveHaTriggers = Action[String, Action, Action[String]](bridge.begin_remove)
    # The managed delegate executes through the same executor/Task adapter as setup.
    managed_fire = Func[String, Task](lambda payload: (received.append(json.loads(payload)), Task.CompletedTask)[1])
    configuration = json.dumps([{'Id': 'native-smoke', 'ConfigurationJson': json.dumps({'trigger': 'event', 'event_type': 'hass_sharp_pythonnet_test'})}])
    lease = await asyncio.wait_for(dotnet_tasks.async_run_dotnet_task(hass, PyInterop.RegisterHaTriggersAsync, 'native-smoke', configuration, managed_fire), 10)
    hass.bus.async_fire('hass_sharp_pythonnet_test', {'value': 42})
    await hass.async_block_till_done()
    assert received[0]['trigger']['event']['data']['value'] == 42
    assert received[0]['trigger']['id'] == 'native-smoke'
    await asyncio.wait_for(dotnet_tasks.async_run_dotnet_task(hass, lambda: lease.DisposeAsync().AsTask()), 10)
    hass.bus.async_fire('hass_sharp_pythonnet_test', {'value': 99})
    await hass.async_block_till_done()
    assert len(received) == 1
    # Error callback must fault a real managed Task rather than return a lease.
    invalid = json.dumps([{'Id': 'bad', 'ConfigurationJson': '{"trigger":"hass_sharp_not_installed"}'}])
    try:
        await asyncio.wait_for(dotnet_tasks.async_run_dotnet_task(hass, PyInterop.RegisterHaTriggersAsync, 'bad', invalid, managed_fire), 10)
    except Exception as error:
        assert 'Native trigger registration failed' in str(error), error
    else:
        raise AssertionError('invalid registration succeeded')
    await bridge.async_shutdown()
    await hass.async_stop(force=True)
    print('PASS real Python.NET native trigger Task registration, dispatch, removal, failure; no blocked loop/executor')

asyncio.run(main())
