# pyright: reportMissingImports=false, reportUnknownArgumentType=false, reportUnknownVariableType=false

import asyncio
from collections.abc import Callable
from typing import Protocol, TypeVar

from homeassistant.core import HomeAssistant

_T = TypeVar("_T")
_T_co = TypeVar("_T_co", covariant=True)


class DotNetTaskAwaiter(Protocol[_T_co]):
    def GetResult(self) -> _T_co: ...

    def OnCompleted(self, continuation: object) -> None: ...


class DotNetTask(Protocol[_T_co]):
    def GetAwaiter(self) -> DotNetTaskAwaiter[_T_co]: ...


async def async_await_dotnet_task(task: DotNetTask[_T]) -> _T:
    """Await a .NET Task without blocking a Python executor worker."""
    from System import Action

    loop = asyncio.get_running_loop()
    future: asyncio.Future[_T] = loop.create_future()

    def set_result(result: _T) -> None:
        if not future.done():
            future.set_result(result)

    def set_exception(error: BaseException) -> None:
        if not future.done():
            future.set_exception(error)

    def on_completed() -> None:
        try:
            result = task.GetAwaiter().GetResult()
        except BaseException as error:
            _ = loop.call_soon_threadsafe(set_exception, error)
        else:
            _ = loop.call_soon_threadsafe(set_result, result)

    task.GetAwaiter().OnCompleted(Action(on_completed))
    return await future


async def async_run_dotnet_task(
    hass: HomeAssistant,
    target: Callable[..., DotNetTask[_T]],
    *args: object,
) -> _T:
    """Start a .NET Task in HA's executor, then await it asynchronously."""
    task = await hass.async_add_executor_job(target, *args)
    return await async_await_dotnet_task(task)
