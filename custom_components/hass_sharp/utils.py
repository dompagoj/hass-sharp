from homeassistant.core import HomeAssistant
from homeassistant.helpers import entity_registry

def get_hass_entities(hass: HomeAssistant):
  # Combine entities from states and registry to ensure we get everything
  entity_ids = set(hass.states.async_entity_ids())
  registry = entity_registry.async_get(hass)
  entity_ids.update(registry.entities.keys())
      
  return list(entity_ids)