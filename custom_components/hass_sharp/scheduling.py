"""Daily wall-clock recurrence: skip gaps/missed starts, first fold only."""
from datetime import datetime, time, timedelta, timezone
from zoneinfo import ZoneInfo


def next_occurrence(rule, after):
    if rule.get('AtUtc'):
        planned = datetime.fromisoformat(rule['AtUtc'])
        if planned.tzinfo is None:
            raise ValueError('One-shot timestamp requires an offset')
        planned = planned.astimezone(timezone.utc)
        return planned if planned > after else None
    zone = ZoneInfo(rule['TimeZone'])
    wall = time.fromisoformat(rule['LocalTime'])
    if wall.tzinfo is not None:
        raise ValueError('LocalTime must have no offset')
    date = after.astimezone(zone).date()
    for offset in range(370):
        local = datetime.combine(date + timedelta(days=offset), wall)
        planned = local.replace(tzinfo=zone, fold=0).astimezone(timezone.utc)
        if planned.astimezone(zone).replace(tzinfo=None) != local:
            continue  # nonexistent local time
        if planned > after:
            return planned
    raise ValueError('No occurrence in the next year')


def register(rule, dispatch, track, now):
    """All callbacks and cancellation run on HA's event loop."""
    active = True
    unsubscribe = None
    last = None

    def arm(after):
        nonlocal unsubscribe
        planned = next_occurrence(rule, after)
        if planned is None:
            return

        def fire(actual):
            nonlocal last
            if not active or (last is not None and planned <= last):
                return
            last = planned
            # Re-arm before dispatch; a failed invocation does not lose recurrence.
            arm(max(planned, now()))
            dispatch(planned.isoformat())

        unsubscribe = track(fire, planned)

    def cancel():
        nonlocal active
        active = False
        if unsubscribe is not None:
            unsubscribe()

    arm(now())
    return cancel


def register_ha(hass, rule_json, dispatch):
    """Called from .NET worker threads; HA helpers belong to its event loop."""
    import json
    import threading
    from homeassistant.core import callback
    from homeassistant.helpers.event import async_track_point_in_utc_time
    from homeassistant.util.dt import utcnow
    rule = json.loads(rule_json)
    next_occurrence(rule, utcnow())  # validate synchronously before accepting
    stopped = threading.Event()
    cleanup = []

    @callback
    def install():
        if stopped.is_set():
            return
        cleanup.append(register(rule, dispatch,
            lambda fn, at: async_track_point_in_utc_time(hass, callback(fn), at), utcnow))

    @callback
    def remove():
        for cancel in cleanup:
            cancel()
        cleanup.clear()

    def cancel():
        stopped.set()
        hass.loop.call_soon_threadsafe(remove)

    hass.loop.call_soon_threadsafe(install)
    return cancel
