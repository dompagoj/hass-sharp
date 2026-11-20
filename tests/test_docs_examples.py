"""Keep the custom entities docs true to the code: their examples, the MCP tool names and their links."""

import ast
import json
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[1]
INTEGRATION = ROOT / "custom_components/hass_sharp"
GUIDE = ROOT / "docs/custom-entities.md"
MCP = ROOT / "docs/mcp-authoring.md"
DOCS = (ROOT / "README.md", GUIDE, ROOT / "docs/backend/entity-definitions.md", ROOT / "docs/backend/run-history.md",
        ROOT / "docs/node-libraries.md", MCP)
# A tool named in the MCP doc: in backticks, alone or called (`list`, `read({id})`), or an entity definition tool's name
# anywhere. A backticked word that is not called and not a tool's name is another identifier, such as `valid`.
CALLED = re.compile(r"`([a-z][a-z_]*)\(")
NAMED = re.compile(r"`([a-z][a-z_]*)[`(]|(?<![A-Za-z0-9_])(entities_[a-z_]+)")
LINK = re.compile(r"\[[^\]]*\]\(([^)\s]+)\)")


def example_definition(title):
    examples = json.loads((INTEGRATION / "authoring_examples.json").read_text(encoding="utf-8"))
    return next(example for example in examples["definitions"] if example["title"] == title)


def fenced_block_after(text, marker, language):
    """The contents of the first fenced block of `language` after the line `marker`."""
    lines = text.splitlines()
    start = lines.index(marker)
    opening = next(i for i in range(start + 1, len(lines)) if lines[i].startswith("```"))
    if lines[opening] != "```" + language:
        raise AssertionError(f"The block after {marker} is {lines[opening]!r}, not a {language} block")
    closing = next(i for i in range(opening + 1, len(lines)) if lines[i] == "```")
    return "\n".join(lines[opening + 1:closing])


def class_node(title, kind):
    """The one node of `kind` (csharp_entity or csharp_device) of the example definition `title`."""
    nodes = [node for node in example_definition(title)["definition"]["logic"]["graph"]["nodes"] if node["kind"] == kind]
    if len(nodes) != 1:
        raise AssertionError(f"{title} has {len(nodes)} nodes of kind {kind}, not one")
    return nodes[0]


# A C# node a document shows as JSON may write its code as this, for the code block shown beside it.
ELIDED = "…"


def llm_constants():
    """SEMANTICS and the tool tables (SPECS, ENTITY_SPECS) of llm.py, read from its source: importing it needs Home
    Assistant. A tool's description is a plain string or an f-string; an f-string's text is joined without its fields."""
    tree = ast.parse((INTEGRATION / "llm.py").read_text(encoding="utf-8"))
    found = {}
    for node in tree.body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id in ("SEMANTICS", "SPECS", "ENTITY_SPECS") for t in node.targets):
            name = next(t.id for t in node.targets if isinstance(t, ast.Name))
            if name == "SEMANTICS":
                found[name] = ast.literal_eval(node.value)
                continue
            table = {}
            for key, value in zip(node.value.keys, node.value.values):
                description = value.elts[1]
                table[key.value] = description.value if isinstance(description, ast.Constant) else "".join(
                    part.value for part in description.values if isinstance(part, ast.Constant))
            found[name] = table
    return found


def members(listed):
    """The member names of a written-out config such as `{key, name, via?}`: without backticks, white space or ?."""
    inner = listed.strip().strip("`").strip()
    if not (inner.startswith("{") and inner.endswith("}")):
        raise AssertionError(f"not a written-out config: {listed!r}")
    return [member.strip().strip("`").strip().rstrip("?") for member in inner[1:-1].replace("\n", " ").split(",")]


def config_after(text, start):
    """The first `{…}` in `text` at or after `start`, with no brace inside it."""
    match = re.compile(r"\{[^{}]*\}").search(text, start)
    if match is None:
        raise AssertionError(f"no written-out config after {text[start:start + 60]!r}")
    return match.group(0)


def tool_names():
    """The keys of the tool tables in llm.py (SPECS for the automations' and libraries' tools, ENTITY_SPECS for the
    entity definitions'), read from its source: importing it needs Home Assistant."""
    tree = ast.parse((INTEGRATION / "llm.py").read_text(encoding="utf-8"))
    names = set()
    for node in tree.body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id in ("SPECS", "ENTITY_SPECS") for t in node.targets):
            names |= {key.value for key in node.value.keys if isinstance(key, ast.Constant)}
    return names


class DocsExamplesTests(unittest.TestCase):
    def test_the_code_of_each_csharp_node_shown_is_the_examples_word_for_word(self):
        # The plug's C# device and the thermometer's C# entity: the code the guide shows is the node's source.
        for title, kind in (("Plug in C#", "csharp_device"), ("Thermometer over HTTP", "csharp_entity")):
            with self.subTest(title):
                block = fenced_block_after(GUIDE.read_text(encoding="utf-8"), f"<!-- example: {title} -->", "csharp")
                self.assertEqual(block, class_node(title, kind)["config"]["source"])

    def test_the_csharp_nodes_shown_as_json_are_the_examples(self):
        # Each node and edge a document shows of a C# node example is the example's of the same id, word for word, but
        # that a C# node's source may be written as … for the code the guide shows; every node and edge of the graph is shown.
        canonical = lambda value: json.dumps(value, sort_keys=True)
        for doc, title in ((GUIDE, "Plug in C#"), (MCP, "Plug in C#"), (MCP, "Thermometer over HTTP")):
            graph = example_definition(title)["definition"]["logic"]["graph"]
            shown = json.loads(fenced_block_after(doc.read_text(encoding="utf-8"), f"<!-- example: {title}, its nodes -->", "json"))
            with self.subTest(doc.name, title=title):
                for part in ("nodes", "edges"):
                    own = {item["id"]: item for item in graph[part]}
                    self.assertEqual(sorted(item["id"] for item in shown[part]), sorted(own), part)
                    for item in shown[part]:
                        expected = json.loads(json.dumps(own[item["id"]]))
                        if part == "nodes" and expected["kind"] in ("csharp_entity", "csharp_device") and item["config"].get("source") == ELIDED:
                            expected["config"]["source"] = ELIDED
                        self.assertEqual(canonical(item), canonical(expected), item["id"])
                self.assertTrue(any(node["kind"] in ("csharp_entity", "csharp_device") for node in shown["nodes"]))

    def test_the_config_members_written_out_are_the_catalogs(self):
        # Wherever the members of a Device node's, a C# device node's or a C# entity node's config are written out (the MCP
        # guide's rules, get_node_schema's description and the three documents, each list marked <!-- config: <kind> -->),
        # they are the catalog's, in its order.
        catalog = json.loads((INTEGRATION / "visual_node_catalog.json").read_text(encoding="utf-8"))
        constants = llm_constants()
        texts = {'SEMANTICS["definitions"]': constants["SEMANTICS"]["definitions"], "get_node_schema": constants["SPECS"]["get_node_schema"]}
        docs = (GUIDE, MCP, ROOT / "docs/backend/entity-definitions.md")
        for kind in ("device", "csharp_device", "csharp_entity"):
            expected = list(catalog["nodes"][kind]["config"])
            for name, text in texts.items():
                with self.subTest(name, kind=kind):
                    found = re.search(rf"\bkind {kind}\b[^{{]*?config is (?=\{{)", text)
                    self.assertIsNotNone(found, f"{name} writes out no config of kind {kind}")
                    self.assertEqual(members(config_after(text, found.end())), expected)
            for doc in docs:
                text = doc.read_text(encoding="utf-8")
                marker = f"<!-- config: {kind} -->"
                with self.subTest(doc.name, kind=kind):
                    self.assertTrue(marker in text, f"{doc.name} marks no written-out config with {marker}")
                    start = 0
                    while (at := text.find(marker, start)) >= 0:
                        self.assertEqual(members(config_after(text, at + len(marker))), expected, f"the list after {marker} at offset {at}")
                        start = at + len(marker)

    def test_a_csharp_devices_settings_are_its_inputs(self):
        # A C# device has no device input and no entity is wired to it, but its settings are input pins, which take wires
        # (the plug example's Address does): no text says it has no inputs, and each says what it has instead.
        definitions = llm_constants()["SEMANTICS"]["definitions"]
        texts = {'SEMANTICS["definitions"]': definitions, **{doc.name: " ".join(doc.read_text(encoding="utf-8").split())
                                                           for doc in (GUIDE, MCP, ROOT / "docs/backend/entity-definitions.md")}}
        for name, text in texts.items():
            with self.subTest(name):
                self.assertIsNone(re.search(r"C# device node has no inputs|so nothing is connected to it", text))
        said = {
            'SEMANTICS["definitions"]': "a C# device node has no device input and no entity is wired to it (its entities are the ones its code adds): its only inputs are its setting pins",
            GUIDE.name: "so it has no **Entities** pin and no entity is wired to it; its inputs are its settings.",
            MCP.name: "C# device node has no `device` input and no entity is wired to it, its entities being the ones its code adds: its only inputs are its setting pins.",
            "entity-definitions.md": "a C# device node has no `device` input (its only inputs are its setting pins, and no entity is wired to it)",
        }
        for name, sentence in said.items():
            with self.subTest(name, said=True):
                self.assertIn(sentence, texts[name])

    def test_a_graph_of_only_csharp_nodes_needs_no_entity_node(self):
        # A graph whose only declarations are a C# entity or a C# device is a definition: no text says an Entity node is
        # needed, and the ones that name the refusal say a C# node is enough.
        texts = {doc.name: " ".join(doc.read_text(encoding="utf-8").split()) for doc in (GUIDE, MCP, ROOT / "docs/backend/entity-definitions.md")}
        for name, text in texts.items():
            with self.subTest(name):
                self.assertIsNone(re.search(r"Entity nodes \(kind `entity`, at least one\)|no Entity node gets|A graph with no Entity node is", text))
        said = {
            MCP.name: "One with no Entity node, C# entity node or C# device node gets `definition.entitiesRequired`",
            "entity-definitions.md": "A graph with no Entity node, C# entity node or C# device node (whose code makes its entities) is refused with `definition.entitiesRequired`",
        }
        for name, sentence in said.items():
            with self.subTest(name, said=True):
                self.assertIn(sentence, texts[name])

    def test_the_config_check_reads_a_list(self):
        # The parts the check above stands on: a list read with its marks, and a list that leaves a member out differs.
        self.assertEqual(members("`{key, name, manufacturer?,\n  via?}`"), ["key", "name", "manufacturer", "via"])
        self.assertEqual(members(config_after("Its config is `{key,\nname?}` and `{other}`", 0)), ["key", "name"])
        self.assertNotEqual(members("{key, via?}"), list(json.loads((INTEGRATION / "visual_node_catalog.json").read_text(encoding="utf-8"))["nodes"]["device"]["config"]))

    def test_the_derived_sensor_is_the_example_definition(self):
        block = fenced_block_after(GUIDE.read_text(encoding="utf-8"), "<!-- example: Motion in either room -->", "json")
        # Compared as canonical JSON text, so that true and 1 differ (as Python's True == 1 would not tell).
        canonical = lambda value: json.dumps(value, sort_keys=True)
        self.assertEqual(canonical(json.loads(block)), canonical(example_definition("Motion in either room")["definition"]))

    def test_the_entity_node_shown_for_authors_is_the_examples(self):
        # Each node and edge a guide shows of "Plug over HTTP" is the example's of the same id, word for word: its relay's
        # Entity node with its command edges, and its Device node with the wires to the Device pins of its entities.
        graph = example_definition("Plug over HTTP")["definition"]["logic"]["graph"]
        canonical = lambda value: json.dumps(value, sort_keys=True)
        for doc in (GUIDE, MCP):
            for marker, kind in (("<!-- example: Plug over HTTP -->", "entity"), ("<!-- example: Plug over HTTP, its device -->", "device")):
                shown = json.loads(fenced_block_after(doc.read_text(encoding="utf-8"), marker, "json"))
                with self.subTest(doc.name, kind=kind):
                    self.assertTrue(shown["nodes"] and shown["edges"])
                    for part in ("nodes", "edges"):
                        own = {item["id"]: item for item in graph[part]}
                        for item in shown[part]:
                            self.assertEqual(canonical(item), canonical(own.get(item["id"])), item["id"])
                    self.assertIn(kind, [node["kind"] for node in shown["nodes"]])
                    if kind == "device":
                        self.assertTrue(all(edge["sourcePort"] == "entities" and edge["targetPort"] == "device" for edge in shown["edges"]))

    def test_the_mcp_doc_names_only_real_tools_and_every_tool(self):
        known = tool_names()
        self.assertIn("entities_save", known)
        text = MCP.read_text(encoding="utf-8")
        called = set(CALLED.findall(text)) | {bare for _, bare in NAMED.findall(text) if bare}
        written = ({quoted or bare for quoted, bare in NAMED.findall(text)} & known) | called
        self.assertEqual(sorted(called - known), [], "tools the doc names that llm.py does not have")
        self.assertEqual(sorted(known - written), [], "tools the doc leaves out")

    def test_a_setting_can_be_an_entity_and_is_required_without_a_value(self):
        # What a setting can be, as the assistant's rules and the three documents say it: an entity handle too (the old
        # list of five types is gone), a string or entity setting with no initializer and no ? required, an entity pin
        # fed by no Setting step, and a required setting whose Setting has no value failing the start.
        definitions = llm_constants()["SEMANTICS"]["definitions"]
        texts = {'SEMANTICS["definitions"]': definitions, **{doc.name: " ".join(doc.read_text(encoding="utf-8").split())
                                                           for doc in (GUIDE, MCP, ROOT / "docs/backend/entity-definitions.md")}}
        for name, text in texts.items():
            with self.subTest(name):
                self.assertIsNone(re.search(r"a `?string`?, `?bool`?, `?int`?, `?long`?, `?double`? or (an )?enum", text))
                self.assertIn("LightEntity", text)
        said = {
            GUIDE.name: (
                "It may be a `string`, `bool`, `int`, `long`, `double`, an enum, or an entity: `Entity` for any entity, or a domain's class such as `LightEntity` or `SensorEntity` for one of that domain's.",
                "A `string` or entity setting with no initializer and no `?` is required:",
                "An entity setting takes no Setting step:",
                "the definition does not start, and says why: \"Hall: Pinger: the setting Host has no value\".",
                "**`Track(entity, change => …)`**",
                "**`Services`** calls Home Assistant's services with typed methods",
            ),
            MCP.name: (
                "`{\"kind\": \"literal\", \"type\": \"entity_id:light\", \"value\": \"light.kitchen\"}`",
                "A string or entity setting with no initializer and no `?` is required:",
                "An entity setting takes no Setting step",
                "`<definition>: <node name>: the setting <Label> has no value`",
            ),
            "entity-definitions.md": (
                "type, secret, options?, minimum?, maximum?, domain?, required?}]}`",
                "a setting's `type`, `secret`, `options`, `minimum`, `maximum`, `domain` or `required`",
                "\"`{Definition}: {Name}: the setting {Label} has no value`\"",
            ),
        }
        for name, sentences in said.items():
            for sentence in sentences:
                with self.subTest(name, said=sentence):
                    self.assertIn(sentence, texts[name])
        # The class the guide shows under Follow another entity is the one the runtime suite compiles (ServiceCallTests).
        block = fenced_block_after(GUIDE.read_text(encoding="utf-8"), "<!-- compiled: Doubled -->", "csharp")
        self.assertIn("public class Doubled : SharpSensor", block)

    def test_every_in_page_link_resolves_to_a_heading(self):
        # A link to #part, in its own document or another one, names a heading there, as GitHub makes its anchor.
        def anchors(path):
            found = set()
            for line in path.read_text(encoding="utf-8").splitlines():
                if line.startswith("#"):
                    words = line.lstrip("#").strip().lower()
                    found.add(re.sub(r"[^\w\- ]", "", words).replace(" ", "-"))
            return found
        for doc in DOCS:
            for target in LINK.findall(doc.read_text(encoding="utf-8")):
                if re.match(r"[a-z]+:", target) or "#" not in target:
                    continue
                file, part = target.split("#", 1)
                path = (doc.parent / file).resolve() if file else doc
                if path.suffix != ".md" or not path.exists():
                    continue
                with self.subTest(doc.name, target=target):
                    self.assertIn(part, anchors(path), f"{doc.relative_to(ROOT)} links to {target}, a heading {path.name} does not have")

    def test_every_relative_link_resolves_to_a_file(self):
        for doc in DOCS:
            for target in LINK.findall(doc.read_text(encoding="utf-8")):
                if re.match(r"[a-z]+:", target) or target.startswith("#"):
                    continue
                path = (doc.parent / target.split("#", 1)[0]).resolve()
                self.assertTrue(path.exists(), f"{doc.relative_to(ROOT)} links to {target}, which does not exist")


if __name__ == "__main__":
    unittest.main()
