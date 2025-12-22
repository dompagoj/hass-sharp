from homeassistant import config_entries
import voluptuous as vol
from .const import DOMAIN, logger

DATA_SCHEMA = vol.Schema({
    vol.Required("username"): str,
    vol.Required("password"): str
})

class ExampleConfigFlow(config_entries.ConfigFlow, domain=DOMAIN):
    """Example config flow."""
    # The schema version of the entries that it creates
    # Home Assistant will call your migrate method if the version changes
    VERSION = 1
    MINOR_VERSION = 1

    async def async_step_user(self, info):
        if info is not None:
            logger.info('Created stuff')
            logger.info(info)
            return self.async_create_entry(
                title="Dompa Integration",
                data=info,
            )
            

        return self.async_show_form(
            step_id="user", data_schema=DATA_SCHEMA
        )