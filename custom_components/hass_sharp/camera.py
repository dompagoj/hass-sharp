"""The cameras of the .NET entity definitions; the entity bridge adds and updates them. For now each one gives no
picture: the pictures .NET makes come with the bridge's camera support."""
from homeassistant.components.camera import Camera
from homeassistant.helpers import entity_platform

from .entity_base import HassSharpEntity


class HassSharpCamera(HassSharpEntity, Camera):
    # HassSharpEntity's initialiser calls Camera's (the next in the order of bases), which a camera needs.

    async def async_camera_image(self, width: int | None = None, height: int | None = None) -> bytes | None:
        return None


async def async_setup_entry(hass, entry, async_add_entities) -> None:
    # No bridge when the entry's setup failed before it existed.
    if (bridge := hass.data.get("hass_sharp_entity_bridge", {}).get(entry.entry_id)) is not None:
        bridge.attach_platform("camera", entity_platform.async_get_current_platform(), HassSharpCamera)
