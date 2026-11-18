import logging
DOMAIN = "hass_sharp"
# The entity platforms of the custom entities, in the order of the catalog's entityDomains.
PLATFORMS = ["binary_sensor", "sensor", "switch", "button", "number", "select", "text", "light", "cover", "camera"]

logger = logging.getLogger('custom_components.HassSharp')