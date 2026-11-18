"""The covers of the .NET entity definitions; the entity bridge adds and updates them, and their commands go to the
definition's handlers. Every cover opens, closes and stops; one with a position also moves to one (Home Assistant
refuses a position for any other). A failed command fails the service call with .NET's message."""
from homeassistant.components.cover import ATTR_POSITION, CoverEntity, CoverEntityFeature
from homeassistant.helpers import entity_platform

from .entity_base import HassSharpEntity


class HassSharpCover(HassSharpEntity, CoverEntity):
    def apply(self, description: dict, device: dict | None, definition_name: str) -> None:
        super().apply(description, device, definition_name)
        features = CoverEntityFeature.OPEN | CoverEntityFeature.CLOSE | CoverEntityFeature.STOP
        if (description.get("options") or {}).get("positionable"):
            features |= CoverEntityFeature.SET_POSITION
        self._attr_supported_features = features

    def apply_values(self, values: dict) -> None:
        position = values.get("position")
        self._attr_current_cover_position = position
        # Closed as .NET says; else at position 0 when it knows a position; else unknown.
        closed = values.get("is_closed")
        if closed is None and position is not None:
            closed = position == 0
        self._attr_is_closed = closed

    async def async_open_cover(self, **kwargs) -> None:
        await self.command("open")

    async def async_close_cover(self, **kwargs) -> None:
        await self.command("close")

    async def async_stop_cover(self, **kwargs) -> None:
        await self.command("stop")

    async def async_set_cover_position(self, **kwargs) -> None:
        await self.command("set_position", position=kwargs[ATTR_POSITION])


async def async_setup_entry(hass, entry, async_add_entities) -> None:
    # No bridge when the entry's setup failed before it existed.
    if (bridge := hass.data.get("hass_sharp_entity_bridge", {}).get(entry.entry_id)) is not None:
        bridge.attach_platform("cover", entity_platform.async_get_current_platform(), HassSharpCover)
