"""The sensors of the .NET entity definitions; the entity bridge adds and updates them. A sync that changes the
options drops a value Home Assistant would no longer show."""
from homeassistant.components.sensor import SensorEntity
from homeassistant.helpers import entity_platform
from homeassistant.util import dt as dt_util

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
        self._attr_native_value = self._native(values.get("value"))

    def _native(self, value):
        """The value as Home Assistant takes it: of a timestamp or date sensor, the ISO 8601 text .NET sends as an aware
        datetime (one without an offset is in Home Assistant's time zone) or a date (of a date and time, its date as
        written). Text that reads as neither is kept, which Home Assistant refuses as the bridge writes the state."""
        if not isinstance(value, str) or self._attr_device_class not in ("timestamp", "date"):
            return value
        if self._attr_device_class == "date" and (day := dt_util.parse_date(value)) is not None:
            return day
        # Home Assistant's parser, not datetime.fromisoformat: it takes the seven digits of the second .NET writes.
        if (moment := dt_util.parse_datetime(value)) is None:
            return value
        if self._attr_device_class == "date":
            return moment.date()
        return moment if moment.tzinfo is not None else moment.replace(tzinfo=dt_util.get_default_time_zone())


async def async_setup_entry(hass, entry, async_add_entities) -> None:
    # No bridge when the entry's setup failed before it existed.
    if (bridge := hass.data.get("hass_sharp_entity_bridge", {}).get(entry.entry_id)) is not None:
        bridge.attach_platform("sensor", entity_platform.async_get_current_platform(), HassSharpSensor)
