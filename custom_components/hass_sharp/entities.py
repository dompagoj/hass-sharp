"""Hand Home Assistant's entity domains and their state names to the .NET manager and keep them current."""
import json

from homeassistant import loader
from homeassistant.const import (
    EVENT_COMPONENT_LOADED,
    EVENT_CORE_CONFIG_UPDATE,
    EVENT_HOMEASSISTANT_STARTED,
    Platform,
)
from homeassistant.core import CoreState, callback
from homeassistant.helpers import entity_registry as er
from homeassistant.helpers.entity_registry import EVENT_ENTITY_REGISTRY_UPDATED
from homeassistant.helpers.event import async_call_later
from homeassistant.helpers.translation import async_get_translations

from .const import logger

DEBOUNCE_SECONDS = 2


async def async_domains(hass):
    """The domains that surely have entities, sorted, whatever their translations.

    The platforms Home Assistant defines (so a domain with nothing loaded yet still gets its typed classes) and
    the domain of every state and registry entry. Loaded components are not here: most (`http`, `frontend`) have
    no entities, so `async_push` adds only those that have entity translations (see `components`).
    """
    domains = {platform.value for platform in Platform}
    domains.update(state.domain for state in hass.states.async_all())
    domains.update(entry.domain for entry in er.async_get(hass).entities.values())
    return sorted(domains)


def components(hass):
    """The top-level names of the loaded components (`sensor.template` is `sensor`), sorted: candidates for a domain."""
    return sorted({component.partition(".")[0] for component in hass.config.components})


async def async_translations(hass, domains):
    """The `entity_component` translations of the domains that are integrations, or none when they cannot be loaded.

    Asking for a domain that is no integration (`sun` is one, `made_up` from a template is not) logs a warning in
    Home Assistant, so only the domains it resolves are asked. State names are cosmetic: without them the
    domains are still pushed.
    """
    try:
        integrations = await loader.async_get_integrations(hass, domains)
        known = [domain for domain in domains if not isinstance(integrations.get(domain), BaseException)]
        return await async_get_translations(hass, hass.config.language, "entity_component", known)
    except Exception as err:
        logger.warning("Entity state names could not be translated; sending the domains without them: %s", err)
        return {}


def payload(domains, translations):
    return json.dumps({"domains": domains, "translations": translations})


async def async_push(hass, manager, active=lambda: True):
    """Send the domains and translations as JSON; failures are logged, never raised.

    Returns the domains sent (the entity domains, not every loaded component), or None when nothing was. Nothing reaches .NET once `active()` is false: the entry
    stopped listening while they were gathered. A push that differs from the last makes .NET rebuild the typed
    entity classes and every node library, so callers keep pushes rare.
    """
    try:
        started = hass.state is CoreState.running
        domains = await async_domains(hass)
        candidates = components(hass)
        translations = await async_translations(hass, sorted(set(domains) | set(candidates)))
        # A component is an entity domain when Home Assistant has entity translations for it (helpers such as
        # `input_boolean` and `timer` are no Platform); the translations of anything else are not sent.
        domains = sorted(set(domains) | {c for c in candidates if any(
            key.startswith(f"component.{c}.entity_component.") for key in translations)})
        translations = {key: text for key, text in translations.items()
                        if any(key.startswith(f"component.{d}.") for d in domains)}
        if not active():
            return None
        await hass.async_add_executor_job(manager.SetEntityDescriptions, payload(domains, translations), started)
        return domains
    except Exception:
        logger.exception("Entity description push failed")
        return None


async def async_start(hass, manager):
    """Listen for changes, then push once; returns the unsubscribe callback.

    Listening first means a "Home Assistant started" event that fires while the first push is still gathering
    schedules a push of its own, which then says started.
    """
    pushed = set()
    unsubscribe = async_track(hass, manager, pushed)
    try:
        pushed.update(await async_push(hass, manager) or ())
    except BaseException:
        # Cancelled (the entry's setup was): nobody else could stop the listeners.
        unsubscribe()
        raise
    return unsubscribe


def async_track(hass, manager, pushed=None):
    """Re-push once changes settle; returns the unsubscribe callback.

    Pushes follow a component loading, Home Assistant starting, a change of language (state names are
    translated) and an entity registry entry of a domain the last push lacked. `pushed` holds that push's domains.
    """
    pushed = set() if pushed is None else pushed
    pending = None
    stopped = False

    async def push(_now):
        nonlocal pending
        pending = None
        if (domains := await async_push(hass, manager, lambda: not stopped)) is not None:
            pushed.clear()
            pushed.update(domains)

    @callback
    def schedule(_event):
        nonlocal pending
        if pending is not None:
            pending()
        pending = async_call_later(hass, DEBOUNCE_SECONDS, push)

    @callback
    def config_updated(event):
        if "language" in (event.data or {}):
            schedule(event)

    @callback
    def registry_updated(event):
        data = event.data or {}
        if data.get("action") == "create" and data.get("entity_id", "").partition(".")[0] not in pushed:
            schedule(event)

    removers = [
        hass.bus.async_listen(event_type, schedule)
        for event_type in (EVENT_COMPONENT_LOADED, EVENT_HOMEASSISTANT_STARTED)
    ]
    removers.append(hass.bus.async_listen(EVENT_CORE_CONFIG_UPDATE, config_updated))
    removers.append(hass.bus.async_listen(EVENT_ENTITY_REGISTRY_UPDATED, registry_updated))

    def unsubscribe():
        nonlocal pending, stopped
        stopped = True
        for remove in removers:
            remove()
        if pending is not None:
            pending()
            pending = None

    return unsubscribe
