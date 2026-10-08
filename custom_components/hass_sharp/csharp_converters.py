from enum import Enum

from homeassistant.core import State
from System import Object, String
from System.Collections.Generic import Dictionary

from HassSharp import HasEntityState


def to_has_entity_state(entity_state: State | None):
    if entity_state is None:
        return None

    attributes_dict = Dictionary[String, Object]()

    for k, v in entity_state.attributes.items():
        if isinstance(v, Enum):
            v = v.value
        elif isinstance(v, (set, tuple)):
            v = list(v)
        elif hasattr(v, "isoformat"):
            v = v.isoformat()
        attributes_dict[str(k)] = v

    ref = HasEntityState()
    ref.EntityId = entity_state.entity_id
    ref.Domain = entity_state.domain
    ref.ObjectId = entity_state.object_id
    ref.State = entity_state.state
    ref.Attributes = attributes_dict
    ref.LastChanged = entity_state.last_changed_timestamp
    ref.LastReported = entity_state.last_reported_timestamp

    return ref
