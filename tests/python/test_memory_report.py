"""The memory line is a leak probe with its own switch: ordinary debug logging must not turn it on."""
import importlib.util
import logging
import pathlib
import sys
import types
import unittest
from unittest.mock import patch


class MemoryReport(unittest.TestCase):
    def setUp(self):
        package = types.ModuleType('hass_sharp_memory_test'); package.__path__ = []
        const = types.ModuleType('hass_sharp_memory_test.const'); const.logger = logging.getLogger('custom_components.HassSharp')
        # The stubs go when the test does (patch.dict restores sys.modules as it was).
        stubs = patch.dict(sys.modules, {package.__name__: package, const.__name__: const})
        stubs.start()
        self.addCleanup(stubs.stop)
        spec = importlib.util.spec_from_file_location('hass_sharp_memory_test.memory_report', pathlib.Path('custom_components/hass_sharp/memory_report.py'))
        self.report = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.report)
        self.parent, self.own = logging.getLogger('custom_components.HassSharp'), logging.getLogger('custom_components.HassSharp.memory')
        levels = self.parent.level, self.own.level
        self.addCleanup(lambda: (self.parent.setLevel(levels[0]), self.own.setLevel(levels[1])))

    def test_only_its_own_logger_at_debug_turns_it_on(self):
        self.assertIs(self.report.logger, self.own)
        self.parent.setLevel(logging.DEBUG)
        self.assertFalse(self.report.enabled())
        self.own.setLevel(logging.DEBUG)
        self.assertTrue(self.report.enabled())
        self.own.setLevel(logging.INFO)
        self.assertFalse(self.report.enabled())

    def test_its_module_stubs_are_removed_afterwards(self):
        self.assertIn('hass_sharp_memory_test.const', sys.modules)
        self.doCleanups()
        self.assertFalse([name for name in sys.modules if name.startswith('hass_sharp_memory_test')])


if __name__ == '__main__': unittest.main()
