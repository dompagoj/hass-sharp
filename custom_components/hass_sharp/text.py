"""The texts of the .NET entity definitions; the entity bridge adds and updates them, and a new text goes to the
definition's handler. Home Assistant refuses a text of the wrong length or one that does not match the pattern
before it gets here, and a failed command fails the service call with .NET's message."""
import re

from homeassistant.components.text import TextEntity, TextMode
from homeassistant.helpers import entity_platform

from .entity_base import HassSharpEntity


class HassSharpText(HassSharpEntity, TextEntity):
    @classmethod
    def refusal(cls, description: dict) -> str | None:
        # .NET checks the pattern with its own regular expressions; Home Assistant matches every state with Python's,
        # and a pattern Python cannot read would fail each state write with a traceback.
        pattern = (description.get("options") or {}).get("pattern")
        if pattern is None:
            return None
        try:
            re.compile(pattern)
        except re.error as error:
            name, key = description.get("name"), description["key"]
            called = f"{name} ({key})" if name else key
            return f"{called} has the pattern {pattern}, which Home Assistant cannot use: {error}"
        return None

    def apply(self, description: dict, device: dict | None, definition_name: str) -> None:
        super().apply(description, device, definition_name)
        # .NET always describes all four; the fallbacks are its defaults.
        options = description.get("options") or {}
        self._attr_native_min = options.get("min", 0)
        self._attr_native_max = options.get("max", 100)
        self._attr_pattern = options.get("pattern")
        self._attr_mode = TextMode(options.get("mode") or TextMode.TEXT)

    def apply_values(self, values: dict) -> None:
        self._attr_native_value = values.get("value")

    async def async_set_value(self, value: str) -> None:
        await self.command("set_value", value=value)


async def async_setup_entry(hass, entry, async_add_entities) -> None:
    # No bridge when the entry's setup failed before it existed.
    if (bridge := hass.data.get("hass_sharp_entity_bridge", {}).get(entry.entry_id)) is not None:
        bridge.attach_platform("text", entity_platform.async_get_current_platform(), HassSharpText)
