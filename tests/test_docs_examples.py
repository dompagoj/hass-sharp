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
DOCS = (ROOT / "README.md", GUIDE, ROOT / "docs/backend/entity-definitions.md", ROOT / "docs/node-libraries.md", MCP)
# A tool named in the MCP doc: its full name, or an entity definition tool's name without the prefix.
TOOL_NAME = re.compile(r"hass_sharp__([a-z][a-z_]*)|(?<![A-Za-z0-9_])(entities_[a-z_]+)")
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
    def test_the_class_example_is_the_example_library_word_for_word(self):
        block = fenced_block_after(GUIDE.read_text(encoding="utf-8"), "<!-- example: Plug from a class -->", "csharp")
        self.assertEqual(block, example_definition("Plug from a class")["library"]["source"])

    def test_the_derived_sensor_is_the_example_definition(self):
        block = fenced_block_after(GUIDE.read_text(encoding="utf-8"), "<!-- example: Motion in either room -->", "json")
        self.assertEqual(json.loads(block), example_definition("Motion in either room")["definition"])

    def test_the_mcp_doc_names_only_real_tools_and_every_entity_tool(self):
        known = tool_names()
        self.assertIn("entities_save", known)
        written = {full or bare for full, bare in TOOL_NAME.findall(MCP.read_text(encoding="utf-8"))}
        self.assertEqual(sorted(written - known), [], "tools the doc names that llm.py does not have")
        self.assertEqual(sorted(name for name in known if name.startswith("entities_") and name not in written), [],
                         "entity definition tools the doc leaves out")

    def test_every_relative_link_resolves_to_a_file(self):
        for doc in DOCS:
            for target in LINK.findall(doc.read_text(encoding="utf-8")):
                if re.match(r"[a-z]+:", target) or target.startswith("#"):
                    continue
                path = (doc.parent / target.split("#", 1)[0]).resolve()
                self.assertTrue(path.exists(), f"{doc.relative_to(ROOT)} links to {target}, which does not exist")


if __name__ == "__main__":
    unittest.main()
