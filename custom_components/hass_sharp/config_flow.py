from homeassistant import config_entries
from typing_extensions import final, override

from .const import DOMAIN, logger


@final
class HassSharpConfigFlow(config_entries.ConfigFlow, domain=DOMAIN):
    """Example config flow."""

    # The schema version of the entries that it creates
    # Home Assistant will call your migrate method if the version changes
    VERSION = 1
    MINOR_VERSION = 1

    @override
    async def async_step_user(self, user_input=None):
        if user_input is not None:
            logger.info(user_input)
            return self.async_create_entry(
                title="Hass Sharp Integration",
                data=user_input,
            )

        return self.async_show_form(step_id="user")
