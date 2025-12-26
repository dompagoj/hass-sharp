from .const import logger
from . import utils

from homeassistant.core import HomeAssistant
from homeassistant.helpers.http import HomeAssistantView

def register_http_routes(hass: HomeAssistant):
  logger.info("Registering views")
  hass.http.register_view(AutomationsView(hass))
  hass.http.register_view(AutomationByIdView(hass))

class AutomationsView(HomeAssistantView):
  url = '/api/hass-sharp/automations'
  name = 'api:hass-sharp:automations'
  requires_auth = False

  def __init__(self, hass: HomeAssistant):
    self.hass = hass
    self.hass_sharp = utils.get_hass_sharp_manager(hass)

  async def get(self, request):
    user_files = await self.hass.async_add_executor_job(self.hass_sharp.GetUserScripts)

    logger.info("user files: %s", user_files)

    return self.json(user_files, 200)


class AutomationByIdView(HomeAssistantView):
  url = '/api/hass-sharp/automations/{fileName}'
  name = 'api:hass-sharp:automation-by-id'
  requires_auth = False

  def __init__(self, hass: HomeAssistant):
    self.hass = hass
    self.hass_sharp = utils.get_hass_sharp_manager(hass)

  async def get(self, _request, fileName: str):
    user_script = await self.hass.async_add_executor_job(self.hass_sharp.GetUserScript, fileName)

    if user_script is None:
      return self.json({"error": "Not found"}, 404)

    return self.json({
      "fileName": user_script.FileName,
      "source": user_script.Source
    }, 200)

