"""A debug line after each setup of the entry: what the process holds, to tell a leak from growth that stops.

Only with debug logging on for ``custom_components.HassSharp``: it walks every Python object and forces a full .NET
collection, which takes a moment. One line per setup, with:

- the process's ``/proc/self/smaps_rollup`` (Rss, Pss_Anon, Private_Dirty);
- the .NET side (``HassSharpManager.MemoryReport``): live and committed GC bytes, loaded and collectible assemblies and
  load contexts;
- Python's tracked objects after a collection, and the types that grew most since the previous line and since the first.
"""

from collections import Counter
import gc
import logging

from .const import logger

_first: Counter | None = None
_previous: Counter | None = None
_setups = 0


def enabled() -> bool:
    return logger.isEnabledFor(logging.DEBUG)


def _smaps() -> str:
    try:
        with open("/proc/self/smaps_rollup", encoding="ascii") as rollup:
            fields = dict(line.split(":", 1) for line in rollup if ":" in line)
    except OSError:
        return "smaps unavailable"
    return ", ".join(f"{key} {fields[key].strip()}" for key in ("Rss", "Pss_Anon", "Private_Dirty") if key in fields)


def _grown(now: Counter, then: Counter | None, count: int = 8) -> str:
    if then is None:
        return "-"
    growth = [(name, now[name] - then.get(name, 0)) for name in now]
    growth = [item for item in growth if item[1] > 0]
    growth.sort(key=lambda item: item[1], reverse=True)
    return ", ".join(f"{name} +{delta}" for name, delta in growth[:count]) or "none"


def log(hass_sharp) -> None:
    """Runs in an executor after the entry is set up."""
    global _first, _previous, _setups
    _setups += 1
    try:
        dotnet = hass_sharp.MemoryReport()
    except Exception as error:  # A diagnostic: never let it fail a setup.
        dotnet = f"dotnet unavailable ({error})"
    gc.collect()
    objects = gc.get_objects()
    types = Counter(type(item).__qualname__ for item in objects)
    total = len(objects)
    del objects
    logger.debug(
        "Memory after setup %d: %s; %s; python objects %d, grown since last: %s; since first: %s",
        _setups, _smaps(), dotnet, total, _grown(types, _previous), _grown(types, _first),
    )
    _previous = types
    if _first is None:
        _first = types
