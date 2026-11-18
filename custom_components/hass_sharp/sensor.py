"""The sensors of the .NET entity definitions; the entity bridge adds and updates them. A sync that changes the
options drops a value Home Assistant would no longer show."""
from homeassistant.components.sensor import SensorEntity
from homeassistant.helpers import entity_platform

from .entity_base import HassSharpEntity


class HassSharpSensor(HassSharpEntity, SensorEntity):
    def apply(self, description: dict, device: dict | None, definition_name: str) -> None:
        super().apply(description, device, definition_name)
        options = description.get("options") or {}
        self._attr_native_unit_of_measurement = options.get("unit")
        self._attr_state_class = options.get("stateClass")
        self._attr_options = options.get("options")
        # On a sync of a live sensor, a value Home Assistant would no longer show (one its new options lack, text where
        # its new unit or state class wants a number) is dropped, as the state write would fail on it: the sensor is
        # unknown until .NET pushes a value again.
        if self.hass is not None and getattr(self, "_attr_native_value", None) is not None:
            try:
                _ = self.state
            except ValueError:
                self._attr_native_value = None

    def apply_values(self, values: dict) -> None:
        self._attr_native_value = values.get("value")


async def async_setup_entry(hass, entry, async_add_entities) -> None:
    # No bridge when the entry's setup failed before it existed.
    if (bridge := hass.data.get("hass_sharp_entity_bridge", {}).get(entry.entry_id)) is not None:
        bridge.attach_platform("sensor", entity_platform.async_get_current_platform(), HassSharpSensor)
