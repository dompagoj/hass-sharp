from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.components.sensor import SensorEntity, SensorDeviceClass
from homeassistant.helpers.entity_platform import AddEntitiesCallback
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant

from .const import logger, DOMAIN

async def async_setup_entry(
        hass: HomeAssistant,
        entry: ConfigEntry,
        async_add_entities: AddEntitiesCallback
) -> None:
    logger.info('Sensor setup init!')
