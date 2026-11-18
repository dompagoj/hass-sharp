"""The numbers of the .NET entity definitions; the entity bridge adds and updates them, and a new value goes to the
definition's handler. Home Assistant refuses a value outside the range before it gets here; .NET refuses the rest,
and a failed command fails the service call with .NET's message."""
from homeassistant.components.number import NumberEntity, NumberMode
from homeassistant.helpers import entity_platform

from .entity_base import HassSharpEntity


class HassSharpNumber(HassSharpEntity, NumberEntity):
    def apply(self, description: dict, device: dict | None, definition_name: str) -> None:
        super().apply(description, device, definition_name)
        # .NET always describes all five; the fallbacks are its defaults.
        options = description.get("options") or {}
        self._attr_native_min_value = options.get("min", 0)
        self._attr_native_max_value = options.get("max", 100)
        self._attr_native_step = options.get("step", 1)
        self._attr_mode = NumberMode(options.get("mode") or NumberMode.AUTO)
        self._attr_native_unit_of_measurement = options.get("unit")

    def apply_values(self, values: dict) -> None:
        self._attr_native_value = values.get("value")

    async def async_set_native_value(self, value: float) -> None:
        await self.command("set_value", value=value)


async def async_setup_entry(hass, entry, async_add_entities) -> None:
    # No bridge when the entry's setup failed before it existed.
    if (bridge := hass.data.get("hass_sharp_entity_bridge", {}).get(entry.entry_id)) is not None:
        bridge.attach_platform("number", entity_platform.async_get_current_platform(), HassSharpNumber)
