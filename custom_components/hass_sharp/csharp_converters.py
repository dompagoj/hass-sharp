from enum import Enum

from HassSharp import HasEntityState
from System import Action, String, Func, Object, Int32
from System.Collections.Generic import Dictionary

from homeassistant.core import State


def to_has_entity_state(entity_state: State | None):
    if entity_state is None: return None

    attributes_dict = Dictionary[String, Object]()

    for k, v in entity_state.attributes.items():
        if isinstance(v, Enum):
            v = v.value
        elif isinstance(v, (set, tuple)):
            v = list(v)
        elif hasattr(v, 'isoformat'):
            v = v.isoformat()
        attributes_dict[k] = v

    ref = HasEntityState()
    ref.EntityId = entity_state.entity_id
    ref.Domain = entity_state.domain
    ref.ObjectId = entity_state.object_id
    ref.State = entity_state.state
    ref.Attributes = attributes_dict
    ref.LastChanged = entity_state.last_changed_timestamp
    ref.LastReported = entity_state.last_reported_timestamp

    return ref
