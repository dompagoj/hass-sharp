"""The lights of the .NET entity definitions; the entity bridge adds and updates them, and their commands go to the
definition's handlers. A light that can be dimmed has the brightness colour mode, any other on and off only (Home
Assistant then drops a brightness from turn on). A failed command fails the service call with .NET's message."""
from homeassistant.components.light import ATTR_BRIGHTNESS, ColorMode, LightEntity
from homeassistant.helpers import entity_platform

from .entity_base import HassSharpEntity


class HassSharpLight(HassSharpEntity, LightEntity):
    def apply(self, description: dict, device: dict | None, definition_name: str) -> None:
        super().apply(description, device, definition_name)
        mode = ColorMode.BRIGHTNESS if (description.get("options") or {}).get("dimmable") else ColorMode.ONOFF
        self._attr_supported_color_modes = {mode}
        self._attr_color_mode = mode

    def apply_values(self, values: dict) -> None:
        self._attr_is_on = values.get("is_on")
        self._attr_brightness = values.get("brightness")

    async def async_turn_on(self, **kwargs) -> None:
        # The brightness only when Home Assistant gave one: without it, the light comes on as the definition says.
        if ATTR_BRIGHTNESS in kwargs:
            await self.command("turn_on", brightness=kwargs[ATTR_BRIGHTNESS])
        else:
            await self.command("turn_on")

    async def async_turn_off(self, **kwargs) -> None:
        await self.command("turn_off")


async def async_setup_entry(hass, entry, async_add_entities) -> None:
    # No bridge when the entry's setup failed before it existed.
    if (bridge := hass.data.get("hass_sharp_entity_bridge", {}).get(entry.entry_id)) is not None:
        bridge.attach_platform("light", entity_platform.async_get_current_platform(), HassSharpLight)
