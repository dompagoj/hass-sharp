import asyncio
import importlib.util
import json
import os
import pathlib
import re
import unittest

ROOT = pathlib.Path(os.environ.get('HASS_SHARP_TEST_ROOT', pathlib.Path(__file__).resolve().parents[2]))
spec = importlib.util.spec_from_file_location('hass_sharp_external', ROOT / 'custom_components/hass_sharp/external.py')
x = importlib.util.module_from_spec(spec)
spec.loader.exec_module(x)

SHA = 'a' * 40
INVALID_URL = "Paste a GitHub repository's address, such as https://github.com/owner/repo or https://github.com/owner/repo/tree/main"
LIMIT = 'GitHub refused the request: it allows 60 requests an hour without signing in; try again later'


def spec_manifest() -> str:
    """hass-sharp-vision's manifest, as the design's section 6 quotes it."""
    text = (ROOT / 'docs/superpowers/specs/2026-11-22-face-detection-external-import-design.md').read_text()
    section = text.split('### 6. Manifest', 1)[1]
    return re.search(r'```toml\n(.*?)```', section, re.S).group(1)


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
        github, asked = self.github(('{"sha": "%s", "commit": {}}' % sha).encode())
        self.assertEqual(run(github.resolve(x.Repo('o', 'r', 'feature/x'))), sha)
        self.assertEqual(run(github.resolve(x.Repo('o', 'r', None))), sha)
        self.assertEqual(asked, [('https://api.github.com/repos/o/r/commits/feature/x', 'application/vnd.github+json'),
                                 ('https://api.github.com/repos/o/r/commits/HEAD', 'application/vnd.github+json')])

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
        for body in (b'{"sha": "abc"}', b'{"sha": "%s"}' % (b'A' * 40), b'[]', b'not json', b'{}'):
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
        github, _ = self.github(x.HttpStatus(429))
        self.assertEqual(error(self, 'fetch', lambda: run(github.file('o', 'r', SHA, 'a.cs'))), LIMIT)


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


if __name__ == '__main__':
    unittest.main()
