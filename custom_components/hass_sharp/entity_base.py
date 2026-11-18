# pyright: reportMissingImports=false, reportUnknownArgumentType=false, reportUnknownVariableType=false
"""The Home Assistant entity of one entity of a .NET definition; each platform module subclasses it.

The bridge (`entity_bridge.EntityBridge`) builds, updates and writes these on the event loop. The entity holds
nothing of its own: its metadata comes from the description .NET sends with each sync, and its state from the
snapshots .NET pushes.
"""
from homeassistant.const import EntityCategory
from homeassistant.helpers.entity import Entity


class HassSharpEntity(Entity):
    _attr_should_poll = False
    _attr_has_entity_name = True

    def __init__(self, bridge, description: dict, device: dict | None, definition_name: str):
        """`device` is the Home Assistant device info of the definition's device, or None for a standalone entity."""
        super().__init__()
        self._bridge = bridge
        self._attr_unique_id = description["uniqueId"]
        # .NET says when the definition runs: until its first snapshot the entity is unavailable, not unknown.
        self._attr_available = False
        self.apply(description, device, definition_name)

    @classmethod
    def refusal(cls, description: dict) -> str | None:
        """Why Home Assistant cannot show an entity so described, as a sentence naming it; None when it can.

        The bridge refuses a definition with such an entity before it changes anything. A platform overrides this for
        what .NET accepts and Home Assistant would fail on at every state write, such as a text pattern Python's `re`
        cannot read.
        """
        return None

    def apply(self, description: dict, device: dict | None, definition_name: str) -> None:
        """Take the metadata of a description; on the loop. The bridge writes the state afterwards.

        The name is always set (spike rule 5): the entity's own, else none on a device (the device's main entity
        takes the device's name) and the definition's name on a standalone entity. The device info counts only when
        the entity is added; the bridge moves a live entity between devices in the registry.
        """
        name = description.get("name")
        if name is None and device is None:
            name = definition_name
        self._attr_name = name
        self._attr_icon = description.get("icon")
        self._attr_device_class = description.get("deviceClass")
        category = description.get("entityCategory")
        # Home Assistant's registry takes the enum, not its text.
        self._attr_entity_category = EntityCategory(category) if category is not None else None
        self._attr_entity_registry_enabled_default = description.get("enabledByDefault") is not False
        self._attr_device_info = device

    def apply_state(self, snapshot: dict) -> None:
        """Take a snapshot .NET pushed: availability, the platform's values and the extra attributes."""
        self._attr_available = snapshot.get("available") is True
        self._attr_extra_state_attributes = dict(snapshot.get("attributes") or {})
        self.apply_values(snapshot.get("values") or {})

    def apply_values(self, values: dict) -> None:
        """Take the platform's own values, such as `is_on`; each platform overrides this."""

    async def command(self, name: str, **data) -> None:
        """Run a command, such as `turn_on`, with the definition's handler; raises HomeAssistantError if it fails."""
        await self._bridge.async_command(self.unique_id, name, data)
