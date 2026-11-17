"""The sensors of the .NET entity definitions; the entity bridge adds and updates them."""
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

    def apply_values(self, values: dict) -> None:
        self._attr_native_value = values.get("value")


async def async_setup_entry(hass, entry, async_add_entities) -> None:
    # No bridge when the entry's setup failed before it existed.
    if (bridge := hass.data.get("hass_sharp_entity_bridge", {}).get(entry.entry_id)) is not None:
        bridge.attach_platform("sensor", entity_platform.async_get_current_platform(), HassSharpSensor)
