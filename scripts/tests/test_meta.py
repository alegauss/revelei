"""PB11: every meta.json - the committed example and whatever the export script has emitted -
holds to scripts/meta.schema.json, the contract Revelei.Inference's ModelMeta reads.

Needs jsonschema (scripts/requirements.txt); without it the class is skipped and unittest says so.
"""

from __future__ import annotations

import json
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent.parent
REPO = SCRIPTS.parent

try:
    import jsonschema
except ImportError:
    jsonschema = None


@unittest.skipIf(jsonschema is None, "jsonschema not installed (scripts/requirements.txt)")
class MetaSchemaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        schema = json.loads((SCRIPTS / "meta.schema.json").read_text(encoding="utf-8"))
        jsonschema.Draft202012Validator.check_schema(schema)
        cls.validator = jsonschema.Draft202012Validator(schema)

    def assert_valid(self, path: Path):
        errors = sorted(self.validator.iter_errors(json.loads(path.read_text(encoding="utf-8"))), key=str)
        self.assertEqual([], [f"{'/'.join(map(str, e.path))}: {e.message}" for e in errors], str(path))

    def test_the_committed_example_is_valid(self):
        self.assert_valid(SCRIPTS / "meta.example.json")

    def test_every_emitted_meta_json_is_valid(self):
        emitted = sorted((REPO / ".cache" / "models").glob("*/meta.json"))
        for path in emitted:
            with self.subTest(path.parent.name):
                self.assert_valid(path)

    def test_an_unknown_field_is_refused(self):
        meta = json.loads((SCRIPTS / "meta.example.json").read_text(encoding="utf-8"))
        meta["colour"] = "rgb"
        self.assertFalse(self.validator.is_valid(meta))


if __name__ == "__main__":
    unittest.main()
