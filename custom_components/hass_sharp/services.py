"""Hand Home Assistant service descriptions to the .NET manager and keep them current."""
import json

from homeassistant.const import EVENT_HOMEASSISTANT_STARTED, EVENT_SERVICE_REGISTERED, EVENT_SERVICE_REMOVED
from homeassistant.core import callback
from homeassistant.helpers.event import async_call_later
from homeassistant.helpers.json import JSONEncoder
from homeassistant.helpers.service import async_get_all_descriptions
from homeassistant.helpers.translation import async_get_translations

from .const import logger

DEBOUNCE_SECONDS = 2


async def async_push(hass, manager):
    """Send `{domain: {service: description}}` as JSON; failures are logged, never raised."""
    try:
        descriptions = await async_get_all_descriptions(hass)
        translations = await async_get_translations(hass, hass.config.language, "services", list(descriptions))
        payload = json.dumps(translated(descriptions, translations), cls=JSONEncoder)
        await hass.async_add_executor_job(manager.SetServiceDescriptions, payload)
    except Exception:
        logger.exception("Service description push failed")


def translated(descriptions, translations):
    """Copy the descriptions with the names Home Assistant's own UI shows.

    Built-in services keep their names and field names in translations, not in the descriptions.
    A translation wins over a name in the description, as in Home Assistant's UI. The cached
    descriptions are not changed.
    """
    def texts(source, prefix):
        copy = dict(source)
        for item in ("name", "description"):
            if text := translations.get(prefix + item):
                copy[item] = text
        return copy

    def fields(raw, prefix):
        if not isinstance(raw, dict):
            return raw
        result = {}
        for key, field in raw.items():
            if isinstance(field, dict) and isinstance(field.get("fields"), dict):
                section = texts(field, f"{prefix}sections.{key}.")
                section["fields"] = fields(field["fields"], prefix)
                result[key] = section
            elif isinstance(field, dict):
                result[key] = texts(field, f"{prefix}fields.{key}.")
            else:
                result[key] = field
        return result

    result = {}
    for domain, services in descriptions.items():
        if not isinstance(services, dict):
            result[domain] = services
            continue
        result[domain] = {}
        for service, description in services.items():
            prefix = f"component.{domain}.services.{service}."
            if not isinstance(description, dict):
                result[domain][service] = description
                continue
            copy = texts(description, prefix)
            if "fields" in description:
                copy["fields"] = fields(description["fields"], prefix)
            result[domain][service] = copy
    return result


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
