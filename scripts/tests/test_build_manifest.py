"""PB12: build-manifest.py turns meta.json folders into a manifest and flat release assets."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent.parent


def _load():
    spec = importlib.util.spec_from_file_location("build_manifest", SCRIPTS / "build-manifest.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


bm = _load()


class BuildManifestTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        self.models, self.stage = root / "models", root / "stage"
        self.payload = b"onnx bytes"
        folder = self.models / "tiny-x4"
        folder.mkdir(parents=True)
        (folder / "fp32.onnx").write_bytes(self.payload)
        self.meta = {
            "name": "tiny-x4",
            "task": "upscale",
            "variants": {
                "fp32": {"file": "fp32.onnx", "bytes": len(self.payload), "sha256": hashlib.sha256(self.payload).hexdigest()}
            },
            "upstream": {"license": "BSD-3-Clause", "license_url": "https://example.org/LICENSE"},
        }
        (folder / "meta.json").write_text(json.dumps(self.meta), encoding="utf-8")

    def test_manifest_lists_urls_sizes_and_hashes(self):
        manifest = bm.build(self.models, self.stage, "https://host/models/v1/", "1")
        model = manifest["models"][0]
        self.assertEqual("https://host/models/v1/tiny-x4-fp32.onnx", model["variants"]["fp32"]["url"])
        self.assertEqual(hashlib.sha256(self.payload).hexdigest(), model["variants"]["fp32"]["sha256"])
        self.assertEqual("https://host/models/v1/tiny-x4-meta.json", model["meta"]["url"])
        self.assertEqual("BSD-3-Clause", model["license"])
        self.assertEqual(manifest, json.loads((self.stage / "manifest.json").read_text(encoding="utf-8")))

    def test_files_are_staged_under_flat_asset_names(self):
        bm.build(self.models, self.stage, "https://host", "1")
        self.assertEqual(self.payload, (self.stage / "tiny-x4-fp32.onnx").read_bytes())
        self.assertTrue((self.stage / "tiny-x4-meta.json").is_file())

    def test_a_file_that_differs_from_meta_json_is_refused(self):
        (self.models / "tiny-x4" / "fp32.onnx").write_bytes(b"changed!!!")
        with self.assertRaises(bm.ManifestError):
            bm.build(self.models, self.stage, "https://host", "1")

    def test_the_catalog_is_schema_2_with_weights_by_quantisation(self):
        # PB70: the same models, each with a version, a kind and its variants as weights files.
        manifest = bm.build(self.models, self.stage, "https://host", "1")
        catalog = bm.catalog(manifest, 3, versions={"tiny-x4": "2"})
        model = catalog["models"][0]
        self.assertEqual((2, 3), (catalog["schema"], catalog["sequence"]))
        self.assertEqual(("2", "onnx", False), (model["version"], model["kind"], model["non_commercial"]))
        self.assertEqual([{"role": "weights", "file": manifest["models"][0]["variants"]["fp32"]}], model["quantisations"]["fp32"]["files"])

    def test_the_generation_models_are_pinned_upstream_by_role(self):
        # PB79: each quantisation is its transformer plus the shared text encoder and VAE.
        sources = json.loads((SCRIPTS / "generation-sources.json").read_text(encoding="utf-8"))
        entries = bm.diffusion(sources)
        self.assertEqual(["z-image-turbo", "flux-2-klein-4b", "qwen-image"], [e["name"] for e in entries])
        for entry in entries:
            self.assertEqual(("diffusion", False), (entry["kind"], entry["non_commercial"]))
            for quantisation in entry["quantisations"].values():
                self.assertEqual(["transformer", "text-encoder", "vae"], [f["role"] for f in quantisation["files"]])
                for listed in quantisation["files"]:
                    self.assertRegex(listed["file"]["url"], r"^https://huggingface\.co/.+/resolve/[0-9a-f]{40}/")
                    self.assertRegex(listed["file"]["sha256"], r"^[0-9a-f]{64}$")
        qwen = entries[2]["quantisations"]["q4_0"]
        self.assertEqual(11852773920 + (2 << 30), qwen["memory"]["video_bytes"])
        self.assertEqual(4683072512 + 253806246 + (1 << 30), qwen["memory"]["system_bytes"])

    def test_a_generation_file_not_pinned_to_a_commit_is_refused(self):
        sources = json.loads((SCRIPTS / "generation-sources.json").read_text(encoding="utf-8"))
        sources["models"][0]["quantisations"]["q4_k"]["commit"] = "main"
        with self.assertRaises(bm.ManifestError):
            bm.diffusion(sources)

    def test_the_catalog_appends_the_generation_models(self):
        manifest = bm.build(self.models, self.stage, "https://host", "1")
        sources = json.loads((SCRIPTS / "generation-sources.json").read_text(encoding="utf-8"))
        names = [m["name"] for m in bm.catalog(manifest, 1, generation=sources)["models"]]
        self.assertEqual(["tiny-x4", "z-image-turbo", "flux-2-klein-4b", "qwen-image"], names)

    def test_a_catalog_sequence_below_1_is_refused(self):
        manifest = bm.build(self.models, self.stage, "https://host", "1")
        with self.assertRaises(bm.ManifestError):
            bm.catalog(manifest, 0)

    def test_no_models_is_refused(self):
        with self.assertRaises(bm.ManifestError):
            bm.build(self.stage, self.stage, "https://host", "1")


if __name__ == "__main__":
    unittest.main()
