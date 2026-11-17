import logging
DOMAIN = "hass_sharp"
# The entity platforms of the custom entities.
PLATFORMS = ["binary_sensor", "sensor", "switch"]

logger = logging.getLogger('custom_components.HassSharp')