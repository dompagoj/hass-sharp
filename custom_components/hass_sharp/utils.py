from typing import Any
from homeassistant.core import HomeAssistant
from homeassistant.helpers import (
    area_registry as ar,
    device_registry as dr,
    entity_registry as er,
    floor_registry as fr,
    label_registry as lr,
)

import os

def get_file_name(path: str):
  return os.path.basename(path)

def get_hass_entities(hass: HomeAssistant):
    state_entity_ids = set(hass.states.async_entity_ids())

    ent_reg = er.async_get(hass)
    registry_entity_ids = set(ent_reg.entities.keys())

    all_entity_ids = list(state_entity_ids | registry_entity_ids)

    return all_entity_ids


def _entity_state_options(domain: str, attributes: dict):
    key = {"climate": "hvac_modes", "select": "options", "input_select": "options"}.get(domain)
    value = attributes.get(key) if key else None
    return list(value) if isinstance(value, (list, tuple)) else None


def get_hass_entity_registry(hass: HomeAssistant):
    registry = er.async_get(hass)
    devices = dr.async_get(hass).devices
    entities = []
    for entity_id in sorted(get_hass_entities(hass)):
        state = hass.states.get(entity_id)
        attributes = state.attributes if state else {}
        entry = registry.entities.get(entity_id)
        device = devices.get(entry.device_id) if entry and entry.device_id else None
        domain = entity_id.split(".", 1)[0]
        supported_features = attributes.get("supported_features")
        entities.append({
            "entity_id": entity_id,
            "name": attributes.get("friendly_name") or ((entry.name or entry.original_name) if entry else None),
            "domain": domain,
            "area_id": (entry.area_id if entry else None) or (device.area_id if device else None),
            "device_id": entry.device_id if entry else None,
            "device_class": ((entry.device_class or entry.original_device_class) if entry else None)
                or attributes.get("device_class"),
            "supported_features": supported_features if supported_features is not None
                else (entry.supported_features if entry else None),
            "unit": attributes.get("unit_of_measurement") or (entry.unit_of_measurement if entry else None),
            "icon": attributes.get("icon") or ((entry.icon or entry.original_icon) if entry else None),
            "disabled": entry is not None and entry.disabled_by is not None,
            "state_options": _entity_state_options(domain, attributes),
        })
    return {
        "entities": entities,
        "areas": [{"area_id": area.id, "name": area.name, "floor_id": area.floor_id}
                  for area in ar.async_get(hass).async_list_areas()],
        "devices": [{"id": device.id, "name": device.name_by_user or device.name, "area_id": device.area_id}
                    for device in devices.values()],
        "floors": [{"floor_id": floor.floor_id, "name": floor.name}
                   for floor in fr.async_get(hass).async_list_floors()],
        "labels": [{"label_id": label.label_id, "name": label.name}
                   for label in lr.async_get(hass).async_list_labels()],
    }


HASS_DATA_MANAGER_KEY = "hass_sharp:manager"

def set_hass_sharp_manager(hass: HomeAssistant, manager: Any):
  hass.data[HASS_DATA_MANAGER_KEY] = manager

def get_hass_sharp_manager(hass: HomeAssistant):
   return hass.data[HASS_DATA_MANAGER_KEY]
