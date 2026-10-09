from homeassistant.core import HomeAssistant
from homeassistant.helpers.http import HomeAssistantView, Request

from . import dotnet_tasks, utils
from .const import logger


def register_http_routes(hass: HomeAssistant):
    logger.info("Registering views")
    hass.http.register_view(AutomationsView(hass))
    hass.http.register_view(AutomationByIdView(hass))


class AutomationsView(HomeAssistantView):
    url = "/api/hass-sharp/automations"
    name = "api:hass-sharp:automations"
    requires_auth = False

    def __init__(self, hass: HomeAssistant):
        self.hass = hass
        self.hass_sharp = utils.get_hass_sharp_manager(hass)

    async def get(self, _request):
        user_files = await self.hass.async_add_executor_job(
            self.hass_sharp.GetUserScripts
        )

        return self.json(user_files, 200)

    async def post(self, request: Request):
        try:
            data = await request.json()
        except (TypeError, ValueError):
            return self.json({"error": "Request body must be valid JSON"}, 400)

        if not isinstance(data, dict) or not isinstance(data.get("name"), str):
            return self.json({"error": "Automation name is required"}, 400)

        file_name, error = await dotnet_tasks.async_run_dotnet_task(
            self.hass,
            self.hass_sharp.CreateEmptyScript,
            data["name"],
        )

        if not file_name:
            return self.json({"error": str(error)}, 400)

        return self.json({"fileName": str(file_name).removesuffix(".cs")}, 201)


class AutomationByIdView(HomeAssistantView):
    url = "/api/hass-sharp/automations/{file_name}"
    name = "api:hass-sharp:automation-by-id"
    requires_auth = False

    def __init__(self, hass: HomeAssistant):
        self.hass = hass
        self.hass_sharp = utils.get_hass_sharp_manager(hass)

    async def get(self, _request, file_name: str):
        user_script = await self.hass.async_add_executor_job(
            self.hass_sharp.GetUserScript, file_name
        )

        if user_script is None:
            return self.json({"error": "Not found"}, 404)

        return self.json(
            {"fileName": user_script.FileName, "source": user_script.Source}, 200
        )

    async def post(self, request: Request, file_name: str):
        data = await request.json()
        (success, error) = await dotnet_tasks.async_run_dotnet_task(
            self.hass,
            self.hass_sharp.SaveScript,
            file_name,
            data["source"],
        )

        if not success:
            return self.json({"error": error}, 400)

        return self.json({"success": True}, 200)

    async def put(self, request: Request, file_name: str):
        if not file_name or file_name in (".", "..") or any(
            character in file_name for character in ("/", "\\", "\0")
        ):
            return self.json({"error": "Invalid automation file name"}, 400)

        try:
            data = await request.json()
        except (TypeError, ValueError):
            return self.json({"error": "Request body must be valid JSON"}, 400)

        if not isinstance(data, dict) or not isinstance(data.get("name"), str):
            return self.json({"error": "Automation name is required"}, 400)

        user_script = await self.hass.async_add_executor_job(
            self.hass_sharp.GetUserScript, file_name
        )
        if user_script is None:
            return self.json({"error": "Not found"}, 404)

        script_path = self.hass.config.path(
            "custom_components", "hass_sharp", "user_scripts", f"{file_name}.cs"
        )
        renamed_file_name, error = await dotnet_tasks.async_run_dotnet_task(
            self.hass, self.hass_sharp.RenameScript, script_path, data["name"]
        )
        if not renamed_file_name:
            return self.json({"error": str(error)}, 400)

        return self.json({"fileName": str(renamed_file_name)}, 200)

    async def delete(self, _request, file_name: str):
        if not file_name or file_name in (".", "..") or any(
            character in file_name for character in ("/", "\\", "\0")
        ):
            return self.json({"error": "Invalid automation file name"}, 400)

        try:
            user_script = await self.hass.async_add_executor_job(
                self.hass_sharp.GetUserScript, file_name
            )
            if user_script is None:
                return self.json({"error": "Not found"}, 404)

            script_path = self.hass.config.path(
                "custom_components", "hass_sharp", "user_scripts", f"{file_name}.cs"
            )
            await self.hass.async_add_executor_job(
                self.hass_sharp.DeleteScript, script_path
            )
        except Exception as error:
            logger.exception("Failed to delete automation %s", file_name)
            return self.json({"error": str(error)}, 500)

        return self.json({"success": True}, 200)
