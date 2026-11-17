"""The switches of the .NET entity definitions; the entity bridge adds and updates them, and their commands go to
the definition's handlers. A failed command fails the service call with .NET's message."""
from homeassistant.components.switch import SwitchEntity
from homeassistant.helpers import entity_platform

from .entity_base import HassSharpEntity


class HassSharpSwitch(HassSharpEntity, SwitchEntity):
    def apply_values(self, values: dict) -> None:
        self._attr_is_on = values.get("is_on")

    async def async_turn_on(self, **kwargs) -> None:
        await self.command("turn_on")

    async def async_turn_off(self, **kwargs) -> None:
        await self.command("turn_off")


async def async_setup_entry(hass, entry, async_add_entities) -> None:
    # No bridge when the entry's setup failed before it existed.
    if (bridge := hass.data.get("hass_sharp_entity_bridge", {}).get(entry.entry_id)) is not None:
        bridge.attach_platform("switch", entity_platform.async_get_current_platform(), HassSharpSwitch)
