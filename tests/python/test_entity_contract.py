"""The platform modules against the catalog's entityDomains, which is the contract .NET, the graph and the panel share.

Runs without Home Assistant: the modules are read as source, not imported. For every domain of the catalog there is a
platform module that attaches itself under that domain and is in PLATFORMS, in the catalog's order; the commands it
sends are exactly the domain's commands, and every data value it sends is one the command names. With Home Assistant,
also that the validators' fixture refuses a Device node's configuration URL exactly when its device registry would.
"""
import ast
import importlib.util
import json
import os
import pathlib
import unittest

ROOT = pathlib.Path(os.environ.get('HASS_SHARP_TEST_ROOT', pathlib.Path(__file__).resolve().parents[2]))
INTEGRATION = ROOT / 'custom_components/hass_sharp'
DOMAINS = json.loads((INTEGRATION / 'visual_node_catalog.json').read_text())['entityDomains']


def platforms():
    """The PLATFORMS list of const.py."""
    tree = ast.parse((INTEGRATION / 'const.py').read_text())
    return next(ast.literal_eval(n.value) for n in tree.body if isinstance(n, ast.Assign)
                and any(isinstance(t, ast.Name) and t.id == 'PLATFORMS' for t in n.targets))


def calls(tree, method):
    """Every call of `<something>.<method>(...)` in the tree."""
    return [n for n in ast.walk(tree) if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute)
            and n.func.attr == method]


def sent(domain):
    """The commands the platform module sends, each with the data names it sends with it."""
    tree = ast.parse((INTEGRATION / f'{domain}.py').read_text())
    commands = {}
    for call in calls(tree, 'command'):
        name = call.args[0] if call.args else None
        if not (isinstance(name, ast.Constant) and isinstance(name.value, str)) or len(call.args) != 1:
            raise AssertionError(f'{domain}.py line {call.lineno}: a command is sent by a name written out, '
                                 'and its data by keyword')
        keywords = [k.arg for k in call.keywords]
        if None in keywords:
            raise AssertionError(f'{domain}.py line {call.lineno}: data is sent by name, not with **')
        commands.setdefault(name.value, set()).update(keywords)
    return commands


class EntityContractTest(unittest.TestCase):
    def test_every_domain_has_a_platform_in_the_catalog_order(self):
        self.assertEqual(platforms(), list(DOMAINS))

    def test_each_platform_attaches_under_its_own_domain(self):
        for domain in DOMAINS:
            with self.subTest(domain):
                tree = ast.parse((INTEGRATION / f'{domain}.py').read_text())
                attached = [c.args[0].value for c in calls(tree, 'attach_platform')]
                self.assertEqual(attached, [domain])

    def test_each_platform_sends_its_domains_commands_and_their_data(self):
        for domain, row in DOMAINS.items():
            with self.subTest(domain):
                commands = sent(domain)
                self.assertEqual(set(commands), set(row['commands']))
                for command, data in commands.items():
                    self.assertLessEqual(data, set(row['commands'][command].get('data', {})), command)

    def test_a_command_that_names_data_is_sent_with_it(self):
        # A data value Home Assistant always gives (a number's value) must reach .NET; one it may leave out (a
        # light's brightness) is sent only when given, which the module does in a call of its own.
        for domain, row in DOMAINS.items():
            for command, spec in row['commands'].items():
                required = {n for n, value in spec.get('data', {}).items() if value.get('required') is not False}
                with self.subTest(f'{domain}.{command}'):
                    self.assertLessEqual(required, sent(domain).get(command, set()))


# Home Assistant's own check of a device's configuration URL; run these inside the HA container or a venv with it.
@unittest.skipIf(importlib.util.find_spec('homeassistant') is None,
                 'Home Assistant is not installed; run these inside the Home Assistant container')
class ConfigurationUrlTests(unittest.TestCase):
    """The validators refuse a Device node's configuration URL exactly when Home Assistant's device registry would."""

    def test_the_fixture_cases_refuse_what_home_assistant_refuses(self):
        from homeassistant.helpers import device_registry

        # As _validate_device_info_fields checks a configuration_url given as text: its scheme one of
        # CONFIGURATION_URL_SCHEMES and a host, parsed by _cached_parse_url (which raises for an address it cannot read).
        def taken(url):
            try:
                parsed = device_registry._cached_parse_url(url)
            except ValueError:
                return False
            return parsed.scheme in device_registry.CONFIGURATION_URL_SCHEMES and bool(parsed.host)

        cases = json.loads((ROOT / 'tests/fixtures/visual-graph-validation.json').read_text())
        checked = 0
        for case in cases:
            urls = [node['config']['configurationUrl'] for node in case['graph'].get('nodes', [])
                    if isinstance(node, dict) and node.get('kind') == 'device' and isinstance(node.get('config'), dict)
                    and isinstance(node['config'].get('configurationUrl'), str)]
            refused = 'device.configurationUrl' in case.get('codes', [])
            # A case refuses one URL alone, or none of them.
            for url in urls:
                with self.subTest(case=case['name'], url=url):
                    self.assertEqual(taken(url), not refused)
                    checked += 1
        self.assertGreaterEqual(checked, 8)


if __name__ == '__main__':
    unittest.main()
