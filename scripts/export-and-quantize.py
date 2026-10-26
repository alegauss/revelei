"""Export the upstream weights to ONNX (PB8) and quantize them to INT8 (PB9).

Reads what download_models.py left in .cache/weights and writes, per model,
<out>/<name>/fp32.onnx, int8.onnx and, where the graph converts cleanly, fp16.onnx (default
out: .cache/models, git-ignored). Each output passes onnx.checker and is then run in ONNX Runtime
against the PyTorch model; an FP32 difference past the tolerance fails the script rather than
writing a model that is quietly wrong. An FP16 variant that loses too much is dropped, and said.

    Real-ESRGAN x4plus, x2plus  dynamic batch, height and width; input RGB in [0, 1]
    GFPGAN v1.4                 1 x 3 x 512 x 512 aligned face; RGB in [-1, 1], out the same
    RMBG-1.4                    BRIA's own ONNX export, adopted once it matches PyTorch;
                                batch x 3 x 1024 x 1024, pixels / 255 - 0.5; out the mask
    SCUNet real PSNR            the denoiser; dynamic batch, height and width, each a multiple
                                of 64; RGB in [0, 1], out the same
    ViTMatte small              the matting model (PB48); dynamic batch, height and width, each
                                a multiple of 32; RGB in [-1, 1] then a trimap channel (0
                                background, 0.5 unknown, 1 foreground); out alpha in [0, 1]
    YuNet 2023mar               OpenCV Zoo's face detector, adopted once it finds the test
                                photo's face; 1 x 3 x 640 x 640 BGR 0-255; fp32 only

    .venv\\Scripts\\python scripts\\export-and-quantize.py
    .venv\\Scripts\\python scripts\\export-and-quantize.py --only gfpgan-v1.4

Requirements: scripts/requirements.txt.
"""

from __future__ import annotations

import argparse
import collections
import json
import shutil
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

import numpy as np
import onnx
import onnxruntime as ort
import torch
from onnxconverter_common import float16
from PIL import Image
from onnxruntime.quantization import QuantType, quantize_dynamic
from torch import nn

sys.path.insert(0, str(Path(__file__).resolve().parent))

from archs.gfpganv1_clean_arch import GFPGANv1Clean  # noqa: E402
from archs.rrdbnet import RRDBNet  # noqa: E402
from archs.scunet import SCUNet  # noqa: E402

REPO = Path(__file__).resolve().parent.parent
WEIGHTS = REPO / ".cache" / "weights"
OUT = REPO / ".cache" / "models"
OPSET = 17

# Largest absolute difference from PyTorch, on outputs spanning a range of about 1 or 2.
FP32_TOLERANCE = 1e-3
# FP16 is judged by PSNR against the FP32 output instead: per-pixel error is expected.
FP16_MIN_PSNR = 40.0

# Unsigned weights, not the QInt8 the plan named: on Real-ESRGAN x4plus at 64x64, QUInt8 came
# within 37.1 dB of FP32 on CPU against QInt8's 32.3 dB, and ran in 0.8 s against 3.7 s.
INT8_WEIGHT_TYPE = QuantType.QUInt8
# What dynamic quantization rewrites; any float op of these kinds left behind is reported.
QUANTIZED_OPS = ("ConvInteger", "MatMulInteger")
FLOAT_COMPUTE_OPS = ("Conv", "MatMul", "Gemm", "ConvTranspose")


@dataclass(frozen=True)
class Export:
    name: str
    weights: str
    # Builds the module from the weights folder; the module returns the one output the app reads.
    build: Callable[[Path], nn.Module]
    # Height and width must be a multiple of this (x2plus pixel-unshuffles by 2).
    multiple: int = 1
    # A fixed square input, batch 1, instead of dynamic axes.
    size: int | None = None
    # The range of the input the model expects, and of what it returns.
    input_range: tuple[float, float] = (0.0, 1.0)
    output_range: tuple[float, float] = (0.0, 1.0)
    # An upstream ONNX file (in the weights folder) adopted as fp32.onnx instead of exporting.
    upstream_onnx: str | None = None
    # PB10: the fixed image (in <weights>/test-images) INT8 and FP16 are checked against FP32 on,
    # and the lowest INT8 PSNR against FP32 that may ship.
    test_image: str = ""
    int8_min_psnr: float = 0.0
    # PB11: what meta.json tells the app. source names the model-sources.json entry the
    # weights came from; tile is the recommended tile side for tiled models, None otherwise.
    task: str = "upscale"
    scale: int = 1
    output_channels: int = 3
    source: str = ""
    tile: int | None = None
    # PB48: 4 for a matting model, whose fourth input channel is the trimap.
    channels: int = 3
    # The square side to trace at, for a model whose traced graph is checked at larger sizes.
    trace: int | None = None


class ExportError(Exception):
    """An export did not produce a model that matches its PyTorch source."""


class FirstOutput(nn.Module):
    """Calls the wrapped network with fixed keyword arguments and keeps one output."""

    def __init__(self, net: nn.Module, pick: Callable[[object], torch.Tensor], **kwargs) -> None:
        super().__init__()
        self.net = net
        self.pick = pick
        self.kwargs = kwargs

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.pick(self.net(x, **self.kwargs))


def state_dict(path: Path) -> dict:
    if not path.is_file():
        raise ExportError(f"{path} is missing; run scripts/download_models.py first")
    state = torch.load(path, map_location="cpu", weights_only=True)
    return state.get("params_ema", state.get("params", state))


def build_rrdbnet(scale: int, weights: str) -> Callable[[Path], nn.Module]:
    def build(folder: Path) -> nn.Module:
        model = RRDBNet(scale=scale)
        model.load_state_dict(state_dict(folder / weights), strict=True)
        return model

    return build


def build_gfpgan(folder: Path) -> nn.Module:
    # GFPGANv1.4's published configuration (gfpgan/utils.py, arch "clean").
    net = GFPGANv1Clean(
        out_size=512,
        num_style_feat=512,
        channel_multiplier=2,
        decoder_load_path=None,
        fix_decoder=False,
        num_mlp=8,
        input_is_latent=True,
        different_w=True,
        narrow=1,
        sft_half=True,
    )
    net.load_state_dict(state_dict(folder / "GFPGANv1.4.pth"), strict=True)
    # The stored noise buffers instead of fresh noise per call, so the graph is deterministic.
    return FirstOutput(net, lambda out: out[0], return_rgb=False, randomize_noise=False)


def build_scunet(folder: Path) -> nn.Module:
    # The configuration SCUNet's own real-photo test script loads these weights with.
    net = SCUNet(in_nc=3, config=(4, 4, 4, 4, 4, 4, 4), dim=64)
    net.load_state_dict(state_dict(folder / "scunet_color_real_psnr.pth"), strict=True)
    return net


def load_briarmbg(path: Path) -> type[nn.Module]:
    """BRIA's briarmbg.py with its transformers base class and config replaced by plain ones."""
    if not path.is_file():
        raise ExportError(f"{path} is missing; run scripts/download_models.py first")
    source = path.read_text(encoding="utf-8")
    for line in ("from transformers import PreTrainedModel", "from .MyConfig import RMBGConfig"):
        if line not in source:
            raise ExportError(f"{path} no longer contains '{line}'; the shim needs updating")
        source = source.replace(line, "")

    class RMBGConfig:
        in_ch = 3
        out_ch = 1

    class PreTrainedModel(nn.Module):
        def __init__(self, config) -> None:
            super().__init__()
            self.config = config

    namespace = {"__name__": "briarmbg", "PreTrainedModel": PreTrainedModel, "RMBGConfig": RMBGConfig}
    exec(compile(source, str(path), "exec"), namespace)  # noqa: S102 - pinned by sha256 in model-sources.json
    return namespace["BriaRMBG"]


def build_rmbg(folder: Path) -> nn.Module:
    net = load_briarmbg(folder / "briarmbg.py")()
    net.load_state_dict(state_dict(folder / "RMBG-1.4.pth"), strict=True)
    return FirstOutput(net, lambda out: out[0][0])


MATTER = "vitmatte-small-composition-1k"


class Alphas(nn.Module):
    """A transformers matting model called as the app calls it: one tensor in, the alpha out."""

    def __init__(self, net: nn.Module) -> None:
        super().__init__()
        self.net = net

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.net(pixel_values=x).alphas


def build_vitmatte(folder: Path) -> nn.Module:
    # transformers carries the architecture, so nothing is vendored; only this export needs it.
    model_folder = folder / MATTER
    for needed in ("config.json", "model.safetensors"):
        if not (model_folder / needed).is_file():
            raise ExportError(f"{model_folder / needed} is missing; run scripts/download_models.py first")
    from transformers import VitMatteForImageMatting

    return Alphas(VitMatteForImageMatting.from_pretrained(model_folder, local_files_only=True))


# PB10: the lowest INT8 PSNR against FP32, on each model's test image, that may ship. Set about
# 2 dB under what the first export measured on 2026-10-19 (x4plus 33.1, x2plus 33.2, GFPGAN 31.2,
# RMBG 29.2 dB; SCUNet 46.9 dB and ViTMatte 43.8 dB on 2026-10-20), so a quantization that got
# worse fails while run-to-run noise does not.
INT8_FLOOR = {"realesrgan-x4plus": 31.0, "realesrgan-x2plus": 31.0, "gfpgan-v1.4": 29.0, "rmbg-1.4": 27.0, "scunet-color-real-psnr": 45.0, MATTER: 41.0}

# The recommended tile side for the upscalers: the middle of the 128 to 1024 range PB41 picks
# from by free video memory, and what the app starts from before it knows the GPU.
UPSCALE_TILE = 256

EXPORTS = [
    Export(
        "realesrgan-x4plus",
        "RealESRGAN_x4plus.pth",
        build_rrdbnet(4, "RealESRGAN_x4plus.pth"),
        test_image="0014.jpg",
        int8_min_psnr=INT8_FLOOR["realesrgan-x4plus"],
        scale=4,
        source="realesrgan-x4plus",
        tile=UPSCALE_TILE,
    ),
    Export(
        "realesrgan-x2plus",
        "RealESRGAN_x2plus.pth",
        build_rrdbnet(2, "RealESRGAN_x2plus.pth"),
        multiple=2,
        test_image="0014.jpg",
        int8_min_psnr=INT8_FLOOR["realesrgan-x2plus"],
        scale=2,
        source="realesrgan-x2plus",
        tile=UPSCALE_TILE,
    ),
    Export(
        "gfpgan-v1.4",
        "GFPGANv1.4.pth",
        build_gfpgan,
        size=512,
        input_range=(-1.0, 1.0),
        output_range=(-1.0, 1.0),
        test_image="Adele_crop.png",
        int8_min_psnr=INT8_FLOOR["gfpgan-v1.4"],
        task="face-restore",
        scale=1,
        source="gfpgan-v1.4",
    ),
    Export(
        "rmbg-1.4",
        "RMBG-1.4.pth",
        build_rmbg,
        size=1024,
        input_range=(-0.5, 0.5),
        upstream_onnx="RMBG-1.4.onnx",
        test_image="example_input.jpg",
        int8_min_psnr=INT8_FLOOR["rmbg-1.4"],
        task="background-removal",
        scale=1,
        output_channels=1,
        source="rmbg-1.4",
    ),
    Export(
        "scunet-color-real-psnr",
        "scunet_color_real_psnr.pth",
        build_scunet,
        # Three halvings of 8 px attention windows: whole windows at every level.
        multiple=64,
        test_image="building.png",
        int8_min_psnr=INT8_FLOOR["scunet-color-real-psnr"],
        task="denoise",
        scale=1,
        source="scunet-color-real-psnr",
        tile=UPSCALE_TILE,
    ),
    Export(
        MATTER,
        f"{MATTER}/model.safetensors",
        build_vitmatte,
        # What its image processor pads to; the backbone pads to its 14-token windows itself.
        multiple=32,
        input_range=(-1.0, 1.0),
        # Validated on RMBG's own trimap of its test photo, so it is exported after rmbg-1.4.
        test_image="example_input.jpg",
        int8_min_psnr=INT8_FLOOR[MATTER],
        task="matting",
        scale=1,
        output_channels=1,
        source=MATTER,
        channels=4,
        # The crop side it was trained on; a 64 px trace would check windows mostly made of padding.
        trace=512,
    ),
]


def trace_side(spec: Export) -> int:
    """The square a model is traced and measured at: 32 multiples, but 2 for a windowed transformer,
    where 32 x 64 px would be a 2048 px trace no CPU check needs."""
    if spec.size is not None:
        return spec.size
    if spec.trace is not None:
        return spec.trace
    return spec.multiple * (2 if spec.multiple >= 32 else 32)


def load(spec: Export, weights: Path) -> nn.Module:
    try:
        return spec.build(weights).eval()
    except ExportError as error:
        raise ExportError(f"{spec.name}: {error}") from error


def export_fp32(model: nn.Module, spec: Export, target: Path) -> None:
    side = trace_side(spec)
    sample = torch.rand(1, spec.channels, side, side)
    axes = None
    if spec.size is None:
        axes = {"input": {0: "batch", 2: "height", 3: "width"}, "output": {0: "batch", 2: "out_height", 3: "out_width"}}
    torch.onnx.export(
        model,
        (sample,),
        str(target),
        input_names=["input"],
        output_names=["output"],
        opset_version=OPSET,
        dynamo=False,
        dynamic_axes=axes,
    )
    onnx.checker.check_model(str(target), full_check=True)


def adopt_fp32(spec: Export, weights: Path, target: Path) -> None:
    source = weights / spec.upstream_onnx
    if not source.is_file():
        raise ExportError(f"{spec.name}: {source} is missing; run scripts/download_models.py first")
    shutil.copyfile(source, target)
    onnx.checker.check_model(str(target), full_check=True)


def export_fp16(fp32: Path, target: Path) -> None:
    converted = float16.convert_float_to_float16(onnx.load(str(fp32)), keep_io_types=True)
    onnx.save(converted, str(target))
    onnx.checker.check_model(str(target), full_check=True)


def quantize_int8(fp32: Path, target: Path) -> str:
    """Dynamic INT8 (weights quantized ahead, activations at run time). Returns the report line.

    Weights reached through another op stay float in the file: GFPGAN's StyleGAN2 decoder
    multiplies each conv weight by the style first, so that conv quantizes its weight at run
    time and the file only shrinks from 340 to 158 MB.
    """
    quantize_dynamic(str(fp32), str(target), weight_type=INT8_WEIGHT_TYPE)
    onnx.checker.check_model(str(target), full_check=True)
    # Loading it is the check that every quantized op has a CPU kernel; quality is PB10's gate.
    ort.InferenceSession(str(target), providers=["CPUExecutionProvider"])
    ops = collections.Counter(node.op_type for node in onnx.load(str(target)).graph.node)
    quantized = sum(ops[op] for op in QUANTIZED_OPS)
    left = {op: ops[op] for op in FLOAT_COMPUTE_OPS if ops[op]}
    megabytes = target.stat().st_size / 1e6
    float_note = ", ".join(f"{count} {op}" for op, count in left.items()) or "none"
    return (
        f"int8 {megabytes:.1f} MB (fp32 {fp32.stat().st_size / 1e6:.1f} MB), "
        f"{quantized} ops quantized, float compute ops left: {float_note}"
    )


def prepare(spec: Export, image: Path) -> np.ndarray:
    """The test image as the model's input: RGB, resized to a fixed size or cropped to the multiple."""
    if not image.is_file():
        raise ExportError(f"{spec.name}: {image} is missing; run scripts/download_models.py first")
    picture = Image.open(image).convert("RGB")
    if spec.size is not None:
        picture = picture.resize((spec.size, spec.size), Image.Resampling.BILINEAR)
    else:
        w, h = picture.size
        picture = picture.crop((0, 0, w - w % spec.multiple, h - h % spec.multiple))
    pixels = np.asarray(picture, dtype=np.float32).transpose(2, 0, 1)[None] / 255.0
    low, high = spec.input_range
    return pixels * (high - low) + low


# PB48: the trimap a matting model is validated on, from RMBG's mask of the same photo. Unknown is
# where the stretched mask is unsure, between the floor and 1 minus it, grown by the radius so the
# matting model sees both sides of the edge; the rest is RMBG's mask thresholded at a half.
# The floor is Revelei.Inference's Matting.TrimapFloor: 0.1 beat 0.02 at every tile size there.
TRIMAP_FLOOR = 0.1
TRIMAP_RADIUS = 8


def _grow(mask: np.ndarray, radius: int) -> np.ndarray:
    """A boolean mask dilated by a (2 radius + 1) square, one axis at a time."""
    k = 2 * radius + 1
    for axis in (0, 1):
        padded = np.pad(mask, [(radius, radius) if a == axis else (0, 0) for a in (0, 1)])
        mask = np.lib.stride_tricks.sliding_window_view(padded, k, axis=axis).any(axis=-1)
    return mask


def trimap_from_mask(mask: np.ndarray, floor: float = TRIMAP_FLOOR, radius: int = TRIMAP_RADIUS) -> np.ndarray:
    """0 background, 0.5 unknown and 1 foreground, from a mask in [0, 1]."""
    trimap = np.where(mask >= 0.5, 1.0, 0.0).astype(np.float32)
    trimap[_grow((mask > floor) & (mask < 1 - floor), radius)] = 0.5
    return trimap


def with_trimap(spec: Export, x: np.ndarray, image: Path, rmbg: Path) -> np.ndarray:
    """The prepared photo with its trimap as the fourth channel, made from rmbg-1.4's fp32.onnx."""
    if not rmbg.is_file():
        raise ExportError(f"{spec.name}: {rmbg} is missing; export rmbg-1.4 first")
    picture = Image.open(image).convert("RGB")
    side = next(e.size for e in EXPORTS if e.name == "rmbg-1.4")
    small = np.asarray(picture.resize((side, side), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0 - 0.5
    mask = run(rmbg, small.transpose(2, 0, 1)[None].copy())[0, 0]
    mask = np.asarray(Image.fromarray(mask).resize(picture.size, Image.Resampling.BILINEAR))[: x.shape[2], : x.shape[3]]
    mask = (mask - mask.min()) / max(float(mask.max() - mask.min()), 1e-6)
    return np.concatenate([x, trimap_from_mask(mask)[None, None]], axis=1).astype(np.float32)


def _box(x: np.ndarray, k: int) -> np.ndarray:
    """Mean over every k x k window, by summed-area table ('valid' windows only)."""
    s = np.pad(x, ((1, 0), (1, 0))).cumsum(0).cumsum(1)
    return (s[k:, k:] - s[:-k, k:] - s[k:, :-k] + s[:-k, :-k]) / (k * k)


def ssim(a: np.ndarray, b: np.ndarray, value_range: tuple[float, float] = (0.0, 1.0), k: int = 7) -> float:
    """SSIM on the channel mean with a uniform k x k window: cheap, and enough to rank variants."""
    low, high = value_range
    x = np.clip(a, low, high).astype(np.float64).mean(axis=(0, 1))
    y = np.clip(b, low, high).astype(np.float64).mean(axis=(0, 1))
    c1, c2 = (0.01 * (high - low)) ** 2, (0.03 * (high - low)) ** 2
    mx, my = _box(x, k), _box(y, k)
    vx, vy = _box(x * x, k) - mx * mx, _box(y * y, k) - my * my
    cov = _box(x * y, k) - mx * my
    score = ((2 * mx * my + c1) * (2 * cov + c2)) / ((mx * mx + my * my + c1) * (vx + vy + c2))
    return float(score.mean())


def validate(spec: Export, folder: Path, images: Path) -> list[str]:
    """PB10: every variant beside fp32.onnx against it on the test image; INT8 below its floor fails.

    The numbers go to <folder>/meta.json under "validation", for the README table.
    """
    x = prepare(spec, images / spec.test_image)
    if spec.channels == 4:
        x = with_trimap(spec, x, images / spec.test_image, folder.parent / "rmbg-1.4" / "fp32.onnx")
    reference = run(folder / "fp32.onnx", x)
    results: dict[str, dict] = {}
    lines = []
    for variant in ("int8", "fp16"):
        path = folder / f"{variant}.onnx"
        if not path.is_file():
            continue
        out = run(path, x)
        quality = psnr(out, reference, spec.output_range)
        structure = ssim(out, reference, spec.output_range)
        results[variant] = {"psnr_db": round(quality, 2), "ssim": round(structure, 4)}
        lines.append(f"{variant} vs fp32 on {spec.test_image}: PSNR {quality:.1f} dB, SSIM {structure:.4f}")
        if variant == "int8":
            results[variant]["min_psnr_db"] = spec.int8_min_psnr
            if quality < spec.int8_min_psnr:
                raise ExportError(f"{spec.name}: int8 PSNR {quality:.1f} dB is below its floor of {spec.int8_min_psnr} dB")
    meta_path = folder / "meta.json"
    meta = json.loads(meta_path.read_text(encoding="utf-8")) if meta_path.is_file() else {}
    meta["validation"] = {"image": spec.test_image, "input": list(x.shape), "against": "fp32", **results}
    meta_path.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    return lines


META_SCHEMA_VERSION = 1
VARIANTS = ("fp32", "fp16", "int8")
SOURCES = Path(__file__).resolve().parent / "model-sources.json"


def largest_activation_bytes_per_pixel(model: nn.Module, spec: Export) -> float:
    """The biggest tensor any module produces, in FP32 bytes per input pixel, read by forward hooks.

    Not the peak memory (several activations are alive at once): PB41 multiplies it by a margin
    against the free video memory to pick a tile.
    """
    side = trace_side(spec)
    largest = 0

    def hook(_module, _inputs, output) -> None:
        nonlocal largest
        for tensor in output if isinstance(output, (list, tuple)) else (output,):
            if isinstance(tensor, torch.Tensor):
                largest = max(largest, tensor.numel())
            elif isinstance(tensor, (list, tuple)):
                largest = max([largest, *(t.numel() for t in tensor if isinstance(t, torch.Tensor))])

    handles = [m.register_forward_hook(hook) for m in model.modules()]
    try:
        with torch.no_grad():
            model(torch.zeros(1, spec.channels, side, side))
    finally:
        for handle in handles:
            handle.remove()
    return largest * 4 / (side * side)


def sha256_of(path: Path) -> str:
    import hashlib

    digest = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


def describe(spec: Export, model: nn.Module, folder: Path) -> str:
    """PB11: everything the app reads from meta.json, written beside the validation results."""
    sources = json.loads(SOURCES.read_text(encoding="utf-8"))
    upstream = next(m for m in sources["models"] if m["name"] == spec.source)
    variants = {}
    for variant in VARIANTS:
        path = folder / f"{variant}.onnx"
        if path.is_file():
            variants[variant] = {"file": path.name, "bytes": path.stat().st_size, "sha256": sha256_of(path)}
    graph = onnx.load(str(folder / "fp32.onnx"), load_external_data=False).graph
    batch_dim = graph.input[0].type.tensor_type.shape.dim[0]
    meta_path = folder / "meta.json"
    old = json.loads(meta_path.read_text(encoding="utf-8")) if meta_path.is_file() else {}
    meta = {
        "schema": META_SCHEMA_VERSION,
        "name": spec.name,
        "task": spec.task,
        "scale": spec.scale,
        "input": {
            "name": graph.input[0].name,
            "layout": "NCHW",
            "colour_order": "RGB",
            "channels": spec.channels,
            "value_range": list(spec.input_range),
            "size": spec.size,
            "multiple": spec.multiple,
            "batch": "dynamic" if batch_dim.dim_param else batch_dim.dim_value,
        },
        "output": {"name": graph.output[0].name, "channels": spec.output_channels, "value_range": list(spec.output_range)},
        "tile": {
            "recommended": spec.tile,
            "largest_activation_bytes_per_pixel": round(largest_activation_bytes_per_pixel(model, spec), 2),
        },
        "variants": variants,
        "upstream": {"url": upstream["url"], "license": upstream["license"], "license_url": upstream["license_url"]},
    }
    if "validation" in old:
        meta["validation"] = old["validation"]
    meta_path.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    return f"meta.json: {', '.join(variants)}; largest activation {meta['tile']['largest_activation_bytes_per_pixel']} B/px"


def run(path: Path, x: np.ndarray) -> np.ndarray:
    session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    name = session.get_inputs()[0].name
    return session.run(None, {name: x})[0]


def psnr(a: np.ndarray, b: np.ndarray, value_range: tuple[float, float] = (0.0, 1.0)) -> float:
    low, high = value_range
    mse = float(np.mean((np.clip(a, low, high) - np.clip(b, low, high)) ** 2))
    return float("inf") if mse == 0 else 10 * np.log10((high - low) ** 2 / mse)


def sizes(spec: Export) -> list[tuple[int, int]]:
    """Two non-square sizes, neither the traced one, prove dynamic axes; a fixed model gets two inputs."""
    if spec.size is not None:
        return [(spec.size, spec.size)] * 2
    if spec.trace is not None:
        # Either side of the traced square, and wider than it, as a photo's band would be.
        return [(spec.trace - 2 * spec.multiple, spec.trace + 4 * spec.multiple), (spec.trace + 4 * spec.multiple, spec.trace - 4 * spec.multiple)]
    if spec.multiple >= 32:
        return [(2 * spec.multiple, 3 * spec.multiple), (3 * spec.multiple, 2 * spec.multiple)]
    return [(24 * spec.multiple, 40 * spec.multiple), (48 * spec.multiple, 36 * spec.multiple)]


def verify(model: nn.Module, spec: Export, fp32: Path, fp16: Path | None) -> tuple[list[str], bool]:
    """Returns the report lines and whether the FP16 file, if any, is good enough to keep."""
    lines = []
    fp16_ok = fp16 is not None
    generator = torch.Generator().manual_seed(7)
    low, high = spec.input_range
    for h, w in sizes(spec):
        x = torch.rand(1, spec.channels, h, w, generator=generator) * (high - low) + low
        if spec.channels == 4:
            # A trimap's three levels, not noise in the input range.
            x[:, 3] = torch.randint(0, 3, (h, w), generator=generator).float() / 2
        with torch.no_grad():
            expected = model(x).numpy()
        got = run(fp32, x.numpy())
        if got.shape != expected.shape:
            raise ExportError(f"{spec.name}: fp32 shape {got.shape} != torch {expected.shape} at {h}x{w}")
        diff = float(np.max(np.abs(got - expected)))
        if diff > FP32_TOLERANCE:
            raise ExportError(f"{spec.name}: fp32 differs from torch by {diff:.2e} at {h}x{w}")
        line = f"{h}x{w} -> {got.shape[2]}x{got.shape[3]}: fp32 max diff {diff:.2e}"
        if fp16 is not None:
            quality = psnr(run(fp16, x.numpy()), got, spec.output_range)
            line += f", fp16 PSNR {quality:.1f} dB"
            fp16_ok = fp16_ok and quality >= FP16_MIN_PSNR
        lines.append(line)
    return lines, fp16_ok


def export(spec: Export, weights: Path, out: Path, fp16: bool = True, int8: bool = True) -> list[str]:
    model = load(spec, weights)
    folder = out / spec.name
    folder.mkdir(parents=True, exist_ok=True)
    fp32_path = folder / "fp32.onnx"
    fp16_path = folder / "fp16.onnx"
    if spec.upstream_onnx:
        adopt_fp32(spec, weights, fp32_path)
    else:
        export_fp32(model, spec, fp32_path)
    unconverted = None
    if fp16:
        try:
            export_fp16(fp32_path, fp16_path)
        except (onnx.checker.ValidationError, onnx.shape_inference.InferenceError) as error:
            # PB48: ViTMatte's shape arithmetic casts to float, which the converter leaves feeding
            # FP16 ops; such a graph does not type-check, so only fp32 and int8 are written.
            fp16_path.unlink(missing_ok=True)
            fp16 = False
            unconverted = f"fp16 dropped: the converted graph does not type-check ({str(error).splitlines()[0][:120]})"
    lines, fp16_ok = verify(model, spec, fp32_path, fp16_path if fp16 else None)
    if unconverted:
        lines.append(unconverted)
    if fp16 and not fp16_ok:
        fp16_path.unlink()
        lines.append(f"fp16 dropped: below {FP16_MIN_PSNR} dB, so only fp32 is written")
    if int8:
        lines.append(quantize_int8(fp32_path, folder / "int8.onnx"))
    lines += validate(spec, folder, weights / "test-images")
    return [*lines, describe(spec, model, folder)]


# PB30: the face detector, adopted as OpenCV Zoo ships it. Fixed 640 x 640, BGR pixels 0 to 255,
# and so small (0.2 MB, 13 ms on a CPU) that FP16 and INT8 would buy nothing: only fp32 is written.
DETECTOR = "yunet-2023mar"
DETECTOR_FILE = "face_detection_yunet_2023mar.onnx"
DETECTOR_SIZE = 640
DETECTOR_STRIDES = (8, 16, 32)
DETECTOR_IMAGE = "Blake_Lively.jpg"


def letterbox(image: Path, side: int) -> tuple[np.ndarray, float]:
    """The image scaled to fit side x side, top-left, black beyond it, as YuNet's BGR 0-255 NCHW input."""
    picture = Image.open(image).convert("RGB")
    scale = side / max(picture.size)
    w, h = max(1, round(picture.width * scale)), max(1, round(picture.height * scale))
    canvas = np.zeros((side, side, 3), dtype=np.float32)
    canvas[:h, :w] = np.asarray(picture.resize((w, h), Image.Resampling.BILINEAR), dtype=np.float32)
    return canvas[:, :, ::-1].transpose(2, 0, 1)[None].copy(), scale


def yunet_faces(outputs: dict[str, np.ndarray], threshold: float = 0.9) -> list[tuple[float, list[float]]]:
    """YuNet's heads decoded as OpenCV's FaceDetectorYN does, before NMS.

    Each face is (score, [x, y, w, h, then five landmarks as x, y]) in input pixels; the landmarks
    are the eye on the image's left, the other eye, the nose tip and the two mouth corners, left first.
    """
    faces = []
    for stride in DETECTOR_STRIDES:
        cols = DETECTOR_SIZE // stride
        cls, obj = outputs[f"cls_{stride}"][0, :, 0], outputs[f"obj_{stride}"][0, :, 0]
        bbox, kps = outputs[f"bbox_{stride}"][0], outputs[f"kps_{stride}"][0]
        score = np.sqrt(np.clip(cls, 0, 1) * np.clip(obj, 0, 1))
        for i in np.flatnonzero(score >= threshold):
            row, col = divmod(int(i), cols)
            w, h = np.exp(bbox[i, 2]) * stride, np.exp(bbox[i, 3]) * stride
            box = [(col + bbox[i, 0]) * stride - w / 2, (row + bbox[i, 1]) * stride - h / 2, w, h]
            marks = [(kps[i, k] + (col if k % 2 == 0 else row)) * stride for k in range(10)]
            faces.append((float(score[i]), [float(v) for v in box + marks]))
    return faces


def adopt_detector(weights: Path, out: Path) -> list[str]:
    """Copies YuNet into <out>/yunet-2023mar, proves it finds the test photo's face, and describes it."""
    source = weights / DETECTOR_FILE
    image = weights / "test-images" / DETECTOR_IMAGE
    for needed in (source, image):
        if not needed.is_file():
            raise ExportError(f"{DETECTOR}: {needed} is missing; run scripts/download_models.py first")
    folder = out / DETECTOR
    folder.mkdir(parents=True, exist_ok=True)
    fp32 = folder / "fp32.onnx"
    shutil.copyfile(source, fp32)
    onnx.checker.check_model(str(fp32), full_check=True)

    x, _ = letterbox(image, DETECTOR_SIZE)
    session = ort.InferenceSession(str(fp32), providers=["CPUExecutionProvider"])
    names = [o.name for o in session.get_outputs()]
    faces = yunet_faces(dict(zip(names, session.run(None, {session.get_inputs()[0].name: x}))))
    if not faces:
        raise ExportError(f"{DETECTOR}: found no face in {DETECTOR_IMAGE}")
    score, best = max(faces)
    if not best[4] < best[6]:
        raise ExportError(f"{DETECTOR}: the first landmark is not the eye on the image's left")

    # The largest tensor the graph makes, per input pixel, from ONNX's own shape inference.
    graph = onnx.shape_inference.infer_shapes(onnx.load(str(fp32))).graph
    largest = max(
        int(np.prod([d.dim_value for d in v.type.tensor_type.shape.dim]))
        for v in [*graph.value_info, *graph.output]
        if all(d.dim_value > 0 for d in v.type.tensor_type.shape.dim)
    )
    sources = json.loads(SOURCES.read_text(encoding="utf-8"))
    upstream = next(m for m in sources["models"] if m["name"] == DETECTOR)
    meta = {
        "schema": META_SCHEMA_VERSION,
        "name": DETECTOR,
        "task": "face-detection",
        "scale": 1,
        "input": {"name": session.get_inputs()[0].name, "layout": "NCHW", "colour_order": "BGR", "channels": 3,
                  "value_range": [0.0, 255.0], "size": DETECTOR_SIZE, "multiple": 1, "batch": 1},
        # A detector's heads: <part>_<stride> for each part, 15 values per anchor in all.
        "output": {"name": "yunet", "channels": 15, "value_range": [0.0, 1.0], "decoder": "yunet",
                   "strides": list(DETECTOR_STRIDES)},
        "tile": {"recommended": None, "largest_activation_bytes_per_pixel": round(largest * 4 / DETECTOR_SIZE**2, 2)},
        "variants": {"fp32": {"file": fp32.name, "bytes": fp32.stat().st_size, "sha256": sha256_of(fp32)}},
        "upstream": {"url": upstream["url"], "license": upstream["license"], "license_url": upstream["license_url"]},
    }
    (folder / "meta.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    return [f"{len(faces)} anchor(s) over 0.9 on {DETECTOR_IMAGE}, best {score:.3f}",
            f"meta.json: fp32; largest activation {meta['tile']['largest_activation_bytes_per_pixel']} B/px"]


def tiny_upscaler(scale: int) -> onnx.ModelProto:
    """A nearest-neighbour Resize with Real-ESRGAN's interface: same names, dynamic axes, [0, 1] RGB.

    Deterministic to the bit on every provider, a few hundred bytes, and needing no download, so a
    UI case can run a whole upscale offline and compare the exported file byte for byte.
    """
    from onnx import TensorProto, helper

    scales = helper.make_tensor("scales", TensorProto.FLOAT, [4], [1.0, 1.0, float(scale), float(scale)])
    node = helper.make_node("Resize", ["input", "", "scales"], ["output"], mode="nearest", nearest_mode="floor",
                            coordinate_transformation_mode="asymmetric")
    graph = helper.make_graph(
        [node],
        f"tiny-nearest-x{scale}",
        [helper.make_tensor_value_info("input", TensorProto.FLOAT, ["batch", 3, "height", "width"])],
        [helper.make_tensor_value_info("output", TensorProto.FLOAT, ["batch", 3, "out_height", "out_width"])],
        [scales],
    )
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", OPSET)], producer_name="photoboost-fixture")
    model.ir_version = 8
    onnx.checker.check_model(model, full_check=True)
    return model


def write_fixtures(out: Path) -> list[str]:
    """PB42: the stand-ins for realesrgan-x4plus and x2plus, under their real names, both variants."""
    written = []
    for spec, scale in ((EXPORTS[0], 4), (EXPORTS[1], 2)):
        folder = out / spec.name
        folder.mkdir(parents=True, exist_ok=True)
        raw = tiny_upscaler(scale).SerializeToString()
        variants = {}
        # PB51: the app's default is the FP16 set the installer carries, and High quality is FP32.
        for variant in ("fp16", "fp32"):
            path = folder / f"{variant}.onnx"
            path.write_bytes(raw)
            variants[variant] = {"file": path.name, "bytes": len(raw), "sha256": sha256_of(path)}
        meta = {
            "schema": META_SCHEMA_VERSION,
            "name": spec.name,
            "task": "upscale",
            "scale": scale,
            "input": {"name": "input", "layout": "NCHW", "colour_order": "RGB", "channels": 3, "value_range": [0.0, 1.0],
                      "size": None, "multiple": 1, "batch": "dynamic"},
            "output": {"name": "output", "channels": 3, "value_range": [0.0, 1.0]},
            # The output is the largest tensor: 3 channels at scale x scale per input pixel, FP32.
            "tile": {"recommended": UPSCALE_TILE, "largest_activation_bytes_per_pixel": 3.0 * scale * scale * 4},
            "variants": variants,
            "upstream": {"url": "https://github.com/xinntao/Real-ESRGAN", "license": "test fixture, no weights",
                         "license_url": "https://github.com/xinntao/Real-ESRGAN/blob/master/LICENSE"},
        }
        (folder / "meta.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8", newline="\n")
        written.append(spec.name)
    return written


DOWNLOAD_PORT = 47623
DOWNLOAD_SIDE = 32
DOWNLOAD_PADDING = 196_608


def tiny_matter() -> onnx.ModelProto:
    """PB59: RMBG-1.4's interface on a 32 px square, its mask the mean of the three channels.

    A 1x1 convolution with weights of a third, so it runs on every provider, plus an initializer
    nothing reads that brings the file to about 770 KB: a download the fixture server's 160 KB/s
    stretches to a few seconds, long enough for a case to watch it and pause it.
    """
    from onnx import TensorProto, helper

    weight = helper.make_tensor("weight", TensorProto.FLOAT, [1, 3, 1, 1], [1.0 / 3] * 3)
    padding = helper.make_tensor("padding", TensorProto.FLOAT, [DOWNLOAD_PADDING],
                                 (np.arange(DOWNLOAD_PADDING, dtype=np.float32) % 251).tolist())
    node = helper.make_node("Conv", ["input", "weight"], ["output"])
    side = DOWNLOAD_SIDE
    graph = helper.make_graph(
        [node],
        "tiny-mean-matter",
        [helper.make_tensor_value_info("input", TensorProto.FLOAT, ["batch", 3, side, side])],
        [helper.make_tensor_value_info("output", TensorProto.FLOAT, ["batch", 1, side, side])],
        [weight, padding],
    )
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", OPSET)], producer_name="photoboost-fixture")
    model.ir_version = 8
    onnx.checker.check_model(model, full_check=True)
    return model


def write_download_fixtures(out: Path) -> list[str]:
    """PB59: what the UI cases' fixture server serves for a first-use download, flat, as hosted.

    The stand-in RMBG-1.4 and its meta.json, and two manifests in build-manifest.py's schema: one
    whose files the server sends, and one whose files it answers 404 for, so a case sees a failure.
    """
    out.mkdir(parents=True, exist_ok=True)
    name = "rmbg-1.4"
    onnx_path = out / f"{name}-fp32.onnx"
    onnx_path.write_bytes(tiny_matter().SerializeToString())
    side = DOWNLOAD_SIDE
    meta = {
        "schema": META_SCHEMA_VERSION,
        "name": name,
        "task": "background-removal",
        "scale": 1,
        "input": {"name": "input", "layout": "NCHW", "colour_order": "RGB", "channels": 3, "value_range": [-0.5, 0.5],
                  "size": side, "multiple": 1, "batch": "dynamic"},
        "output": {"name": "output", "channels": 1, "value_range": [0.0, 1.0]},
        "tile": {"recommended": None, "largest_activation_bytes_per_pixel": 16.0},
        "variants": {"fp32": {"file": "fp32.onnx", "bytes": onnx_path.stat().st_size, "sha256": sha256_of(onnx_path)}},
        "upstream": {"url": "https://huggingface.co/briaai/RMBG-1.4", "license": "test fixture, no weights",
                     "license_url": "https://bria.ai/bria-huggingface-model-license-agreement/"},
    }
    meta_path = out / f"{name}-meta.json"
    meta_path.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8", newline="\n")

    written = [onnx_path.name, meta_path.name]
    for manifest_name, folder in (("manifest.json", "files"), ("manifest-missing.json", "missing")):
        base = f"http://127.0.0.1:{DOWNLOAD_PORT}/{folder}"
        manifest = {
            "schema": 1,
            "version": "fixture",
            "base_url": base,
            "models": [{
                "name": name,
                "task": "background-removal",
                "meta": {"url": f"{base}/{meta_path.name}", "bytes": meta_path.stat().st_size, "sha256": sha256_of(meta_path)},
                "variants": {"fp32": {"url": f"{base}/{onnx_path.name}", "bytes": onnx_path.stat().st_size,
                                      "sha256": sha256_of(onnx_path)}},
                "license": "test fixture, no weights",
                "license_url": "https://bria.ai/bria-huggingface-model-license-agreement/",
            }],
        }
        (out / manifest_name).write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
        written.append(manifest_name)
    return written


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Export the upstream weights to ONNX.")
    parser.add_argument("--weights", type=Path, default=WEIGHTS, help="where download_models.py put the files")
    parser.add_argument("--out", type=Path, default=OUT, help="output folder (default: .cache/models)")
    parser.add_argument("--only", action="append", default=[], metavar="NAME", help="export just this model; repeatable")
    parser.add_argument("--no-fp16", action="store_true", help="skip the FP16 variant")
    parser.add_argument("--no-int8", action="store_true", help="skip the INT8 variant")
    parser.add_argument("--validate-only", action="store_true", help="only re-check the variants already in --out")
    parser.add_argument("--fixtures", type=Path, metavar="DIR", help="write the tiny stand-in models UI cases run (PB42) and stop")
    parser.add_argument("--download-fixtures", type=Path, metavar="DIR",
                        help="write what the UI cases' fixture server serves for a first-use download (PB59) and stop")
    args = parser.parse_args(argv)

    if args.fixtures is not None:
        for name in write_fixtures(args.fixtures):
            print(f"fixture {name} -> {args.fixtures / name}")
        return 0

    if args.download_fixtures is not None:
        for name in write_download_fixtures(args.download_fixtures):
            print(f"download fixture {name} -> {args.download_fixtures / name}")
        return 0

    known = {e.name for e in EXPORTS} | {DETECTOR}
    unknown = [n for n in args.only if n not in known]
    if unknown:
        print(f"ERROR unknown model(s): {', '.join(unknown)}; known: {', '.join(sorted(known))}", file=sys.stderr)
        return 2

    failed = []
    for spec in EXPORTS:
        if args.only and spec.name not in args.only:
            continue
        try:
            if args.validate_only:
                folder = args.out / spec.name
                lines = validate(spec, folder, args.weights / "test-images")
                lines.append(describe(spec, load(spec, args.weights), folder))
            else:
                lines = export(spec, args.weights, args.out, fp16=not args.no_fp16, int8=not args.no_int8)
            for line in lines:
                print(f"{spec.name}: {line}")
            print(f"{spec.name}: {'validated' if args.validate_only else 'exported to'} {args.out / spec.name}")
        except ExportError as error:
            print(f"ERROR {error}", file=sys.stderr)
            failed.append(spec.name)
    if (not args.only or DETECTOR in args.only) and not report_detector(args.weights, args.out):
        failed.append(DETECTOR)
    return 1 if failed else 0


def report_detector(weights: Path, out: Path) -> bool:
    """Adopts the detector and prints what happened; False when it failed."""
    try:
        for line in adopt_detector(weights, out):
            print(f"{DETECTOR}: {line}")
    except ExportError as error:
        print(f"ERROR {error}", file=sys.stderr)
        return False
    print(f"{DETECTOR}: adopted into {out / DETECTOR}")
    return True


if __name__ == "__main__":
    sys.exit(main())
