"""The selects of the .NET entity definitions; the entity bridge adds and updates them, and a choice goes to the
definition's handler. Home Assistant refuses an option not in the list before it gets here, and a failed command
fails the service call with .NET's message. A sync that removes the current option from the list drops it."""
from homeassistant.components.select import SelectEntity
from homeassistant.helpers import entity_platform

from .entity_base import HassSharpEntity


class HassSharpSelect(HassSharpEntity, SelectEntity):
    def apply(self, description: dict, device: dict | None, definition_name: str) -> None:
        super().apply(description, device, definition_name)
        self._attr_options = list((description.get("options") or {}).get("options") or [])
        # An option the list no longer has is dropped: the select is unknown until .NET pushes one again.
        if getattr(self, "_attr_current_option", None) not in (None, *self._attr_options):
            self._attr_current_option = None

    def apply_values(self, values: dict) -> None:
        self._attr_current_option = values.get("option")

    async def async_select_option(self, option: str) -> None:
        await self.command("select_option", option=option)


async def async_setup_entry(hass, entry, async_add_entities) -> None:
    # No bridge when the entry's setup failed before it existed.
    if (bridge := hass.data.get("hass_sharp_entity_bridge", {}).get(entry.entry_id)) is not None:
        bridge.attach_platform("select", entity_platform.async_get_current_platform(), HassSharpSelect)
