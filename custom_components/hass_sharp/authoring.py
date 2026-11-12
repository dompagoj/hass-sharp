"""Shared administrator-only authoring boundary for WebSocket and future MCP adapters."""
import json

from . import dotnet_tasks


class AuthoringService:
    def __init__(self, hass, manager):
        self.hass = hass
        self.manager = manager

    async def _request(self, method, request, *, is_admin):
        if not is_admin:
            raise PermissionError("Administrator required")
        if not isinstance(request, dict):
            raise ValueError("Authoring request must be an object")
        result = await dotnet_tasks.async_run_dotnet_task(self.hass, method, json.dumps(request))
        return json.loads(result)

    async def visual(self, request, *, is_admin):
        return await self._request(self.manager.VisualRequest, request, is_admin=is_admin)

    async def node_library(self, request, *, is_admin):
        return await self._request(self.manager.NodeLibraryRequest, request, is_admin=is_admin)
