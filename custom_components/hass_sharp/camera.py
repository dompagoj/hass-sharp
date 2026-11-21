"""The cameras of the .NET entity definitions; the entity bridge adds and updates them. Home Assistant asks for a
picture when someone looks, and .NET gives the camera's current frame as JPEG, making one first when the camera makes
its frames. A camera has no commands and streams nothing: Home Assistant's MJPEG stream asks for a frame every
`frame_interval` seconds, and scales a still picture itself when a size is asked for. Without that option the interval
is 1/30 s: frames come as fast as they are made, at most 30 a second, as a camera whose frames are pictures of another
camera waits for that camera's next picture."""
from homeassistant.components.camera import Camera
from homeassistant.helpers import entity_platform

from .entity_base import HassSharpEntity

# The seconds between frames when the definition leaves them out: as fast as the frames come, at most 30 a second.
FRAME_INTERVAL = 1 / 30


class HassSharpCamera(HassSharpEntity, Camera):
    # HassSharpEntity's initialiser calls Camera's (the next in the order of bases), which a camera needs. The content
    # type stays Camera's JPEG, and there are no supported features.

    def apply(self, description: dict, device: dict | None, definition_name: str) -> None:
        super().apply(description, device, definition_name)
        interval = (description.get("options") or {}).get("frame_interval")
        self._attr_frame_interval = interval if interval is not None else FRAME_INTERVAL

    async def async_camera_image(self, width: int | None = None, height: int | None = None) -> bytes | None:
        # No picture on an error: .NET has logged why.
        return await self._bridge.async_camera_image(self.unique_id)


async def async_setup_entry(hass, entry, async_add_entities) -> None:
    # No bridge when the entry's setup failed before it existed.
    if (bridge := hass.data.get("hass_sharp_entity_bridge", {}).get(entry.entry_id)) is not None:
        bridge.attach_platform("camera", entity_platform.async_get_current_platform(), HassSharpCamera)
