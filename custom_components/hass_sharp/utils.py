from typing import Any
from homeassistant.core import HomeAssistant
from homeassistant.helpers import entity_registry

import os

def get_file_name(path: str):
  return os.path.basename(path)

def get_hass_entities(hass: HomeAssistant):
  # Combine entities from states and registry to ensure we get everything
  entity_ids = set(hass.states.async_entity_ids())
  registry = entity_registry.async_get(hass)
  entity_ids.update(registry.entities.keys())
      
  return list(entity_ids)


HASS_DATA_MANAGER_KEY = "hass_sharp:manager"

def set_hass_sharp_manager(hass: HomeAssistant, manager: Any):
  hass.data[HASS_DATA_MANAGER_KEY] = manager

def get_hass_sharp_manager(hass: HomeAssistant):
   return hass.data[HASS_DATA_MANAGER_KEY]
