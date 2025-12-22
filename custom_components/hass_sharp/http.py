from .const import logger

from homeassistant.core import HomeAssistant
from homeassistant.helpers.http import HomeAssistantView

def register_http_routes(hass: HomeAssistant):
  logger.info("Registering views")
  hass.http.register_view(AutomationsView)

class AutomationsView(HomeAssistantView):
  url = '/api/hass-sharp/automations'
  name = 'api:hass-sharp:automations'

  async def get(self, request):
    return self.json({
       "hello": "world"
    })