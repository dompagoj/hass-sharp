from .const import logger
from . import utils

from homeassistant.core import HomeAssistant
from homeassistant.helpers.http import HomeAssistantView

def register_http_routes(hass: HomeAssistant):
  logger.info("Registering views")
  hass.http.register_view(AutomationsView(hass))

class AutomationsView(HomeAssistantView):
  url = '/api/hass-sharp/automations'
  name = 'api:hass-sharp:automations'
  requires_auth = False

  def __init__(self, hass: HomeAssistant):
    self.hass = hass
    self.hass_sharp = utils.get_hass_sharp_manager(hass)

  async def get(self, request):
    user_files = await self.hass.async_add_executor_job(self.hass_sharp.GetUserFiles)

    user_files_py = [{
      "name": utils.get_file_name(f.FilePath),
      "filePath": f.FilePath,
      "scripts": [{
        "className": s.ClassName,
        "methods": [m.Name for m in s.Methods]
      } for s in f.Scripts]
    } for f in user_files]

    return self.json(user_files_py, 200)
