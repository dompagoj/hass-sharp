"""The binary sensors of the .NET entity definitions; the entity bridge adds and updates them."""
from homeassistant.components.binary_sensor import BinarySensorEntity
from homeassistant.helpers import entity_platform

from .entity_base import HassSharpEntity


class HassSharpBinarySensor(HassSharpEntity, BinarySensorEntity):
    def apply_values(self, values: dict) -> None:
        self._attr_is_on = values.get("is_on")


async def async_setup_entry(hass, entry, async_add_entities) -> None:
    # No bridge when the entry's setup failed before it existed.
    if (bridge := hass.data.get("hass_sharp_entity_bridge", {}).get(entry.entry_id)) is not None:
        bridge.attach_platform("binary_sensor", entity_platform.async_get_current_platform(), HassSharpBinarySensor)
