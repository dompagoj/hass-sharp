from typing import Any
from homeassistant.core import HomeAssistant
from homeassistant.helpers import entity_registry as er

import os

def get_file_name(path: str):
  return os.path.basename(path)

def get_hass_entities(hass: HomeAssistant):
    state_entity_ids = set(hass.states.async_entity_ids())

    ent_reg = er.async_get(hass)
    registry_entity_ids = set(ent_reg.entities.keys())

    all_entity_ids = list(state_entity_ids | registry_entity_ids)

    return all_entity_ids


def get_hass_entity_suggestions(hass: HomeAssistant):
    registry = er.async_get(hass)
    entities = []
    for entity_id in sorted(get_hass_entities(hass)):
        state = hass.states.get(entity_id)
        entry = registry.entities.get(entity_id)
        name = (state.attributes.get("friendly_name") if state else None) or (
            (entry.name or entry.original_name) if entry else None
        )
        entity = {"entity_id": entity_id}
        if name:
            entity["friendly_name"] = name
        entities.append(entity)
    return entities


HASS_DATA_MANAGER_KEY = "hass_sharp:manager"

def set_hass_sharp_manager(hass: HomeAssistant, manager: Any):
  hass.data[HASS_DATA_MANAGER_KEY] = manager

def get_hass_sharp_manager(hass: HomeAssistant):
   return hass.data[HASS_DATA_MANAGER_KEY]
