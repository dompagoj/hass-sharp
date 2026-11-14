"""Awaiting a .NET Task from Python, with fakes for the Task and System.Action (no Python.NET in this process)."""
import ast
import asyncio
import pathlib
import sys
import threading
import types
import unittest
from unittest.mock import patch


def load():
    """The module without its Home Assistant import (used for an annotation only)."""
    tree = ast.parse(pathlib.Path('custom_components/hass_sharp/dotnet_tasks.py').read_text())
    tree.body = [n for n in tree.body if not (isinstance(n, ast.ImportFrom) and n.module == 'homeassistant.core')]
    module = types.ModuleType('dotnet_tasks')
    module.HomeAssistant = object
    exec(compile(tree, '<dotnet_tasks>', 'exec'), module.__dict__)
    return module


SYSTEM = patch.dict(sys.modules, {'System': types.SimpleNamespace(Action=lambda fn: fn)})


class FakeTask:
    """Completes from another thread, as a .NET Task does from its pool."""
    def __init__(self, result=None, error=None):
        self.result, self.error, self.continuation = result, error, None
    def GetAwaiter(self):
        return self
    def OnCompleted(self, continuation):
        self.continuation = continuation
    def GetResult(self):
        if self.error is not None:
            raise self.error
        return self.result
    def complete_on_pool_thread(self):
        errors = []
        def run():
            try:
                self.continuation()
            except BaseException as error:
                errors.append(error)
        thread = threading.Thread(target=run); thread.start(); thread.join()
        return errors


@SYSTEM
class DotNetTasks(unittest.TestCase):
    def test_completion_after_the_loop_closed_raises_nothing_on_the_pool_thread(self):
        tasks = load()
        for task in (FakeTask(result='done'), FakeTask(error=RuntimeError('failed in .NET'))):
            loop = asyncio.new_event_loop()
            waiting = loop.create_task(tasks.async_await_dotnet_task(task))
            loop.run_until_complete(asyncio.sleep(0))
            waiting.cancel()
            loop.run_until_complete(asyncio.gather(waiting, return_exceptions=True))
            loop.close()
            # An exception escaping into a .NET pool thread would end the process.
            self.assertEqual(task.complete_on_pool_thread(), [])

    def test_completion_resolves_the_awaiting_coroutine(self):
        tasks = load()
        async def main():
            task = FakeTask(result=42)
            waiting = asyncio.create_task(tasks.async_await_dotnet_task(task))
            await asyncio.sleep(0)
            self.assertEqual(task.complete_on_pool_thread(), [])
            return await waiting
        self.assertEqual(asyncio.run(main()), 42)


if __name__ == '__main__': unittest.main()
