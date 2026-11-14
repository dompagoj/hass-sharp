"""A debug line after each setup of the entry: what the process holds, to tell a leak from growth that stops.

Only when the ``custom_components.HassSharp.memory`` logger itself is set to debug (AGENTS.md: the leak probe), never by
turning on debug logging for ``custom_components.HassSharp``: it walks every Python object while holding the GIL and
forces a full .NET collection, which stalls Home Assistant for a moment on a large instance. One line per setup, with:

- the process's ``/proc/self/smaps_rollup`` (Rss, Pss_Anon, Private_Dirty);
- the .NET side (``HassSharpManager.MemoryReport``): live and committed GC bytes, loaded and collectible assemblies and
  load contexts;
- Python's tracked objects after a collection, and the types that grew most since the previous line and since the first.
"""

from collections import Counter
import gc
import logging

from .const import logger as _integration_logger

# Its own logger, checked by its own level: the parent's debug setting does not turn the probe on.
logger = _integration_logger.getChild("memory")

_first: Counter | None = None
_previous: Counter | None = None
_setups = 0


def enabled() -> bool:
    return logger.level == logging.DEBUG


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
