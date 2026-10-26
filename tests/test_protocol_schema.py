"""Asset-free schema/fixture/linkage guards. Subset evaluator is fixture tooling, not ingress.

C# explicit field readers are wire enforcement. This deliberately small evaluator exercises
only keywords used by this repository's schema; it is not a general JSON Schema library.
"""
import json
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
SCHEMA = json.loads((ROOT / "protocol/wire-v1.schema.json").read_text())


def accepts(value, schema):
    if "$ref" in schema:
        target = SCHEMA
        for part in schema["$ref"].removeprefix("#/").split("/"):
            target = target[part]
        if not accepts(value, target):
            return False
    if "const" in schema and value != schema["const"]:
        return False
    if "enum" in schema and value not in schema["enum"]:
        return False
    if "type" in schema:
        types = {"object": lambda: isinstance(value, dict), "array": lambda: isinstance(value, list),
                 "string": lambda: isinstance(value, str), "integer": lambda: type(value) is int,
                 "number": lambda: type(value) in (int, float), "boolean": lambda: type(value) is bool}
        if not types[schema["type"]]():
            return False
    if isinstance(value, dict):
        if any(key not in value for key in schema.get("required", [])):
            return False
        properties = schema.get("properties", {})
        if schema.get("additionalProperties") is False and value.keys() - properties.keys():
            return False
        if any(not accepts(value[key], rule) for key, rule in properties.items() if key in value):
            return False
        for key, required in schema.get("dependentRequired", {}).items():
            if key in value and any(name not in value for name in required):
                return False
    if isinstance(value, list):
        if len(value) < schema.get("minItems", 0) or len(value) > schema.get("maxItems", float("inf")):
            return False
        if "items" in schema and any(not accepts(item, schema["items"]) for item in value):
            return False
    if isinstance(value, str):
        if len(value) > schema.get("maxLength", float("inf")):
            return False
        if "pattern" in schema and re.search(schema["pattern"], value) is None:
            return False
    if type(value) in (int, float):
        if value < schema.get("minimum", -float("inf")) or value > schema.get("maximum", float("inf")):
            return False
        if value <= schema.get("exclusiveMinimum", -float("inf")):
            return False
    if "allOf" in schema and not all(accepts(value, item) for item in schema["allOf"]):
        return False
    if "oneOf" in schema and sum(accepts(value, item) for item in schema["oneOf"]) != 1:
        return False
    if "not" in schema and accepts(value, schema["not"]):
        return False
    if "if" in schema:
        branch = "then" if accepts(value, schema["if"]) else "else"
        if branch in schema and not accepts(value, schema[branch]):
            return False
    return True


class ProtocolSchemaTests(unittest.TestCase):
    def test_schema_local_references_resolve_and_fixture_keywords_known(self):
        allowed = {"$schema", "$id", "$ref", "title", "description", "$defs", "type", "enum", "const",
                   "pattern", "maxLength", "minimum", "maximum", "exclusiveMinimum", "required",
                   "properties", "additionalProperties", "dependentRequired", "allOf", "oneOf",
                   "if", "then", "else", "not", "items", "maxItems", "minItems", "writeOnly"}

        def visit(schema):
            self.assertFalse(schema.keys() - allowed)
            if "$ref" in schema:
                self.assertTrue(schema["$ref"].startswith("#/$defs/"))
                self.assertIn(schema["$ref"].split("/")[-1], SCHEMA["$defs"])
            for key in ("$defs", "properties"):
                for child in schema.get(key, {}).values():
                    visit(child)
            for key in ("allOf", "oneOf"):
                for child in schema.get(key, []):
                    visit(child)
            for key in ("if", "then", "else", "not", "items"):
                if key in schema:
                    visit(schema[key])
        visit(SCHEMA)

    def test_request_schema_fixtures(self):
        fixtures = json.loads((ROOT / "protocol/fixtures/requests.json").read_text())
        for request in fixtures["accepted"]:
            with self.subTest(method=request["method"]):
                self.assertTrue(accepts(request, SCHEMA["$defs"]["request"]))
        for entry in fixtures["rejected"]:
            with self.subTest(entry=entry):
                self.assertFalse(accepts(entry["request"], SCHEMA["$defs"]["request"]))

    def test_response_schema_fixtures(self):
        fixtures = json.loads((ROOT / "protocol/fixtures/responses.json").read_text())
        for response in fixtures["accepted"]:
            with self.subTest(id=response.get("id")):
                self.assertTrue(accepts(response, SCHEMA))
        for response in fixtures["rejected"]:
            with self.subTest(response=response):
                self.assertFalse(accepts(response, SCHEMA))

    def test_both_endpoints_and_tests_link_same_protocol(self):
        for relative in ("mod/RimPilot.Mod.csproj", "cli/RimPilot.Cli.csproj", "tests/JsonIngress/JsonIngress.csproj"):
            project = ROOT / relative
            paths = [(project.parent / item.get("Project")).resolve()
                     for item in ET.parse(project).findall(".//Import") if "$" not in item.get("Project")]
            self.assertIn(ROOT / "shared/Protocol/Protocol.props", paths)
        props = ET.parse(ROOT / "shared/Protocol/Protocol.props")
        self.assertEqual(props.find(".//Compile").get("Include"), "$(MSBuildThisFileDirectory)*.cs")
        self.assertEqual(props.findall(".//Reference"), [])
        self.assertEqual(props.findall(".//ProjectReference"), [])

    def test_no_raw_source_progress_schema_fields_or_object_serializer(self):
        fields = set()

        def visit(value):
            if isinstance(value, dict):
                fields.update(value.get("properties", {}))
                for child in value.values():
                    visit(child)
            elif isinstance(value, list):
                for child in value:
                    visit(child)
        visit(SCHEMA)
        self.assertFalse(fields & {"total", "offset", "scanned", "candidateIndex", "rawCount", "lostCount"})
        for source in (ROOT / "shared/Protocol").glob("*.cs"):
            text = re.sub(r"//[^\n]*", "", source.read_text())
            self.assertNotRegex(text, r"\b(?:Deserialize\w*|ToObject|FromObject|SerializeObject)\s*(?:<|\()")
            self.assertNotRegex(text, r"\b(?:JsonSerializer|TypeNameHandling|Activator)\b")
            self.assertNotIn("System.Reflection", text)


if __name__ == "__main__":
    unittest.main()
