"""PB70: sign-catalog.py signs catalog.json the way Revelei.Inference's ModelCatalog verifies.

Needs `cryptography` (scripts/requirements.txt); without it the class is skipped and unittest
says so in its summary. tests/fixtures/catalog holds a catalog this script signed with a
throwaway key, which ModelCatalogTests verifies on the C# side.
"""

from __future__ import annotations

import base64
import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent.parent
FIXTURE = SCRIPTS.parent / "tests" / "fixtures" / "catalog"

try:
    import cryptography  # noqa: F401
except ImportError:
    cryptography = None


def _load():
    spec = importlib.util.spec_from_file_location("sign_catalog", SCRIPTS / "sign-catalog.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


@unittest.skipIf(cryptography is None, "cryptography not installed (scripts/requirements.txt)")
class SignCatalogTests(unittest.TestCase):
    def setUp(self):
        self.sc = _load()
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def test_a_signature_verifies_and_is_the_64_byte_p1363_form(self):
        spki = self.sc.new_key(self.root / "key.pem")
        signature = self.sc.sign(self.sc.load(self.root / "key.pem"), b'{"schema": 2}')
        self.assertEqual(64, len(base64.b64decode(signature)))
        self.assertTrue(self.sc.verify(spki, b'{"schema": 2}', signature))
        self.assertFalse(self.sc.verify(spki, b'{"schema": 3}', signature))

    def test_an_existing_key_is_never_overwritten(self):
        self.sc.new_key(self.root / "key.pem")
        before = (self.root / "key.pem").read_bytes()
        with self.assertRaises(FileExistsError):
            self.sc.new_key(self.root / "key.pem")
        self.assertEqual(before, (self.root / "key.pem").read_bytes())

    def test_main_writes_the_sig_beside_the_catalog(self):
        self.sc.new_key(self.root / "key.pem")
        (self.root / "catalog.json").write_bytes(b'{"schema": 2}\n')
        self.assertEqual(0, self.sc.main(["--key", str(self.root / "key.pem"), str(self.root / "catalog.json")]))
        signature = (self.root / "catalog.json.sig").read_text(encoding="ascii").strip()
        self.assertTrue(self.sc.verify(self.sc.public_key(self.sc.load(self.root / "key.pem")), b'{"schema": 2}\n', signature))

    def test_the_fixture_the_csharp_side_reads_verifies(self):
        spki = (FIXTURE / "public-key.txt").read_text(encoding="ascii").strip()
        signature = (FIXTURE / "catalog.json.sig").read_text(encoding="ascii").strip()
        self.assertTrue(self.sc.verify(spki, (FIXTURE / "catalog.json").read_bytes(), signature))


if __name__ == "__main__":
    unittest.main()
