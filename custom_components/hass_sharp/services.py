"""Hand Home Assistant service descriptions to the .NET manager and keep them current."""
import json

from homeassistant.const import EVENT_HOMEASSISTANT_STARTED, EVENT_SERVICE_REGISTERED, EVENT_SERVICE_REMOVED
from homeassistant.core import callback
from homeassistant.helpers.event import async_call_later
from homeassistant.helpers.json import JSONEncoder
from homeassistant.helpers.service import async_get_all_descriptions

from .const import logger

DEBOUNCE_SECONDS = 2


async def async_push(hass, manager):
    """Send `{domain: {service: description}}` as JSON; failures are logged, never raised."""
    try:
        descriptions = await async_get_all_descriptions(hass)
        payload = json.dumps(descriptions, cls=JSONEncoder)
        await hass.async_add_executor_job(manager.SetServiceDescriptions, payload)
    except Exception:
        logger.exception("Service description push failed")


def async_track(hass, manager):
    """Re-push descriptions once service changes settle; returns the unsubscribe callback."""
    pending = None

    async def push(_now):
        nonlocal pending
        pending = None
        await async_push(hass, manager)

    @callback
    def schedule(_event):
        nonlocal pending
        if pending is not None:
            pending()
        pending = async_call_later(hass, DEBOUNCE_SECONDS, push)

    removers = [
        hass.bus.async_listen(event_type, schedule)
        for event_type in (EVENT_SERVICE_REGISTERED, EVENT_SERVICE_REMOVED, EVENT_HOMEASSISTANT_STARTED)
    ]

    def unsubscribe():
        nonlocal pending
        for remove in removers:
            remove()
        if pending is not None:
            pending()
            pending = None

    return unsubscribe
