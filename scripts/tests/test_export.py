"""PB8: export-and-quantize.py on a tiny RRDBNet, so the path the real weights take runs in seconds.

Needs the export requirements (scripts/requirements.txt); without torch the whole class is
skipped and unittest says so in its summary.
"""

from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent.parent

try:
    import numpy as np
    import onnx
    import torch
    from PIL import Image
except ImportError:
    torch = None

try:
    import jsonschema
except ImportError:
    jsonschema = None


def _load_export_module():
    spec = importlib.util.spec_from_file_location("export_and_quantize", SCRIPTS / "export-and-quantize.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


@unittest.skipIf(torch is None, "export requirements not installed (scripts/requirements.txt)")
class ExportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.ex = _load_export_module()
        from archs.rrdbnet import RRDBNet

        cls.RRDBNet = RRDBNet

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)

    def tiny(self, scale: int, seed: int = 0):
        torch.manual_seed(seed)
        return self.RRDBNet(scale=scale, num_feat=8, num_block=1, num_grow_ch=4).eval()

    def spec(self, scale: int, **kwargs):
        return self.ex.Export(f"tiny-x{scale}", "unused.pth", lambda folder: self.tiny(scale), 2 if scale == 2 else 1, **kwargs)

    def exported(self, scale: int, **kwargs):
        model, spec = self.tiny(scale), self.spec(scale, **kwargs)
        fp32, fp16 = self.folder / "fp32.onnx", self.folder / "fp16.onnx"
        self.ex.export_fp32(model, spec, fp32)
        self.ex.export_fp16(fp32, fp16)
        return model, spec, fp32, fp16

    def test_axes_are_dynamic_and_named(self):
        _, _, fp32, _ = self.exported(4)
        graph = onnx.load(str(fp32)).graph
        dims = [d.dim_param for d in graph.input[0].type.tensor_type.shape.dim]
        self.assertEqual(["batch", "", "height", "width"], dims)
        self.assertEqual(self.ex.OPSET, onnx.load(str(fp32)).opset_import[0].version)

    def test_x4_matches_torch_at_untraced_sizes(self):
        model, spec, fp32, fp16 = self.exported(4)
        lines, fp16_ok = self.ex.verify(model, spec, fp32, fp16)
        self.assertIn("24x40 -> 96x160", lines[0])
        self.assertTrue(fp16_ok)

    def test_x2_unshuffle_matches_torch(self):
        model, spec, fp32, fp16 = self.exported(2)
        lines, _ = self.ex.verify(model, spec, fp32, fp16)
        self.assertIn("48x80 -> 96x160", lines[0])

    def test_fixed_size_export_has_no_dynamic_axes(self):
        # GFPGAN's shape: one aligned crop at a fixed size, input in [-1, 1].
        model, spec, fp32, _ = self.exported(4, size=16, input_range=(-1.0, 1.0), output_range=(-1.0, 1.0))
        dims = [d.dim_value for d in onnx.load(str(fp32)).graph.input[0].type.tensor_type.shape.dim]
        self.assertEqual([1, 3, 16, 16], dims)
        lines, _ = self.ex.verify(model, spec, fp32, None)
        self.assertIn("16x16 -> 64x64", lines[0])

    def test_a_model_that_differs_from_its_source_fails(self):
        _, spec, fp32, _ = self.exported(4)
        other = self.tiny(4, seed=1)
        with self.assertRaises(self.ex.ExportError):
            self.ex.verify(other, spec, fp32, None)

    def test_an_fp16_variant_below_the_threshold_is_not_kept(self):
        model, spec, fp32, _ = self.exported(4)
        # A different model's FP16 output stands in for a conversion that lost too much.
        wrong = self.folder / "wrong.onnx"
        self.ex.export_fp32(self.tiny(4, seed=1), spec, wrong)
        _, fp16_ok = self.ex.verify(model, spec, fp32, wrong)
        self.assertFalse(fp16_ok)

    def test_int8_quantizes_every_conv_and_loads_on_cpu(self):
        # PB9: dynamic INT8 with unsigned weights; the report names what stayed float.
        _, _, fp32, _ = self.exported(4)
        int8 = self.folder / "int8.onnx"
        line = self.ex.quantize_int8(fp32, int8)
        ops = {node.op_type for node in onnx.load(str(int8)).graph.node}
        self.assertIn("ConvInteger", ops)
        self.assertNotIn("Conv", ops)
        self.assertIn("float compute ops left: none", line)
        weights = [i for i in onnx.load(str(int8)).graph.initializer if i.name.endswith("_quantized")]
        self.assertTrue(weights)
        self.assertTrue(all(w.data_type == onnx.TensorProto.UINT8 for w in weights))

    def validation_folder(self, floor: float):
        """A tiny x4 export with an int8 variant, and a test image, laid out as the script expects."""
        model, _, fp32, _ = self.exported(4)
        self.ex.quantize_int8(fp32, self.folder / "int8.onnx")
        images = self.folder / "test-images"
        images.mkdir()
        rng = np.random.default_rng(3)
        Image.fromarray(rng.integers(0, 256, (20, 30, 3), dtype=np.uint8)).save(images / "probe.png")
        return self.spec(4, test_image="probe.png", int8_min_psnr=floor), images

    def test_validation_writes_psnr_and_ssim_to_meta_json(self):
        # PB10: the numbers the README table reads.
        spec, images = self.validation_folder(floor=0.0)
        lines = self.ex.validate(spec, self.folder, images)
        meta = json.loads((self.folder / "meta.json").read_text(encoding="utf-8"))["validation"]
        self.assertEqual([1, 3, 20, 30], meta["input"])
        self.assertIn("psnr_db", meta["int8"])
        self.assertIn("ssim", meta["fp16"])
        self.assertEqual(2, len(lines))

    def test_int8_below_its_floor_fails_the_script(self):
        spec, images = self.validation_folder(floor=200.0)
        with self.assertRaises(self.ex.ExportError):
            self.ex.validate(spec, self.folder, images)

    @unittest.skipIf(jsonschema is None, "jsonschema not installed")
    def test_describe_writes_a_schema_valid_meta_json_and_keeps_validation(self):
        # PB11: the whole file the app reads, beside PB10's validation key.
        spec, images = self.validation_folder(floor=0.0)
        spec = self.ex.Export(**{**spec.__dict__, "scale": 4, "source": "realesrgan-x4plus", "tile": 256})
        model = self.tiny(4)
        self.ex.validate(spec, self.folder, images)
        self.ex.describe(spec, model, self.folder)
        meta = json.loads((self.folder / "meta.json").read_text(encoding="utf-8"))
        schema = json.loads((SCRIPTS / "meta.schema.json").read_text(encoding="utf-8"))
        jsonschema.validate(meta, schema)
        self.assertIn("validation", meta)
        self.assertEqual("dynamic", meta["input"]["batch"])
        self.assertEqual(hashlib.sha256((self.folder / "int8.onnx").read_bytes()).hexdigest(), meta["variants"]["int8"]["sha256"])
        # Tiny RRDBNet: 8 features at 4x the side, in FP32 bytes.
        self.assertEqual(8 * 16 * 4, meta["tile"]["largest_activation_bytes_per_pixel"])

    @unittest.skipIf(jsonschema is None, "jsonschema not installed")
    def test_fixtures_are_nearest_upscalers_with_schema_valid_meta(self):
        # PB42: the stand-ins UI cases run, and the committed copies must be what the script writes.
        import onnxruntime as ort

        schema = json.loads((SCRIPTS / "meta.schema.json").read_text(encoding="utf-8"))
        committed = SCRIPTS.parent / "cases" / "fixtures" / "models"
        rng = np.random.default_rng(7)
        image = rng.random((1, 3, 5, 7)).astype(np.float32)
        for name in self.ex.write_fixtures(self.folder):
            meta = json.loads((self.folder / name / "meta.json").read_text(encoding="utf-8"))
            jsonschema.validate(meta, schema)
            scale = meta["scale"]
            session = ort.InferenceSession(str(self.folder / name / "fp32.onnx"), providers=["CPUExecutionProvider"])
            out = session.run(None, {"input": image})[0]
            np.testing.assert_array_equal(image.repeat(scale, axis=2).repeat(scale, axis=3), out)
            for file in ("fp16.onnx", "fp32.onnx", "meta.json"):
                self.assertEqual((committed / name / file).read_bytes(), (self.folder / name / file).read_bytes(), f"{name}/{file}")

    @unittest.skipIf(jsonschema is None, "jsonschema not installed")
    def test_download_fixtures_are_a_mean_matter_the_committed_manifests_describe(self):
        # PB59: what the cases' fixture server serves, and the committed copies must be what the script writes.
        import onnxruntime as ort

        schema = json.loads((SCRIPTS / "meta.schema.json").read_text(encoding="utf-8"))
        committed = SCRIPTS.parent / "cases" / "fixtures" / "download"
        written = self.ex.write_download_fixtures(self.folder)
        jsonschema.validate(json.loads((self.folder / "rmbg-1.4-meta.json").read_text(encoding="utf-8")), schema)
        image = np.random.default_rng(7).random((1, 3, 32, 32)).astype(np.float32)
        session = ort.InferenceSession(str(self.folder / "rmbg-1.4-fp32.onnx"), providers=["CPUExecutionProvider"])
        np.testing.assert_allclose(image.mean(axis=1, keepdims=True), session.run(None, {"input": image})[0], rtol=1e-5)
        manifest = json.loads((self.folder / "manifest.json").read_text(encoding="utf-8"))
        onnx_entry = manifest["models"][0]["variants"]["fp32"]
        self.assertEqual(hashlib.sha256((self.folder / "rmbg-1.4-fp32.onnx").read_bytes()).hexdigest(), onnx_entry["sha256"])
        self.assertTrue(onnx_entry["url"].startswith("http://127.0.0.1:"))
        for file in written:
            self.assertEqual((committed / file).read_bytes(), (self.folder / file).read_bytes(), file)

    def test_yunet_heads_decode_as_opencv_does(self):
        # PB30: the same anchor Revelei.Inference's FaceDetectionTests decodes, so both sides agree.
        heads = {}
        for stride in self.ex.DETECTOR_STRIDES:
            n = (self.ex.DETECTOR_SIZE // stride) ** 2
            heads.update({f"cls_{stride}": np.zeros((1, n, 1)), f"obj_{stride}": np.ones((1, n, 1)),
                          f"bbox_{stride}": np.zeros((1, n, 4)), f"kps_{stride}": np.zeros((1, n, 10))})
        cols = self.ex.DETECTOR_SIZE // 8
        i = cols + 1
        heads["cls_8"][0, i, 0] = 0.81
        heads["bbox_8"][0, i] = [0.5, -0.25, np.log(2), 0]
        heads["kps_8"][0, i] = [0, 0, 1, 0, 0.5, 0.5, 0, 1, 1, 1]
        [(score, values)] = self.ex.yunet_faces(heads, threshold=0.5)
        self.assertAlmostEqual(0.9, score, places=6)
        np.testing.assert_allclose([4, 2, 16, 8, 8, 8, 16, 8, 12, 12, 8, 16, 16, 16], values, atol=1e-9)

    @unittest.skipIf(jsonschema is None, "jsonschema not installed")
    def test_the_adopted_detector_finds_a_face_and_writes_a_schema_valid_meta_json(self):
        weights = self.ex.WEIGHTS
        if not (weights / self.ex.DETECTOR_FILE).is_file() or not (weights / "test-images" / self.ex.DETECTOR_IMAGE).is_file():
            self.skipTest("needs scripts/download_models.py to have run")
        lines = self.ex.adopt_detector(weights, self.folder)
        meta = json.loads((self.folder / self.ex.DETECTOR / "meta.json").read_text(encoding="utf-8"))
        jsonschema.validate(meta, json.loads((SCRIPTS / "meta.schema.json").read_text(encoding="utf-8")))
        self.assertEqual(("face-detection", "BGR", "yunet"), (meta["task"], meta["input"]["colour_order"], meta["output"]["decoder"]))
        self.assertEqual(["fp32"], list(meta["variants"]))
        self.assertIn("anchor(s) over 0.9", lines[0])

    def test_scunet_masks_shifted_windows_as_upstream_does(self):
        # PB36: the band-label mask that replaced upstream's 6-D one (which crashed DirectML).
        from archs.scunet import WMSA

        def upstream(h: int, w: int, p: int, shift: int):
            mask = torch.zeros(h, w, p, p, p, p, dtype=torch.bool)
            s = p - shift
            mask[-1, :, :s, :, s:, :] = True
            mask[-1, :, s:, :, :s, :] = True
            mask[:, -1, :, :s, :, s:] = True
            mask[:, -1, :, s:, :, :s] = True
            return mask.reshape(h * w, p * p, p * p)

        attention = WMSA(32, 32, 32, 8, "SW")
        for height, width in ((16, 24), (32, 8), (8, 8)):
            with self.subTest(height=height, width=width):
                expected = upstream(height // 8, width // 8, 8, 4)
                self.assertTrue(torch.equal(expected, attention.generate_mask(height, width)))

    def test_scunet_window_partition_round_trips(self):
        from archs.scunet import WMSA

        attention = WMSA(32, 32, 32, 8, "W")
        x = torch.rand(2, 16, 24, 5)
        windows = attention.partition(x)
        self.assertEqual((2 * 2 * 3, 64, 5), tuple(windows.shape))
        # The second window of the first image is rows 0-7, columns 8-15.
        self.assertTrue(torch.equal(x[0, 0:8, 8:16].reshape(64, 5), windows[1]))
        self.assertTrue(torch.equal(x, attention.unpartition(windows, 2, 16, 24)))

    def test_ssim_is_one_for_identical_images_and_lower_otherwise(self):
        rng = np.random.default_rng(5)
        a = rng.random((1, 3, 32, 32)).astype(np.float32)
        self.assertAlmostEqual(1.0, self.ex.ssim(a, a), places=6)
        self.assertLess(self.ex.ssim(a, np.clip(a + rng.normal(0, 0.2, a.shape), 0, 1)), 0.9)

    def test_every_model_has_a_test_image_and_an_int8_floor(self):
        for spec in self.ex.EXPORTS:
            with self.subTest(spec.name):
                self.assertTrue(spec.test_image)
                self.assertGreater(spec.int8_min_psnr, 0)

    def test_psnr_uses_the_output_range(self):
        a = np.zeros((1, 1, 4, 4), dtype=np.float32)
        b = np.full((1, 1, 4, 4), 0.1, dtype=np.float32)
        self.assertAlmostEqual(20.0, self.ex.psnr(a, b, (0.0, 1.0)), places=3)
        self.assertAlmostEqual(26.0206, self.ex.psnr(a, b, (-1.0, 1.0)), places=3)

    def test_missing_weights_name_the_download_script(self):
        spec = self.ex.EXPORTS[0]
        with self.assertRaises(self.ex.ExportError) as caught:
            self.ex.load(spec, self.folder)
        self.assertIn("download_models.py", str(caught.exception))

    def test_the_rmbg_shim_refuses_a_changed_upstream_file(self):
        changed = self.folder / "briarmbg.py"
        changed.write_text("import torch\n", encoding="utf-8")
        with self.assertRaises(self.ex.ExportError):
            self.ex.load_briarmbg(changed)

    def test_the_trimap_marks_rmbgs_unsure_band_grown_by_the_radius_as_unknown(self):
        # PB48: subject on the left, background on the right, one soft column between them.
        mask = np.zeros((9, 20), dtype=np.float32)
        mask[:, :9] = 1.0
        mask[:, 9] = 0.5
        trimap = self.ex.trimap_from_mask(mask, radius=2)
        np.testing.assert_array_equal(np.full(9, 0.5), trimap[:, 7])
        np.testing.assert_array_equal(np.full(9, 0.5), trimap[:, 11])
        self.assertEqual((1.0, 0.0), (trimap[4, 6], trimap[4, 12]))

    def test_a_matting_model_exports_with_its_trimap_channel_and_matches_torch(self):
        # PB48: a tiny randomly initialised ViTMatte through the same path the real one takes.
        try:
            from transformers import VitDetConfig, VitMatteConfig, VitMatteForImageMatting
        except ImportError:
            self.skipTest("transformers not installed (scripts/requirements.txt)")
        backbone = VitDetConfig(hidden_size=32, num_hidden_layers=2, num_attention_heads=2, mlp_ratio=2, image_size=64,
                                patch_size=16, num_channels=4, window_block_indices=[0], window_size=2,
                                residual_block_indices=[], use_relative_position_embeddings=True, out_features=["stage2"])
        config = VitMatteConfig(backbone_config=backbone, hidden_size=32, convstream_hidden_sizes=[8, 16, 32],
                                fusion_hidden_sizes=[32, 16, 8, 4])
        torch.manual_seed(0)
        model = self.ex.Alphas(VitMatteForImageMatting(config)).eval()
        spec = self.ex.Export("tiny-matte", "unused", lambda folder: model, multiple=32, input_range=(-1.0, 1.0),
                              task="matting", output_channels=1, channels=4, trace=256)
        fp32 = self.folder / "fp32.onnx"
        self.ex.export_fp32(model, spec, fp32)
        inputs = onnx.load(str(fp32)).graph.input[0].type.tensor_type.shape.dim
        self.assertEqual(4, inputs[1].dim_value)
        # verify raises past the tolerance; both sizes differ from the traced square.
        lines, _ = self.ex.verify(model, spec, fp32, None)
        self.assertEqual(["192x384", "384x128"], [line.split(" ")[0] for line in lines])

    def test_the_matting_model_is_exported_after_the_model_its_trimap_comes_from(self):
        names = [spec.name for spec in self.ex.EXPORTS]
        self.assertLess(names.index("rmbg-1.4"), names.index(self.ex.MATTER))


if __name__ == "__main__":
    unittest.main()
