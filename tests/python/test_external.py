import asyncio
import copy
import importlib.util
import json
import os
import pathlib
import unittest
from types import SimpleNamespace as NS
from urllib.parse import unquote

from test_visual import EntryState, load_routes

ROOT = pathlib.Path(os.environ.get('HASS_SHARP_TEST_ROOT', pathlib.Path(__file__).resolve().parents[2]))
spec = importlib.util.spec_from_file_location('hass_sharp_external', ROOT / 'custom_components/hass_sharp/external.py')
x = importlib.util.module_from_spec(spec)
spec.loader.exec_module(x)

SHA = 'a' * 40
INVALID_URL = "Paste a GitHub repository's address, such as https://github.com/owner/repo or https://github.com/owner/repo/tree/main"
LIMIT = 'GitHub refused the request: it allows 60 requests an hour without signing in; try again later'


def spec_manifest() -> str:
    """hass-sharp-vision's manifest, as tests/fixtures/hass_sharp.toml copies it (the docs quote the same text)."""
    return (ROOT / 'tests/fixtures/hass_sharp.toml').read_text(encoding='utf-8')


def error(test, code, call):
    with test.assertRaises(x.ExternalError) as raised:
        call()
    test.assertEqual(raised.exception.code, code)
    return raised.exception.message


def run(coroutine):
    return asyncio.run(coroutine)


class Urls(unittest.TestCase):
    def test_url_forms(self):
        for url in ('https://github.com/o/r', 'https://github.com/o/r/', 'https://github.com/o/r.git',
                    'https://www.github.com/o/r', '  https://github.com/o/r?tab=readme-ov-file#readme '):
            self.assertEqual(x.parse_url(url), x.Repo('o', 'r', None), url)
        self.assertEqual(x.parse_url('https://github.com/o/r/tree/main'), x.Repo('o', 'r', 'main'))
        self.assertEqual(x.parse_url('https://github.com/o/r/tree/feature/x'), x.Repo('o', 'r', 'feature/x'))
        self.assertEqual(x.parse_url('https://github.com/dompagoj/hass-sharp-vision'), x.Repo('dompagoj', 'hass-sharp-vision', None))
        self.assertEqual(x.parse_url('https://github.com/o/r/tree/v1.2/'), x.Repo('o', 'r', 'v1.2'))
        self.assertEqual(x.parse_url('https://github.com/o/r/tree/caf%C3%A9'), x.Repo('o', 'r', 'café'))
        for url in ('http://github.com/o/r', 'https://gitlab.com/o/r', 'https://github.com/o',
                    'https://github.com/o/r/blob/main/x.cs', 'https://github.com/o/r/issues', 'https://github.com/o/r/tree/',
                    'https://github.com/-o/r', 'https://github.com/o/..', 'https://github.com/o/.', 'https://github.com:8443/o/r',
                    'https://user@github.com/o/r', '', 'github.com/o/r', 'https://github.com/o/r/tree/a/../b',
                    'https://github.com/o/r/tree/a//b', 'https://github.com/o/r/tree/..'):
            self.assertEqual(error(self, 'invalid_request', lambda: x.parse_url(url)), INVALID_URL, url)

    def test_error_answer(self):
        self.assertEqual(x.ExternalError('not_found', 'gone').answer(),
                         {'version': 1, 'ok': False, 'error': {'code': 'not_found', 'detail': 'gone'}})


class Resolving(unittest.TestCase):
    def github(self, answer):
        asked = []

        async def fetch(url, accept):
            asked.append((url, accept))
            if isinstance(answer, BaseException):
                raise answer
            return answer
        return x.GitHub(fetch), asked

    def test_resolve_ref(self):
        sha = '0123456789abcdef0123456789abcdef01234567'
        # GitHub answers the bare sha for this media type (40 bytes), not the commit with every file's patch.
        github, asked = self.github(sha.encode())
        self.assertEqual(run(github.resolve(x.Repo('o', 'r', 'feature/x'))), sha)
        self.assertEqual(run(github.resolve(x.Repo('o', 'r', None))), sha)
        self.assertEqual(asked, [('https://api.github.com/repos/o/r/commits/feature/x', 'application/vnd.github.sha'),
                                 ('https://api.github.com/repos/o/r/commits/HEAD', 'application/vnd.github.sha')])
        github, _ = self.github(f' {sha}\n'.encode())
        self.assertEqual(run(github.resolve(x.Repo('o', 'r', None))), sha)

        for status in (404, 422):
            github, _ = self.github(x.HttpStatus(status))
            self.assertEqual(error(self, 'not_found', lambda: run(github.resolve(x.Repo('o', 'r', 'feature/x')))),
                             'GitHub has no public repository o/r with a branch, tag or commit feature/x')
            self.assertEqual(error(self, 'not_found', lambda: run(github.resolve(x.Repo('o', 'r', None)))),
                             'GitHub has no public repository o/r')
        github, _ = self.github(x.HttpStatus(409))
        self.assertEqual(error(self, 'not_found', lambda: run(github.resolve(x.Repo('o', 'r', None)))),
                         "GitHub's repository o/r has no commits yet")
        for status in (403, 429):
            github, _ = self.github(x.HttpStatus(status))
            self.assertEqual(error(self, 'fetch', lambda: run(github.resolve(x.Repo('o', 'r', None)))), LIMIT)
        github, _ = self.github(x.HttpStatus(500))
        self.assertTrue(error(self, 'fetch', lambda: run(github.resolve(x.Repo('o', 'r', None)))).startswith('Could not reach GitHub: '))
        for body in (b'abc', b'A' * 40, b'{"sha": "%s"}' % sha.encode(), sha.encode() + b'0', b'', b'\xff' * 40):
            github, _ = self.github(body)
            self.assertTrue(error(self, 'fetch', lambda: run(github.resolve(x.Repo('o', 'r', None)))).startswith('Could not reach GitHub: '), body)

    def test_file(self):
        github, asked = self.github(b'class A {}')
        self.assertEqual(run(github.file('o', 'r', SHA, 'nodes/Vision.cs')), b'class A {}')
        self.assertEqual(run(github.file('o', 'r', SHA, 'my nodes/a#b.cs')), b'class A {}')
        self.assertEqual([url for url, _ in asked], [f'https://raw.githubusercontent.com/o/r/{SHA}/nodes/Vision.cs',
                                                     f'https://raw.githubusercontent.com/o/r/{SHA}/my%20nodes/a%23b.cs'])
        github, _ = self.github(x.HttpStatus(404))
        with self.assertRaises(x.HttpStatus) as raised:
            run(github.file('o', 'r', SHA, 'nodes/Vision.cs'))
        self.assertEqual(raised.exception.status, 404)
        # The raw host's refusal is not the API's limit of 60 requests an hour.
        for status in (403, 429):
            github, _ = self.github(x.HttpStatus(status))
            self.assertEqual(error(self, 'fetch', lambda: run(github.file('o', 'r', SHA, 'nodes/a.cs'))),
                             'GitHub refused to send nodes/a.cs: try again later')
        github, _ = self.github(x.HttpStatus(500))
        self.assertEqual(error(self, 'fetch', lambda: run(github.file('o', 'r', SHA, 'a.cs'))),
                         'Could not reach GitHub: it answered with HTTP status 500')


class Manifest(unittest.TestCase):
    def test_manifest(self):
        items = x.parse_manifest(spec_manifest().encode())
        self.assertEqual(items, [
            x.Item('library', 'Vision.cs', 'nodes/Vision.cs',
                   'Face and person detection on a native library (YuNet and YOLOX-nano on tract), and the Detect faces node', 'vision'),
            x.Item('library', 'PeopleDetector.cs', 'nodes/PeopleDetector.cs',
                   'People detector: a device that watches a camera and reports faces and people seen', 'people_detector'),
            x.Item('entity', 'Front door people', 'nodes/front_door_people.json', 'A People detector on camera.front_door', None),
        ])

        self.assertTrue(error(self, 'manifest', lambda: x.parse_manifest(b'[[library]\nname = ')).startswith('hass_sharp.toml is not valid TOML: '))
        self.assertEqual(error(self, 'manifest', lambda: x.parse_manifest(b'\xff\xfe')), 'hass_sharp.toml is not UTF-8 text')
        for depth in (200_000, 2_000):
            self.assertEqual(error(self, 'manifest', lambda: x.parse_manifest(b'a = ' + b'[' * depth + b']' * depth)),
                             'hass_sharp.toml is not valid TOML: it nests arrays or tables too deeply')

        # Entities listed before libraries still come after them; unknown keys, here and at the top, are ignored.
        items = x.parse_manifest(b'version = 3\n[[entity]]\nname = "E"\npath = "e.json"\nicon = "mdi:eye"\n'
                                 b'[[library]]\nname = "A.cs"\npath = "A.cs"\nfuture = true\n')
        self.assertEqual(items, [x.Item('library', 'A.cs', 'A.cs', '', None), x.Item('entity', 'E', 'e.json', '', None)])
        self.assertEqual(x.parse_manifest(b'title = "nothing yet"\n'), [])

        bad = '\n'.join(f'[[library]]\n{body}' for body in (
            'name = "1.cs"\npath = "/abs.cs"', 'name = "2.cs"\npath = "a/../b.cs"', 'name = "3.cs"\npath = \'a\\b.cs\'',
            'name = "4.cs"\npath = "./a.cs"', 'name = "5.cs"\npath = "a//b.cs"', 'name = "6.cs"\npath = "a.CS"',
            'name = "7.cs"\npath = "a.json"', 'path = "eight.cs"', f'name = "{"n" * 121}"\npath = "nine.cs"',
            'name = "ten/x.cs"\npath = "ten.cs"', 'name = "11.cs"\npath = "eleven.cs"\nid = "Bad"',
            'name = "12.cs"\npath = "twelve.cs"\nid = "same"', 'name = "13.cs"\npath = "twelve.cs"',
            'name = "12.cs"\npath = "fourteen.cs"', 'name = "15.cs"\npath = "fifteen.cs"\nid = "same"',
            'name = "16.cs"\npath = "sixteen.cs"\ndescription = 3')) + (
            '\n[[entity]]\nname = "E"\npath = "e.cs"\n[[entity]]\nname = "  "\npath = "e.json"\n')
        message = error(self, 'manifest', lambda: x.parse_manifest(bad.encode()))
        self.assertTrue(message.startswith('hass_sharp.toml: '), message)
        cs = 'path must be a relative path inside the repository to a .cs file'
        problems = message.removeprefix('hass_sharp.toml: ').split('; ')
        self.assertEqual(problems[:7], [f'library {n}: {cs}' for n in range(1, 8)])
        for expected in ('library 8: name', 'library 9: name', 'library 10: name', 'library 11: id', 'library 13: path twelve.cs',
                         'library 14: name 12.cs', 'library 15: id same', 'library 16: description',
                         'entity 1: path must be a relative path inside the repository to a .json file', 'entity 2: name'):
            self.assertTrue(any(p.startswith(expected) for p in problems), expected)
        self.assertEqual(len(problems), 17, problems)
        self.assertFalse(any(p.startswith('library 12') for p in problems))

        many = ''.join(f'[[library]]\nname = "L{n}.cs"\npath = "L{n}.cs"\n' for n in range(26)) + \
            ''.join(f'[[entity]]\nname = "E{n}"\npath = "E{n}.json"\n' for n in range(25))
        self.assertEqual(error(self, 'manifest', lambda: x.parse_manifest(many.encode())), 'hass_sharp.toml lists more than 50 items')
        self.assertEqual(len(x.parse_manifest(many.replace('[[library]]\nname = "L25.cs"\npath = "L25.cs"\n', '').encode())), 50)

        message = error(self, 'manifest', lambda: x.parse_manifest(b'library = "Vision.cs"\n[entity]\nname = "E"\n'))
        self.assertIn('library must be', message)
        self.assertIn('entity must be', message)
        self.assertIn('library 1: must be a table', error(self, 'manifest', lambda: x.parse_manifest(b'library = [1]\n')))

    def test_check_path(self):
        for path, kind in (('a.cs', 'library'), ('nodes/Vision.cs', 'library'), ('nodes/front_door_people.json', 'entity'),
                           ('a b/c-d.e.cs', 'library'), ('.hidden/x.cs', 'library')):
            self.assertIsNone(x.check_path(path, kind), path)
        for path, kind in (('', 'library'), ('/a.cs', 'library'), ('a/', 'library'), ('a/./b.cs', 'library'), ('..', 'library'),
                           ('a\\b.cs', 'library'), ('a.cs', 'entity'), ('a.json', 'library'), ('a.Json', 'entity'),
                           ('a\nb.cs', 'library'), (3, 'library')):
            self.assertIsNotNone(x.check_path(path, kind), path)
        self.assertEqual(x.check_path('a.cs', 'entity'), 'path must be a relative path inside the repository to a .json file')


class Ids(unittest.TestCase):
    def test_definition_id_matches_frontend(self):
        # FE/tests/entity-model.test.ts, "definition ids".
        d = x.definition_id
        self.assertEqual(d('Any motion', []), 'any_motion')
        self.assertEqual(d('  Kitchen -- Plug!! ', []), 'kitchen_plug')
        self.assertEqual(d('__Hall__', []), 'hall')
        self.assertEqual(d('Plug 2', []), 'plug_2')
        self.assertEqual(d('', []), 'entity')
        self.assertEqual(d('!!!', []), 'entity')
        self.assertEqual(d('Café', []), 'cafe')
        self.assertEqual(d('Čajna kuhinja — Žarulja', []), 'cajna_kuhinja_zarulja')
        self.assertEqual(d('Ølstue', []), 'lstue')
        self.assertEqual(d('a' * 100, []), 'a' * 80)
        self.assertEqual(d('a' * 79 + ' b', []), 'a' * 79)
        self.assertEqual(d('New', []), 'new_2')
        self.assertEqual(d('  new!! ', ['new_2']), 'new_3')
        self.assertEqual(d('Newer', []), 'newer')
        self.assertEqual(d('Any motion', ['any_motion']), 'any_motion_2')
        self.assertEqual(d('Any motion', ['any_motion', 'any_motion_2']), 'any_motion_3')
        self.assertEqual(d('', ['entity']), 'entity_2')
        self.assertEqual(d('a' * 100, ['a' * 80]), 'a' * 78 + '_2')
        taken = ['a' * 80, *('a' * 78 + f'_{i + 2}' for i in range(8)), 'a' * 77 + '_10']
        self.assertEqual(d('a' * 100, taken), 'a' * 77 + '_11')
        self.assertEqual(d('Any motion', iter(['any_motion'])), 'any_motion_2')
        for id in (d('Any motion', []), d('a' * 100, taken), d('-x-', [])):
            self.assertRegex(id, r'^[a-z0-9][a-z0-9_-]{0,79}\Z')

    def test_rewrite_library_kinds(self):
        detect = 'library:vision:global%3A%3ANodeLib.FaceDetection.DetectFaces'
        device = {'id': 'device', 'kind': 'csharp_device', 'config': {'source': 'public class FrontDoorPeople : PeopleDetector { }'}}
        graph = {'nodes': [{'id': 'faces', 'kind': detect, 'config': {}},
                           {'id': 'other', 'kind': 'library:other:global%3A%3ANodeLib.X.Y', 'config': {}},
                           {'id': 'frame', 'kind': 'camera_frame', 'config': {'entity': 'camera.front_door'}},
                           device],
                 'edges': [{'source': 'frame', 'target': 'faces'}]}
        before = repr(graph)
        rewritten = x.rewrite_library_kinds(graph, {'vision': 'library-1234'})
        self.assertEqual(repr(graph), before)
        self.assertEqual([n['kind'] for n in rewritten['nodes']],
                         ['library:library-1234:global%3A%3ANodeLib.FaceDetection.DetectFaces',
                          'library:other:global%3A%3ANodeLib.X.Y', 'camera_frame', 'csharp_device'])
        self.assertEqual(rewritten['nodes'][3], device)
        self.assertEqual(rewritten['edges'], graph['edges'])
        rewritten['nodes'][2]['config']['entity'] = 'changed'
        self.assertEqual(graph['nodes'][2]['config']['entity'], 'camera.front_door')
        self.assertEqual(x.rewrite_library_kinds({'edges': []}, {'vision': 'v'}), {'edges': []})


class Items(unittest.TestCase):
    entity = x.Item('entity', 'Front door people', 'nodes/front_door_people.json', '', None)
    library = x.Item('library', 'Vision.cs', 'nodes/Vision.cs', '', 'vision')

    def problem(self, call):
        with self.assertRaises(x.ItemProblem) as raised:
            call()
        return str(raised.exception)

    def test_entity_body(self):
        logic = {'kind': 'graph', 'graph': {'nodes': [], 'edges': []}}
        answer = b'{"id": "front_door_people", "revision": 4, "name": "Old name", "settings": [{"key": "k"}], "logic": %s, ' \
                 b'"entities": [], "devices": [], "source": "class X {}"}' % json.dumps(logic).encode()
        self.assertEqual(x.entity_body(self.entity, answer),
                         {'name': 'Front door people', 'settings': [{'key': 'k'}], 'logic': logic})
        self.assertEqual(x.entity_body(self.entity, b'{"logic": {"kind": "graph", "graph": {}}, "settings": null}'),
                         {'name': 'Front door people', 'settings': [], 'logic': {'kind': 'graph', 'graph': {}}})
        self.assertEqual(x.entity_body(self.entity, '﻿{"logic": {"kind": "graph", "graph": {}}}'.encode()),
                         {'name': 'Front door people', 'settings': [], 'logic': {'kind': 'graph', 'graph': {}}})
        wrong = 'nodes/front_door_people.json is not an entity definition: it needs logic of kind graph'
        for body in (b'{"name": "x"}', b'{"logic": {"kind": "class", "source": ""}}', b'{"logic": {"kind": "graph"}}',
                     b'{"logic": {"kind": "graph", "graph": []}}', b'{"logic": "graph"}', b'[]', b'3'):
            self.assertEqual(self.problem(lambda: x.entity_body(self.entity, body)), wrong, body)
        self.assertTrue(self.problem(lambda: x.entity_body(self.entity, b'{"logic": ')).startswith(
            'nodes/front_door_people.json is not valid JSON: '))
        self.assertEqual(self.problem(lambda: x.entity_body(self.entity, b'\xff')), 'nodes/front_door_people.json is not UTF-8 text')

    def test_deep_entity_file(self):
        deep = 'nodes/front_door_people.json is not valid JSON: it nests more than 64 levels deep'
        # Too deep for the parser, and parsed but too deep to copy (rewrite_library_kinds) or for .NET's 64 levels.
        for depth in (200_000, 1_000, 63):
            body = b'{"logic": {"kind": "graph", "graph": {"nodes": %s}}}' % (b'[' * depth + b']' * depth)
            self.assertEqual(self.problem(lambda: x.entity_body(self.entity, body)), deep, depth)
        body = b'{"logic": {"kind": "graph", "graph": {"nodes": %s}}}' % (b'[' * 61 + b']' * 61)
        self.assertEqual(x.rewrite_library_kinds(x.entity_body(self.entity, body)['logic']['graph'], {})['nodes'],
                         json.loads(b'[' * 61 + b']' * 61))

    def test_library_source(self):
        self.assertEqual(x.library_source(self.library, 'namespace NodeLib; // Café'.encode()), 'namespace NodeLib; // Café')
        self.assertEqual(self.problem(lambda: x.library_source(self.library, b'class \xc3')), 'nodes/Vision.cs is not UTF-8 text')


class FakeContent:
    def __init__(self, body):
        self.body, self.read = body, 0

    async def iter_chunked(self, size):
        for start in range(0, len(self.body), size):
            chunk = self.body[start:start + size]
            self.read += len(chunk)
            yield chunk


class FakeResponse:
    def __init__(self, body=b'', status=200, headers=None):
        self.status, self.headers, self.content = status, headers or {}, FakeContent(body)

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        return False


class FakeSession:
    """Answers each request with `answer`, or, for a dict, with the answer for its URL."""

    def __init__(self, answer):
        self.answer, self.calls = answer, []

    def get(self, url, **kwargs):
        self.calls.append((url, kwargs))
        answer = self.answer[url] if isinstance(self.answer, dict) else self.answer
        if isinstance(answer, BaseException):
            raise answer
        return answer


def moved(location, status=302):
    return FakeResponse(b'moved', status=status, headers={'Location': location})


class Fetching(unittest.TestCase):
    url = f'https://raw.githubusercontent.com/o/r/{SHA}/nodes/x.cs'

    def fetch(self, answer):
        session = FakeSession(answer)
        return session, x.aiohttp_fetch(session, 'the timeout')

    def test_aiohttp_fetch_caps(self):
        whole = FakeResponse(b'a' * x.CAP)
        session, fetch = self.fetch(whole)
        self.assertEqual(run(fetch(self.url, 'text/plain')), b'a' * x.CAP)
        self.assertEqual(session.calls, [(self.url, {'headers': {'Accept': 'text/plain', 'User-Agent': 'HassSharp'},
                                                     'timeout': 'the timeout', 'allow_redirects': False})])

        over = FakeResponse(b'a' * (x.CAP + 1024 * 1024))
        _, fetch = self.fetch(over)
        self.assertEqual(error(self, 'fetch', lambda: run(fetch(self.url, '*/*'))), 'x.cs is larger than 2 MiB')
        self.assertLessEqual(over.content.read, x.CAP + 64 * 1024)
        _, fetch = self.fetch(FakeResponse(b'a' * (x.CAP + 1)))
        self.assertEqual(error(self, 'fetch', lambda: run(fetch(self.url, '*/*'))), 'x.cs is larger than 2 MiB')

        declared = FakeResponse(b'a', headers={'Content-Length': str(x.CAP + 1)})
        _, fetch = self.fetch(declared)
        self.assertEqual(error(self, 'fetch', lambda: run(fetch(self.url, '*/*'))), 'x.cs is larger than 2 MiB')
        self.assertEqual(declared.content.read, 0)

        _, fetch = self.fetch(FakeResponse(b'gone', status=404))
        with self.assertRaises(x.HttpStatus) as raised:
            run(fetch(self.url, '*/*'))
        self.assertEqual(raised.exception.status, 404)

        _, fetch = self.fetch(asyncio.TimeoutError())
        self.assertTrue(error(self, 'fetch', lambda: run(fetch(self.url, '*/*'))).startswith('Could not reach GitHub: '))

        class ClientError(Exception): pass
        class ClientConnectorError(ClientError): pass
        _, fetch = self.fetch(ClientConnectorError('Cannot connect to host raw.githubusercontent.com:443'))
        self.assertEqual(error(self, 'fetch', lambda: run(fetch(self.url, '*/*'))),
                         'Could not reach GitHub: Cannot connect to host raw.githubusercontent.com:443')
        _, fetch = self.fetch(OSError('Network is unreachable'))
        self.assertEqual(error(self, 'fetch', lambda: run(fetch(self.url, '*/*'))), 'Could not reach GitHub: Network is unreachable')
        # Anything else is HassSharp's own defect, not the network's, and is not dressed up as one.
        _, fetch = self.fetch(KeyError('x'))
        with self.assertRaises(KeyError):
            run(fetch(self.url, '*/*'))


    def test_aiohttp_fetch_redirects(self):
        api = 'https://api.github.com/repos/o/r/commits/HEAD'
        renamed = 'https://api.github.com/repositories/42/commits/HEAD'
        session, fetch = self.fetch({api: moved(renamed, 301), renamed: moved('/repositories/42/commits/main', 307),
                                     'https://api.github.com/repositories/42/commits/main': moved(self.url),
                                     self.url: FakeResponse(b'{}')})
        self.assertEqual(run(fetch(api, 'application/vnd.github+json')), b'{}')
        self.assertEqual([url for url, _ in session.calls],
                         [api, renamed, 'https://api.github.com/repositories/42/commits/main', self.url])
        self.assertTrue(all(kwargs['allow_redirects'] is False for _, kwargs in session.calls))

        # A hop off GitHub's hosts is refused before it is requested, wherever the chain would end.
        for location, host in (('https://example.com/x.cs', 'example.com'), ('//169.254.169.254/latest', '169.254.169.254'),
                               ('https://raw.githubusercontent.com:8443/x.cs', 'raw.githubusercontent.com:8443'),
                               ('https://user@raw.githubusercontent.com/x.cs', 'user@raw.githubusercontent.com')):
            session, fetch = self.fetch({api: moved(location), 'https://example.com/x.cs': moved(self.url)})
            self.assertEqual(error(self, 'fetch', lambda: run(fetch(api, '*/*'))), f'Could not reach GitHub: it sent the request to {host}')
            self.assertEqual([url for url, _ in session.calls], [api])
        session, fetch = self.fetch({api: moved('http://raw.githubusercontent.com/o/r/x.cs')})
        self.assertEqual(error(self, 'fetch', lambda: run(fetch(api, '*/*'))),
                         'Could not reach GitHub: it sent the request to raw.githubusercontent.com without https')
        self.assertEqual(len(session.calls), 1)
        _, fetch = self.fetch({})
        self.assertEqual(error(self, 'fetch', lambda: run(fetch('https://example.com/x.cs', '*/*'))),
                         'Could not reach GitHub: it sent the request to example.com')

        loop = 'https://raw.githubusercontent.com/loop'
        session, fetch = self.fetch(moved(loop))
        self.assertEqual(error(self, 'fetch', lambda: run(fetch(loop, '*/*'))),
                         f'Could not reach GitHub: it redirected the request more than {x.MAX_REDIRECTS} times')
        self.assertEqual(len(session.calls), x.MAX_REDIRECTS + 1)
        # A redirect without a Location is an answer like any other.
        _, fetch = self.fetch(FakeResponse(status=304))
        with self.assertRaises(x.HttpStatus) as raised:
            run(fetch(self.url, '*/*'))
        self.assertEqual(raised.exception.status, 304)


SHA7 = '0123abc'
REPO_SHA = SHA7 + '0' * 33
RAW = f'https://raw.githubusercontent.com/o/r/{REPO_SHA}/'
API = 'https://api.github.com/repos/o/r/commits/'
DETECT = 'library:vision:global%3A%3ANodeLib.FaceDetection.DetectFaces'
VISION = 'namespace NodeLib.FaceDetection;\npublic static class Faces { }\n'
PEOPLE = 'namespace NodeLib;\npublic class PeopleDetector : SharpDevice { }\n'
CONFLICT = 'It changed after you inspected the repository; inspect it again'
NOT_ATTEMPTED = 'Not imported, because the node libraries were not'
FACES_ENTRY = '\n[[entity]]\nname = "Faces at the door"\npath = "nodes/faces.json"\ndescription = "Detect faces on the door camera"\n'


def device_node(camera='camera.front_door', *, kind='csharp_device', setting_type='entity', settings=None, arguments=None):
    """The People detector's C# device node, `front_door`, whose Camera is typed into it."""
    camera_setting = {'key': 'Camera', 'label': 'Camera', 'description': '', 'type': setting_type, 'secret': False,
                      'domain': 'camera', 'required': True}
    return {'id': 'front_door', 'kind': kind, 'position': {'x': 0, 'y': 0},
            'config': {'key': 'front_door', 'name': 'Front door', 'source': 'public class FrontDoor : PeopleDetector { }',
                       'signature': {'title': 'Front door', 'description': '', 'domain': None,
                                     'settings': [camera_setting] if settings is None else settings},
                       'arguments': {'Camera': {'kind': 'literal', 'type': 'entity_id:camera', 'value': camera}}
                       if arguments is None else arguments}}


def graph(*nodes):
    return {'nodes': list(nodes), 'edges': []}


def people_file(settings=(), **node):
    return {'name': 'Named in the file', 'settings': list(settings), 'logic': {'kind': 'graph', 'graph': graph(device_node(**node))}}


FACES_GRAPH = graph({'id': 'faces', 'kind': DETECT, 'config': {}})


class Repository:
    """GitHub, faked: the API names REPO_SHA for any ref, and the raw host serves `files` by path at that sha, 404 for
    any other. A file over CAP is refused as aiohttp_fetch refuses it; an exception given as a file is raised."""

    def __init__(self, manifest=None, files=None, api=None):
        self.files = {'hass_sharp.toml': spec_manifest() + FACES_ENTRY if manifest is None else manifest,
                      'nodes/Vision.cs': VISION, 'nodes/PeopleDetector.cs': PEOPLE,
                      'nodes/front_door_people.json': json.dumps(people_file()),
                      'nodes/faces.json': json.dumps({'name': 'Faces', 'logic': {'kind': 'graph', 'graph': FACES_GRAPH}}),
                      **(files or {})}
        self.api = api if api is not None else REPO_SHA
        self.asked = []
        self.github = x.GitHub(self.fetch)

    async def fetch(self, url, accept):
        self.asked.append(url)
        if url.startswith(API):
            answer = self.api
        elif url.startswith(RAW):
            answer = self.files.get(unquote(url[len(RAW):]), x.HttpStatus(404))
        else:
            answer = x.HttpStatus(404)
        if isinstance(answer, BaseException):
            raise answer
        data = answer.encode() if isinstance(answer, str) else answer
        if len(data) > x.CAP:
            raise x.TooLarge(unquote(url.rsplit('/', 1)[-1]))
        return data


def ok(result):
    return {'version': 1, 'ok': True, 'result': result}


def refused(code, detail):
    return {'version': 1, 'ok': False, 'error': {'code': code, 'detail': detail}}


class Backend:
    """HassSharp's stores, faked, recording every call: `libraries` and `definitions` are summaries, `stored` the
    definitions a read answers, `imported` the import's answer and `saved` each save's in turn (None: saved)."""

    def __init__(self, libraries=(), definitions=(), unreadable=(), stored=None, imported=None, saved=()):
        self.listed_libraries, self.listed_definitions = list(libraries), list(definitions)
        self.unreadable, self.stored = list(unreadable), stored or {}
        self.imported, self.saved = imported, list(saved)
        self.calls = []

    async def libraries(self):
        self.calls.append(('libraries',))
        return copy.deepcopy(self.listed_libraries)

    async def definitions(self):
        self.calls.append(('definitions',))
        return copy.deepcopy(self.listed_definitions), list(self.unreadable)

    async def read_definition(self, id):
        self.calls.append(('read', id))
        if id not in self.stored:
            raise x.ExternalError('not_found', 'Entity definition not found')
        return copy.deepcopy(self.stored[id])

    async def import_libraries(self, libraries):
        self.calls.append(('import', copy.deepcopy(libraries)))
        return self.imported or ok({'saved': True, 'documents': [{**l, 'revision': l['expectedRevision'] + 1} for l in libraries],
                                    'generation': 'g2', 'catalog': {}, 'automations': [], 'invalid': [], 'definitions': [],
                                    'invalidDefinitions': []})

    async def save_definition(self, request):
        self.calls.append(('save', copy.deepcopy(request)))
        answer = self.saved.pop(0) if self.saved else None
        return answer or ok({'id': request['id'], 'saved': True,
                             'document': {'id': request['id'], 'enabled': request.get('enabled', True)}, 'active': False})

    def writes(self):
        return [call for call in self.calls if call[0] in ('import', 'save')]

    def saves(self):
        return [call[1] for call in self.calls if call[0] == 'save']


def pick(path, mode='new', kind=None, **more):
    return {'kind': kind or ('library' if path.endswith('.cs') else 'entity'), 'path': path, 'mode': mode, **more}


def outcome(kind, name, path, outcome, id=None, enabled=None, message=None):
    return {'kind': kind, 'name': name, 'path': path, 'outcome': outcome, 'id': id, 'enabled': enabled, 'message': message}


class Inspecting(unittest.TestCase):
    def test_inspect_lists_items_and_existing(self):
        manifest = spec_manifest() + FACES_ENTRY + (
            '\n[[entity]]\nname = "Missing"\npath = "x.json"\n[[entity]]\nname = "Not one"\npath = "bad.json"\n'
            '[[library]]\nname = "Big.cs"\npath = "x.cs"\n[[library]]\nname = "Latin1.cs"\npath = "latin1.cs"\n')
        repo = Repository(manifest, {'x.cs': 'a' * (x.CAP + 1), 'bad.json': '{"logic": {"kind": "class"}}', 'latin1.cs': b'// caf\xe9'})
        backend = Backend(libraries=[{'id': 'a', 'name': 'Vision.cs', 'revision': 2}, {'id': 'b', 'name': 'Vision.cs', 'revision': 5},
                                     {'id': 'other', 'name': 'Other.cs', 'revision': 1}],
                          definitions=[{'id': 'front_door_people', 'name': 'Front door people', 'revision': 3},
                                       {'id': 'lower', 'name': 'front door people', 'revision': 1}],
                          unreadable=['broken'])
        answer = run(x.inspect(repo.github, backend, 'https://github.com/o/r/tree/main'))

        def item(kind, name, path, description='', existing=(), problem=None):
            return {'kind': kind, 'name': name, 'description': description, 'path': path, 'existing': list(existing), 'problem': problem}
        self.assertEqual(answer, {'owner': 'o', 'repo': 'r', 'ref': 'main', 'sha': REPO_SHA, 'items': [
            item('library', 'Vision.cs', 'nodes/Vision.cs',
                 'Face and person detection on a native library (YuNet and YOLOX-nano on tract), and the Detect faces node',
                 [{'id': 'a', 'revision': 2, 'name': 'Vision.cs'}, {'id': 'b', 'revision': 5, 'name': 'Vision.cs'}]),
            item('library', 'PeopleDetector.cs', 'nodes/PeopleDetector.cs',
                 'People detector: a device that watches a camera and reports faces and people seen'),
            item('library', 'Big.cs', 'x.cs', problem='x.cs is larger than 2 MiB'),
            item('library', 'Latin1.cs', 'latin1.cs', problem='latin1.cs is not UTF-8 text'),
            item('entity', 'Front door people', 'nodes/front_door_people.json', 'A People detector on camera.front_door',
                 [{'id': 'front_door_people', 'revision': 3, 'name': 'Front door people'}]),
            item('entity', 'Faces at the door', 'nodes/faces.json', 'Detect faces on the door camera'),
            item('entity', 'Missing', 'x.json', problem='x.json is not in the repository at 0123abc'),
            item('entity', 'Not one', 'bad.json', problem='bad.json is not an entity definition: it needs logic of kind graph'),
        ]})
        # One API call for the ref, then the manifest and every listed file at the sha it answered.
        self.assertEqual(repo.asked[0], API + 'main')
        self.assertEqual(repo.asked[1:], [RAW + p for p in ('hass_sharp.toml', 'nodes/Vision.cs', 'nodes/PeopleDetector.cs', 'x.cs',
                                                           'latin1.cs', 'nodes/front_door_people.json', 'nodes/faces.json',
                                                           'x.json', 'bad.json')])
        self.assertEqual(backend.writes(), [])

    def test_inspect_errors(self):
        cases = (
            (Repository(api=x.HttpStatus(404)), 'not_found', 'GitHub has no public repository o/r'),
            (Repository(api=x.HttpStatus(403)), 'fetch', LIMIT),
            (Repository(files={'hass_sharp.toml': x.HttpStatus(404)}), 'not_found', 'o/r has no hass_sharp.toml at its root at 0123abc'),
            (Repository(files={'hass_sharp.toml': 'x' * (x.CAP + 1)}), 'fetch', 'hass_sharp.toml is larger than 2 MiB'),
            (Repository(files={'nodes/Vision.cs': x.ExternalError('fetch', 'Could not reach GitHub: it did not answer in time')}),
             'fetch', 'Could not reach GitHub: it did not answer in time'),
            # The raw host refusing a file is not the API's limit, and it refuses the whole inspect.
            (Repository(files={'hass_sharp.toml': x.HttpStatus(429)}), 'fetch', 'GitHub refused to send hass_sharp.toml: try again later'),
            (Repository(files={'nodes/Vision.cs': x.HttpStatus(403)}), 'fetch', 'GitHub refused to send nodes/Vision.cs: try again later'),
        )
        for repo, code, message in cases:
            backend = Backend()
            self.assertEqual(error(self, code, lambda: run(x.inspect(repo.github, backend, 'https://github.com/o/r'))), message)
            self.assertEqual(backend.writes(), [])
        backend = Backend()
        repo = Repository(manifest='[[library]\n')
        self.assertTrue(error(self, 'manifest', lambda: run(x.inspect(repo.github, backend, 'https://github.com/o/r'))).startswith(
            'hass_sharp.toml is not valid TOML: '))
        for url in ('https://gitlab.com/o/r', None, 3):
            repo = Repository()
            self.assertEqual(error(self, 'invalid_request', lambda: run(x.inspect(repo.github, backend, url))), INVALID_URL)
            self.assertEqual(repo.asked, [])
        self.assertEqual(backend.calls, [])


class Importing(unittest.TestCase):
    def importing(self, picks, repo=None, backend=None):
        repo, backend = repo or Repository(), backend or Backend()
        answer = run(x.import_items(repo.github, backend, {'owner': 'o', 'repo': 'r', 'sha': REPO_SHA, 'picks': picks}))
        return answer, repo, backend

    def test_import_refetches_the_sha_only(self):
        _, repo, _ = self.importing([pick('nodes/Vision.cs'), pick('nodes/PeopleDetector.cs'), pick('nodes/front_door_people.json')])
        self.assertEqual(repo.asked, [RAW + p for p in ('hass_sharp.toml', 'nodes/Vision.cs', 'nodes/PeopleDetector.cs',
                                                       'nodes/front_door_people.json')])
        self.assertFalse(any(url.startswith('https://api.github.com') for url in repo.asked))

    def test_import_libraries_then_entities(self):
        answer, _, backend = self.importing([pick('nodes/Vision.cs'), pick('nodes/PeopleDetector.cs'), pick('nodes/front_door_people.json')])
        self.assertEqual([call[0] for call in backend.calls], ['libraries', 'definitions', 'import', 'save'])
        self.assertEqual(backend.calls[2][1], [
            {'id': 'vision', 'name': 'Vision.cs', 'source': VISION, 'expectedRevision': 0},
            {'id': 'people_detector', 'name': 'PeopleDetector.cs', 'source': PEOPLE, 'expectedRevision': 0}])
        # The manifest's name, not the file's; the file's graph as it is, with no library kind to rewrite.
        self.assertEqual(backend.saves(), [{'operation': 'save', 'id': 'front_door_people', 'expectedRevision': 0, 'enabled': False,
                                            'deriveSignatures': True,
                                            'definition': {'name': 'Front door people', 'settings': [],
                                                           'logic': people_file()['logic']}}])
        self.assertEqual(answer['items'], [
            outcome('library', 'Vision.cs', 'nodes/Vision.cs', 'imported', 'vision'),
            outcome('library', 'PeopleDetector.cs', 'nodes/PeopleDetector.cs', 'imported', 'people_detector'),
            outcome('entity', 'Front door people', 'nodes/front_door_people.json', 'imported', 'front_door_people', False)])
        self.assertEqual((answer['owner'], answer['repo'], answer['sha']), ('o', 'r', REPO_SHA))
        # Of .NET's answer only what the panel uses: the generation and where each file is stored.
        self.assertEqual(answer['libraries'], {'generation': 'g2', 'documents': [
            {'id': 'vision', 'name': 'Vision.cs', 'revision': 1}, {'id': 'people_detector', 'name': 'PeopleDetector.cs', 'revision': 1}]})

    def test_kinds_follow_the_library_actually_used(self):
        stored = [{'id': 'library-1234', 'name': 'Vision.cs', 'revision': 4}]
        used = 'library:library-1234:global%3A%3ANodeLib.FaceDetection.DetectFaces'
        answer, _, backend = self.importing([pick('nodes/Vision.cs', 'overwrite', target='library-1234', expectedRevision=4),
                                             pick('nodes/faces.json')], backend=Backend(libraries=stored))
        self.assertEqual(backend.calls[2], ('import', [{'id': 'library-1234', 'name': 'Vision.cs', 'source': VISION, 'expectedRevision': 4}]))
        self.assertEqual(backend.saves()[0]['definition']['logic']['graph']['nodes'][0]['kind'], used)
        self.assertEqual([i['outcome'] for i in answer['items']], ['overwritten', 'imported'])
        self.assertEqual(answer['items'][0]['id'], 'library-1234')

        # Skipped, it is still the one stored file of that name that the definition uses.
        answer, _, backend = self.importing([pick('nodes/faces.json')], backend=Backend(libraries=stored))
        self.assertEqual([call[0] for call in backend.calls], ['libraries', 'definitions', 'save'])
        self.assertEqual(backend.saves()[0]['definition']['logic']['graph']['nodes'][0]['kind'], used)
        self.assertIsNone(answer['libraries'])

        # Two files of that name: which one is meant is not known, so the kind is left as the file has it.
        _, _, backend = self.importing([pick('nodes/faces.json')], backend=Backend(
            libraries=[{'id': 'a', 'name': 'Vision.cs', 'revision': 1}, {'id': 'b', 'name': 'Vision.cs', 'revision': 1}]))
        self.assertEqual(backend.saves()[0]['definition']['logic']['graph']['nodes'][0]['kind'], DETECT)

    def test_manifest_id_taken_gives_a_new_id(self):
        backend = Backend(libraries=[{'id': 'vision', 'name': 'Other.cs', 'revision': 1}])
        answer, _, backend = self.importing([pick('nodes/Vision.cs'), pick('nodes/PeopleDetector.cs'), pick('nodes/faces.json')],
                                            backend=backend)
        vision, people = backend.calls[2][1]
        self.assertRegex(vision['id'], r'^library-[0-9a-f-]{36}$')
        self.assertEqual(people['id'], 'people_detector')
        self.assertEqual(answer['items'][0]['id'], vision['id'])
        self.assertEqual(backend.saves()[0]['definition']['logic']['graph']['nodes'][0]['kind'],
                         f'library:{vision["id"]}:global%3A%3ANodeLib.FaceDetection.DetectFaces')

    def test_library_overwrite_only(self):
        libraries = [{'id': 'a', 'name': 'Vision.cs', 'revision': 1}, {'id': 'other', 'name': 'Other.cs', 'revision': 1}]
        definitions = [{'id': 'front_door_people', 'name': 'Front door people', 'revision': 3},
                       {'id': 'porch', 'name': 'Porch', 'revision': 1}]
        named = (
            pick('nodes/Vision.cs'),
            pick('nodes/Vision.cs', 'overwrite', target='other', expectedRevision=1),
            pick('nodes/Vision.cs', 'overwrite', target='a'),
            pick('nodes/Vision.cs', 'overwrite', target='a', expectedRevision='1'),
            pick('nodes/PeopleDetector.cs', 'overwrite', target='a', expectedRevision=1),
            pick('nodes/PeopleDetector.cs', target='people_detector'),
            pick('nodes/front_door_people.json', 'overwrite', target='porch', expectedRevision=1),
            pick('nodes/front_door_people.json', 'overwrite', target='front_door_people'),
            pick('nodes/Nope.cs'),
            pick('nodes/front_door_people.json', kind='library'),
        )
        for bad in named:
            with self.subTest(pick=bad):
                repo, backend = Repository(), Backend(libraries=libraries, definitions=definitions)
                message = error(self, 'invalid_request', lambda: self.importing([pick('nodes/faces.json'), bad], repo, backend))
                self.assertIn(bad['path'], message)
                self.assertEqual(backend.writes(), [])
                self.assertEqual(repo.asked, [RAW + 'hass_sharp.toml'])
        # Refused before anything is fetched.
        for request in ({'owner': 'o', 'repo': 'r', 'sha': REPO_SHA, 'picks': [pick('nodes/Vision.cs'), pick('nodes/Vision.cs')]},
                        {'owner': 'o', 'repo': 'r', 'sha': REPO_SHA, 'picks': []},
                        {'owner': 'o', 'repo': 'r', 'sha': REPO_SHA},
                        {'owner': 'o', 'repo': 'r', 'sha': REPO_SHA, 'picks': [pick('nodes/Vision.cs', 'copy')]},
                        {'owner': 'o', 'repo': 'r', 'sha': REPO_SHA, 'picks': [pick('nodes/Vision.cs', kind='script')]},
                        {'owner': 'o', 'repo': 'r', 'sha': REPO_SHA, 'picks': [{'kind': 'library', 'mode': 'new'}]},
                        {'owner': 'o', 'repo': 'r', 'sha': REPO_SHA, 'picks': ['nodes/Vision.cs']},
                        {'owner': 'o', 'repo': 'r', 'sha': REPO_SHA.upper(), 'picks': [pick('nodes/Vision.cs')]},
                        {'owner': 'o', 'repo': 'r', 'sha': SHA7, 'picks': [pick('nodes/Vision.cs')]},
                        {'owner': 'o/x', 'repo': 'r', 'sha': REPO_SHA, 'picks': [pick('nodes/Vision.cs')]},
                        {'owner': 'o', 'repo': '..', 'sha': REPO_SHA, 'picks': [pick('nodes/Vision.cs')]},
                        {'repo': 'r', 'sha': REPO_SHA, 'picks': [pick('nodes/Vision.cs')]}):
            with self.subTest(request=request):
                repo, backend = Repository(), Backend(libraries=libraries)
                error(self, 'invalid_request', lambda: run(x.import_items(repo.github, backend, request)))
                self.assertEqual((repo.asked, backend.calls), ([], []))
        # A picked file that cannot be imported refuses the whole import, before anything is written.
        repo, backend = Repository(files={'nodes/faces.json': '{"logic": 3}'}), Backend()
        self.assertEqual(error(self, 'invalid_request', lambda: self.importing([pick('nodes/Vision.cs'), pick('nodes/faces.json')], repo, backend)),
                         'nodes/faces.json is not an entity definition: it needs logic of kind graph')
        self.assertEqual(backend.writes(), [])
        repo, backend = Repository(files={'nodes/faces.json': x.ExternalError('fetch', 'Could not reach GitHub: down')}), Backend()
        self.assertEqual(error(self, 'fetch', lambda: self.importing([pick('nodes/Vision.cs'), pick('nodes/faces.json')], repo, backend)),
                         'Could not reach GitHub: down')
        self.assertEqual(backend.writes(), [])

    def test_refused_libraries_stop_entities(self):
        diagnostics = [{'sourceId': 'people_detector', 'startLine': 3, 'startColumn': 5, 'endLine': 3, 'endColumn': 9,
                        'message': "The type or namespace name 'Faces' could not be found", 'severity': 3},
                       {'sourceId': 'people_detector', 'startLine': 1, 'startColumn': 1, 'endLine': 1, 'endColumn': 2,
                        'message': 'A warning', 'severity': 2},
                       {'sourceId': 'shared', 'startLine': 7, 'startColumn': 2, 'endLine': 7, 'endColumn': 3,
                        'message': 'Another error', 'severity': 3}]
        validation = refused('validation', {'diagnostics': diagnostics, 'invalid': [],
                                            'automations': [{'id': 'porch', 'name': 'Porch lights', 'diagnostics': []}],
                                            'definitions': [{'id': 'door', 'name': 'Door bell', 'diagnostics': []}]})
        backend = Backend(libraries=[{'id': 'shared', 'name': 'Shared.cs', 'revision': 2}], imported=validation)
        answer, _, backend = self.importing([pick('nodes/PeopleDetector.cs'), pick('nodes/front_door_people.json')], backend=backend)
        message = ("PeopleDetector.cs 3:5: The type or namespace name 'Faces' could not be found; Shared.cs 7:2: Another error; "
                   "it would break Porch lights; it would break Door bell")
        self.assertEqual(answer['items'], [
            outcome('library', 'PeopleDetector.cs', 'nodes/PeopleDetector.cs', 'failed', message=message),
            outcome('entity', 'Front door people', 'nodes/front_door_people.json', 'failed', message=NOT_ATTEMPTED)])
        self.assertIsNone(answer['libraries'])
        self.assertEqual(backend.saves(), [])

        many = refused('validation', {'diagnostics': [{**diagnostics[0], 'message': f'Error {n}'} for n in range(5)],
                                      'automations': [], 'invalid': [], 'definitions': []})
        answer, _, _ = self.importing([pick('nodes/PeopleDetector.cs')], backend=Backend(imported=many))
        self.assertEqual(answer['items'][0]['message'],
                         'PeopleDetector.cs 3:5: Error 0; PeopleDetector.cs 3:5: Error 1; PeopleDetector.cs 3:5: Error 2')
        for answered, expected in ((refused('conflict', 'The document changed'), CONFLICT),
                                   (refused('storage', 'Disk full'), 'Disk full')):
            answer, _, backend = self.importing([pick('nodes/Vision.cs'), pick('nodes/faces.json')], backend=Backend(imported=answered))
            self.assertEqual([(i['outcome'], i['message']) for i in answer['items']], [('failed', expected), ('failed', NOT_ATTEMPTED)])
            self.assertEqual(backend.saves(), [])

    def test_entity_failure_keeps_libraries(self):
        validation = refused('validation', {'valid': False, 'diagnostics': [
            {'nodeId': 'front_door', 'message': 'Camera is required', 'severity': 3},
            {'nodeId': None, 'message': 'Only advice', 'severity': 2}]})
        backend = Backend(saved=[validation])
        answer, _, backend = self.importing([pick('nodes/Vision.cs'), pick('nodes/PeopleDetector.cs'),
                                             pick('nodes/front_door_people.json'), pick('nodes/faces.json')], backend=backend)
        self.assertEqual(answer['items'], [
            outcome('library', 'Vision.cs', 'nodes/Vision.cs', 'imported', 'vision'),
            outcome('library', 'PeopleDetector.cs', 'nodes/PeopleDetector.cs', 'imported', 'people_detector'),
            outcome('entity', 'Front door people', 'nodes/front_door_people.json', 'failed', message='Camera is required'),
            outcome('entity', 'Faces at the door', 'nodes/faces.json', 'imported', 'faces_at_the_door', False)])
        self.assertEqual([call[0] for call in backend.calls], ['libraries', 'definitions', 'import', 'save', 'save'])
        self.assertIsNotNone(answer['libraries'])

        stored = {'front_door_people': {'id': 'front_door_people', 'revision': 3, 'name': 'Front door people', 'settings': [],
                                        'logic': {'kind': 'graph', 'graph': graph()}}}
        listed = [{'id': 'front_door_people', 'name': 'Front door people', 'revision': 3}]
        for answered, expected in ((refused('conflict', 'Revision mismatch'), CONFLICT), (refused('storage', 'Disk full'), 'Disk full')):
            answer, _, _ = self.importing([pick('nodes/front_door_people.json', 'overwrite', target='front_door_people', expectedRevision=3)],
                                          backend=Backend(definitions=listed, stored=stored, saved=[answered]))
            self.assertEqual(answer['items'][0]['outcome'], 'failed')
            self.assertEqual(answer['items'][0]['message'], expected)
        # Deleted since it was listed: changed after the inspect.
        answer, _, backend = self.importing([pick('nodes/front_door_people.json', 'overwrite', target='front_door_people', expectedRevision=3)],
                                            backend=Backend(definitions=listed))
        self.assertEqual((answer['items'][0]['outcome'], answer['items'][0]['message']), ('failed', CONFLICT))
        self.assertEqual(backend.saves(), [])

    def test_overwrite_keeps_setting_values(self):
        stored = {'front_door_people': {
            'id': 'front_door_people', 'revision': 3, 'name': 'Front door people', 'enabled': True,
            'settings': [{'key': 'address', 'label': 'Address', 'type': 'string', 'secret': True, 'hasValue': True},
                         {'key': 'interval', 'type': 'number', 'value': 5}],
            'logic': {'kind': 'graph', 'graph': graph()}}}
        settings = [{'key': 'address', 'label': 'Address', 'type': 'string', 'secret': True, 'value': ''},
                    {'key': 'interval', 'type': 'string', 'value': '5 s'},
                    {'key': 'extra', 'type': 'boolean', 'value': True}]
        repo = Repository(files={'nodes/front_door_people.json': json.dumps(people_file(settings))})
        backend = Backend(definitions=[{'id': 'front_door_people', 'name': 'Front door people', 'revision': 3}], stored=stored)
        answer, _, backend = self.importing(
            [pick('nodes/front_door_people.json', 'overwrite', target='front_door_people', expectedRevision=3)], repo, backend)
        [saved] = backend.saves()
        self.assertEqual(saved['definition']['settings'], [
            {'key': 'address', 'label': 'Address', 'type': 'string', 'secret': True, 'keep': True},
            {'key': 'interval', 'type': 'string', 'value': '5 s'},
            {'key': 'extra', 'type': 'boolean', 'value': True}])
        self.assertEqual((saved['id'], saved['expectedRevision'], saved['deriveSignatures']), ('front_door_people', 3, True))
        self.assertNotIn('enabled', saved)
        self.assertEqual(saved['definition']['name'], 'Front door people')
        self.assertEqual(answer['items'], [outcome('entity', 'Front door people', 'nodes/front_door_people.json', 'overwritten',
                                                   'front_door_people', True)])

    def test_overwrite_keeps_device_inputs(self):
        def stored(node):
            return {'front_door_people': {'id': 'front_door_people', 'revision': 3, 'name': 'Front door people', 'settings': [],
                                          'logic': {'kind': 'graph', 'graph': graph(node)}}}
        listed = [{'id': 'front_door_people', 'name': 'Front door people', 'revision': 3}]
        answer, _, backend = self.importing(
            [pick('nodes/front_door_people.json', 'overwrite', target='front_door_people', expectedRevision=3)],
            backend=Backend(definitions=listed, stored=stored(device_node('camera.porch'))))
        [node] = backend.saves()[0]['definition']['logic']['graph']['nodes']
        self.assertEqual(node['config']['arguments']['Camera'], {'kind': 'literal', 'type': 'entity_id:camera', 'value': 'camera.porch'})
        self.assertEqual({k: v for k, v in node.items() if k != 'config'}, {'id': 'front_door', 'kind': 'csharp_device', 'position': {'x': 0, 'y': 0}})

        tuned = device_node('camera.porch', settings=[*device_node()['config']['signature']['settings'],
                                                      {'key': 'CheckEvery', 'type': 'number'}, {'key': 'FaceScore', 'type': 'number'}],
                            arguments={'Camera': {'kind': 'literal', 'type': 'entity_id:camera', 'value': 'camera.porch'},
                                       'CheckEvery': {'kind': 'literal', 'type': 'number', 'value': 2.5},
                                       'FaceScore': {'kind': 'literal', 'type': 'number', 'value': 0.8}})
        file_settings = tuned['config']['signature']['settings']
        imported = graph(device_node(settings=file_settings))
        before = copy.deepcopy(imported)
        arguments = lambda g: g['nodes'][0]['config'].get('arguments')
        camera = lambda g: (arguments(g) or {}).get('Camera', {}).get('value')
        # A value tuned in the stored node that the file does not set is kept, beside the camera.
        self.assertEqual(arguments(x.keep_device_inputs(imported, graph(tuned))), tuned['config']['arguments'])
        self.assertEqual(imported, before)
        # A file's node without a signature is taken to have the stored node's settings: the camera and the rest are kept.
        unsigned = device_node()
        del unsigned['config']['signature']
        self.assertEqual(arguments(x.keep_device_inputs(graph(unsigned), graph(tuned))), tuned['config']['arguments'])
        bare = device_node(arguments={})
        del bare['config']['signature'], bare['config']['arguments']
        self.assertEqual(arguments(x.keep_device_inputs(graph(bare), graph(tuned))), tuned['config']['arguments'])
        # A setting of another type or domain in the file's signature than in the stored one's, or one the file's
        # signature does not have, is the file's.
        for settings in ([{**file_settings[0], 'type': 'string'}, *file_settings[1:]],
                         [{**file_settings[0], 'domain': 'image'}, *file_settings[1:]],
                         file_settings[1:]):
            kept = x.keep_device_inputs(graph(device_node(settings=settings)), graph(tuned))
            self.assertEqual(camera(kept), 'camera.front_door', settings)
            self.assertEqual(arguments(kept)['CheckEvery']['value'], 2.5)
        for existing in (device_node('camera.porch', setting_type='string'),
                         device_node('camera.porch', settings=[{'key': 'Camera', 'type': 'entity', 'domain': 'image'}])):
            self.assertEqual(camera(x.keep_device_inputs(graph(device_node()), graph(existing))), 'camera.front_door', existing)
        # A pin the imported graph wires is filled by that edge, not by the stored value.
        wired = graph(device_node(settings=file_settings, arguments={'Camera': {'kind': 'unset'}}), {'id': 'pick', 'kind': 'setting', 'config': {'key': 'camera'}})
        wired['edges'] = [{'id': 'e', 'kind': 'data', 'source': 'pick', 'sourcePort': 'value', 'target': 'front_door', 'targetPort': 'Camera'}]
        kept = x.keep_device_inputs(wired, graph(tuned))
        self.assertEqual(arguments(kept)['Camera'], {'kind': 'unset'})
        self.assertEqual(arguments(kept)['FaceScore']['value'], 0.8)
        # A stored node of another kind or id keeps nothing.
        for existing in ({**tuned, 'kind': 'csharp_entity'}, {**tuned, 'id': 'back_door'}):
            self.assertEqual(arguments(x.keep_device_inputs(imported, graph(existing))), {'Camera': before['nodes'][0]['config']['arguments']['Camera']}, existing['id'])
        # A stored argument other than a typed value (here one cleared) is kept only where the file sets it too.
        cleared = {**tuned, 'config': {**tuned['config'], 'arguments': {'Camera': {'kind': 'unset'}, 'FaceScore': {'kind': 'unset'}}}}
        self.assertEqual(arguments(x.keep_device_inputs(imported, graph(cleared))), {'Camera': {'kind': 'unset'}})
        # A C# entity's typed settings are kept alike; graphs of other shapes are taken as they come.
        entity = device_node(kind='csharp_entity')
        self.assertEqual(camera(x.keep_device_inputs(graph(entity), graph(device_node('camera.porch', kind='csharp_entity')))), 'camera.porch')
        self.assertEqual(x.keep_device_inputs(imported, {}), imported)
        self.assertEqual(x.keep_device_inputs({'nodes': [3, {'id': 'front_door'}]}, graph(device_node())), {'nodes': [3, {'id': 'front_door'}]})
        self.assertEqual(x.keep_device_inputs(graph(bare), graph(device_node(arguments={}))), graph(bare))

    def test_an_entity_that_raises_still_gets_its_line(self):
        # An exception (not a refusal) after the libraries are stored: that entity fails, the next is still imported.
        class Raising(Backend):
            async def save_definition(self, request):
                if request['id'] == 'front_door_people':
                    self.calls.append(('save', copy.deepcopy(request)))
                    raise RuntimeError('The .NET call was interrupted')
                return await super().save_definition(request)
        backend = Raising()
        with self.assertLogs('custom_components.HassSharp.external', 'ERROR') as logged:
            answer, _, backend = self.importing([pick('nodes/Vision.cs'), pick('nodes/front_door_people.json'), pick('nodes/faces.json')],
                                                backend=backend)
        self.assertEqual(answer['items'], [
            outcome('library', 'Vision.cs', 'nodes/Vision.cs', 'imported', 'vision'),
            outcome('entity', 'Front door people', 'nodes/front_door_people.json', 'failed', message='Not imported: The .NET call was interrupted'),
            outcome('entity', 'Faces at the door', 'nodes/faces.json', 'imported', 'faces_at_the_door', False)])
        self.assertEqual(answer['libraries']['documents'], [{'id': 'vision', 'name': 'Vision.cs', 'revision': 1}])
        self.assertEqual([call[0] for call in backend.calls], ['libraries', 'definitions', 'import', 'save', 'save'])
        self.assertIn('nodes/front_door_people.json', logged.output[0])

        class Unreadable(Backend):
            async def read_definition(self, id):
                raise KeyError('logic')
        listed = [{'id': 'front_door_people', 'name': 'Front door people', 'revision': 3}]
        with self.assertLogs('custom_components.HassSharp.external', 'ERROR'):
            answer, _, _ = self.importing([pick('nodes/front_door_people.json', 'overwrite', target='front_door_people', expectedRevision=3)],
                                          backend=Unreadable(definitions=listed))
        self.assertEqual((answer['items'][0]['outcome'], answer['items'][0]['message']), ('failed', "Not imported: 'logic'"))

    def test_new_ids_avoid_taken_and_each_other(self):
        manifest = ('[[entity]]\nname = "Front door people"\npath = "a.json"\n'
                    '[[entity]]\nname = "Front door people"\npath = "b.json"\n')
        body = json.dumps(people_file())
        repo = Repository(manifest, {'a.json': body, 'b.json': body})
        backend = Backend(definitions=[{'id': 'front_door_people', 'name': 'Front door people', 'revision': 1}],
                          unreadable=['front_door_people_2'])
        answer, _, backend = self.importing([pick('b.json'), pick('a.json')], repo, backend)
        self.assertEqual([s['id'] for s in backend.saves()], ['front_door_people_3', 'front_door_people_4'])
        self.assertEqual([(i['path'], i['id']) for i in answer['items']], [('a.json', 'front_door_people_3'), ('b.json', 'front_door_people_4')])


class Route(unittest.IsolatedAsyncioTestCase):
    """hass_sharp/external through websocket.py and AuthoringService, with .NET, Home Assistant's session and its
    trigger check faked, and the real fetcher."""

    def setUp(self):
        self.dotnet = []
        self.libraries = [{'version': 1, 'id': 'shared', 'name': 'Shared.cs', 'revision': 2, 'source': '//'}]

        def library_request(raw):
            request = json.loads(raw)
            self.dotnet.append(('library', request))
            if request['operation'] == 'list':
                return json.dumps(ok({'documents': self.libraries, 'generation': 'g1'}))
            return json.dumps(ok({'saved': True, 'documents': [{'version': 1, **l, 'revision': 1} for l in request['libraries']],
                                  'generation': 'g2', 'catalog': {'nodes': {}}, 'automations': [{'id': 'porch'}], 'invalid': [],
                                  'definitions': [], 'invalidDefinitions': []}))

        def entity_request(raw):
            request = json.loads(raw)
            self.dotnet.append(('entity', request))
            if request['operation'] == 'list':
                return json.dumps({**ok([{'id': 'porch', 'name': 'Porch', 'revision': 1, 'enabled': True}]),
                                   'unreadable': [{'id': 'broken', 'reason': 'bad', 'deletable': True}]})
            if request['operation'] == 'preview':
                return json.dumps(ok({'valid': True, 'diagnostics': []}))
            return json.dumps(ok({'id': request['id'], 'saved': True, 'document': {'id': request['id'], 'enabled': False}}))

        files = {'hass_sharp.toml': spec_manifest(), 'nodes/Vision.cs': VISION, 'nodes/PeopleDetector.cs': PEOPLE,
                 'nodes/front_door_people.json': json.dumps(people_file())}
        self.session = FakeSession({**{RAW + path: FakeResponse(body.encode()) for path, body in files.items()},
                                    API + 'HEAD': FakeResponse(REPO_SHA.encode())})
        self.hass = NS(data={'hass_sharp_llm': {'entry': EntryState(None, ready=True)}})

        async def no_trigger_problems(hass, graph):
            return []
        sessions = []
        self.sessions = sessions
        self.handlers, self.env = load_routes(
            NS(NodeLibraryRequest=library_request, EntityRequest=entity_request), self.hass, external=x,
            async_get_clientsession=lambda hass: sessions.append(hass) or self.session,
            ClientTimeout=lambda **k: k, native_triggers=NS(async_validate_graph_triggers=no_trigger_problems))

    async def send(self, request, admin=True):
        results, errors = [], []
        connection = NS(user=NS(is_admin=admin), send_result=lambda *v: results.append(v), send_error=lambda *v: errors.append(v))
        await self.handlers['hass_sharp/external'](self.hass, connection, {'id': 1, 'request': request})
        return results, errors

    async def test_external_route_is_admin_and_envelope(self):
        results, errors = await self.send({'operation': 'inspect', 'url': 'https://github.com/o/r'}, admin=False)
        self.assertEqual((results, errors), ([], [(1, 'unauthorized', 'Administrator required')]))
        self.assertEqual((self.session.calls, self.dotnet), ([], []))

        results, errors = await self.send({'operation': 'inspect', 'url': 'https://github.com/o/r'})
        self.assertEqual(errors, [])
        [(_, answer)] = results
        self.assertEqual((answer['version'], answer['ok']), (1, True))
        self.assertEqual(answer['result']['sha'], REPO_SHA)
        self.assertEqual([(i['name'], i['existing'], i['problem']) for i in answer['result']['items']],
                         [('Vision.cs', [], None), ('PeopleDetector.cs', [], None), ('Front door people', [], None)])
        self.assertEqual(self.sessions, [self.hass])
        self.assertTrue(all(kwargs['timeout'] == {'total': 30} for _, kwargs in self.session.calls))
        self.assertEqual(self.dotnet, [('library', {'operation': 'list'}), ('entity', {'operation': 'list'})])

        results, errors = await self.send({'operation': 'inspect', 'url': 'https://gitlab.com/o/r'})
        self.assertEqual(results, [(1, refused('invalid_request', INVALID_URL))])
        results, errors = await self.send({'operation': 'export'})
        self.assertEqual(results, [(1, refused('invalid_request', 'operation must be inspect or import'))])
        self.env['logger'].exception.assert_not_called()

    async def test_external_import_saves_through_the_stores(self):
        results, errors = await self.send({'operation': 'import', 'owner': 'o', 'repo': 'r', 'sha': REPO_SHA, 'picks': [
            pick('nodes/Vision.cs'), pick('nodes/front_door_people.json')]})
        self.assertEqual(errors, [])
        [(_, answer)] = results
        self.assertEqual([(i['outcome'], i['id']) for i in answer['result']['items']], [('imported', 'vision'), ('imported', 'front_door_people')])
        # Not the sources, the catalog or the rebuilt automations: what the panel uses.
        self.assertEqual(answer['result']['libraries'], {'generation': 'g2', 'documents': [{'id': 'vision', 'name': 'Vision.cs', 'revision': 1}]})
        self.assertEqual([(side, request['operation']) for side, request in self.dotnet],
                         [('library', 'list'), ('entity', 'list'), ('library', 'import'), ('entity', 'preview'), ('entity', 'save')])
        self.assertEqual(self.dotnet[2][1]['libraries'], [{'id': 'vision', 'name': 'Vision.cs', 'source': VISION, 'expectedRevision': 0}])
        # The save goes through `entities`, so Home Assistant's trigger check runs on its derived preview first.
        self.assertTrue(self.dotnet[3][1]['deriveSignatures'])
        save = self.dotnet[4][1]
        self.assertEqual((save['id'], save['expectedRevision'], save['enabled'], save['deriveSignatures']), ('front_door_people', 0, False, True))
        self.assertEqual(self.session.calls[0][0], RAW + 'hass_sharp.toml')
        self.assertFalse(any(url.startswith('https://api.github.com') for url, _ in self.session.calls))

    async def test_external_refusal_of_a_list_is_the_answer(self):
        async def storage(hass, method, request):
            return json.dumps(refused('storage', 'The node library folder cannot be read'))
        self.env['dotnet_tasks'] = NS(async_run_dotnet_task=storage)
        results, errors = await self.send({'operation': 'inspect', 'url': 'https://github.com/o/r'})
        self.assertEqual((results, errors), ([(1, refused('storage', 'The node library folder cannot be read'))], []))


if __name__ == '__main__':
    unittest.main()
