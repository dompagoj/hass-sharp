"""The buttons of the .NET entity definitions; the entity bridge adds and updates them, and a press goes to the
definition's handler. A failed press fails the service call with .NET's message."""
from homeassistant.components.button import ButtonEntity
from homeassistant.helpers import entity_platform

from .entity_base import HassSharpEntity


class HassSharpButton(HassSharpEntity, ButtonEntity):
    # No values: Home Assistant shows when the button was last pressed.

    async def async_press(self) -> None:
        await self.command("press")


async def async_setup_entry(hass, entry, async_add_entities) -> None:
    # No bridge when the entry's setup failed before it existed.
    if (bridge := hass.data.get("hass_sharp_entity_bridge", {}).get(entry.entry_id)) is not None:
        bridge.attach_platform("button", entity_platform.async_get_current_platform(), HassSharpButton)
