"""Pure descriptor/schema guards; no native filesystem, listener or game proof."""
import json
from pathlib import Path
import re
import unittest

from test_protocol_schema import accepts

ROOT = Path(__file__).resolve().parent.parent
SCHEMA = json.loads((ROOT / "protocol/discovery-v1.schema.json").read_text())
FIXTURES = json.loads((ROOT / "protocol/fixtures/discovery.json").read_text())


class DiscoveryContractTests(unittest.TestCase):
    def test_shared_fixtures_match_schema(self):
        for record in FIXTURES["accepted"]:
            self.assertTrue(accepts(record, SCHEMA))
            self.assertLessEqual(len(json.dumps(record).encode()), 1024)
        for record in FIXTURES["rejected"]:
            self.assertFalse(accepts(record, SCHEMA))

    def test_schema_closed_and_private(self):
        self.assertFalse(SCHEMA["additionalProperties"])
        self.assertEqual(set(SCHEMA["required"]), {"v", "session", "host", "port", "token"})
        self.assertEqual(set(SCHEMA["properties"]), set(SCHEMA["required"]))
        self.assertTrue(SCHEMA["properties"]["token"]["writeOnly"])
        self.assertEqual(SCHEMA["properties"]["host"]["const"], "127.0.0.1")
        self.assertEqual(SCHEMA["properties"]["v"]["const"], 1)
        self.assertEqual(SCHEMA["properties"]["port"]["minimum"], 1)
        self.assertEqual(SCHEMA["properties"]["port"]["maximum"], 65535)

    def test_schema_rejects_missing_extra_and_wrong_types(self):
        record = FIXTURES["accepted"][0]
        for key in record:
            without = record.copy()
            del without[key]
            self.assertFalse(accepts(without, SCHEMA))
            self.assertFalse(accepts(record | {key: None}, SCHEMA))
        for host in ("localhost", "127.1", "::1", "0.0.0.0", "192.0.2.1"):
            self.assertFalse(accepts(record | {"host": host}, SCHEMA))
        for port in (0, 65536, "1", True, 1.5):
            self.assertFalse(accepts(record | {"port": port}, SCHEMA))
        for field in ("pid", "path", "$type"):
            self.assertFalse(accepts(record | {field: "x"}, SCHEMA))
        for field in ("session", "token"):
            self.assertFalse(accepts(record | {field: record[field] + "\n"}, SCHEMA))

    def test_documented_limits_match_executable_contract(self):
        source = (ROOT / "shared/Protocol/Discovery.cs").read_text()
        expected = {"DescriptorBytes": 1024, "DirectoryEntries": 64, "Records": 16, "Probes": 16,
                    "ConcurrentProbes": 1, "ProbeTimeoutMs": 1000, "TotalProbeTimeoutMs": 5000,
                    "RuntimePathChars": 1024}
        for name, limit in expected.items():
            self.assertRegex(source, rf"const int {name} = {limit};")
        self.assertIn("const int TotalRecordBytes = Records * DescriptorBytes;", source)
        self.assertIn("StrictJson.Parse", source)
        self.assertNotRegex(re.sub(r"//[^\n]*", "", source), r"\b(?:Directory|File|Dns|Process)\.")


if __name__ == "__main__":
    unittest.main()
