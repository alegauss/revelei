"""Publish the optional FP32 model set to Hugging Face (PB12).

The installer carries the FP16 set (PB51); this hosts the FP32 one the app downloads for High
quality (PB13), in the owner's public repository alegauss/photo-boost-models. Two commits:

  1. the FP32 files and their meta.json under the flat names build-manifest.py stages, and a
     model card naming each model's license;
  2. manifest.json, built with resolve/<commit 1>/ URLs, so no later upload can change a file
     the app verifies by SHA-256.

RMBG-1.4 is not re-hosted: our fp32.onnx is BRIA's own export, so its manifest entry points at
briaai/RMBG-1.4 at the commit model-sources.json pins. Its meta.json is ours and is hosted.

    .venv\\Scripts\\python scripts\\publish-models.py --token-file D:\\Keys\\hf.txt
    .venv\\Scripts\\python scripts\\publish-models.py --dry-run

The token is read from the file and never printed. The result, the URL the app pins, goes to
scripts/published-models.json. Requirements: huggingface_hub (scripts/requirements.txt).
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import shutil
import sys
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
REPO = SCRIPTS.parent
MODELS = REPO / ".cache" / "models"
STAGE = REPO / ".cache" / "publish-fp32"
SOURCES = SCRIPTS / "model-sources.json"
RECORD = SCRIPTS / "published-models.json"

REPO_ID = "alegauss/photo-boost-models"
VERSION = "2"
VARIANT = "fp32"
# Hosted here, FP32. Version 2 adds ViTMatte (PB58): background removal fetches it on first
# use. YuNet stays FP32 in the installer, so nothing downloads it.
HOSTED = ["realesrgan-x4plus", "realesrgan-x2plus", "gfpgan-v1.4", "scunet-color-real-psnr", "vitmatte-small-composition-1k"]
# PB58: the copy the app embeds, so it knows every file's size and hash without the network.
EMBEDDED = REPO / "src" / "Revelei.Inference" / "models-manifest.json"
# Listed, with its file at the upstream's own pinned URL.
EXTERNAL = {"rmbg-1.4": "rmbg-1.4-onnx"}


def _manifest_module():
    spec = importlib.util.spec_from_file_location("build_manifest", SCRIPTS / "build-manifest.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


bm = _manifest_module()


def external_urls(models: Path, sources: Path = SOURCES) -> dict[tuple[str, str], str]:
    """Each external model's FP32 URL, from model-sources.json, checked to be the same bytes."""
    entries = {m["name"]: m for m in json.loads(sources.read_text(encoding="utf-8"))["models"]}
    urls = {}
    for model, source in EXTERNAL.items():
        upstream = entries[source]
        ours = json.loads((models / model / "meta.json").read_text(encoding="utf-8"))["variants"][VARIANT]
        if ours["sha256"] != upstream["sha256"]:
            raise bm.ManifestError(f"{model}: our {VARIANT} is not {source}'s file, so it cannot point there")
        urls[(model, VARIANT)] = upstream["url"]
    return urls


def model_card(manifest: dict) -> str:
    """The repository's README: what each file is, where it came from, and under which license."""
    lines = [
        "---",
        "license: other",
        "license_name: per-model",
        "tags: [onnx, image-restoration, super-resolution, revelei]",
        "---",
        "",
        "# Revelei models (FP32)",
        "",
        "The FP32 ONNX models [Revelei](https://github.com/Japode/revelei) downloads for its",
        "High quality mode. They are exported from each upstream's own weights and checked against",
        "PyTorch by `scripts/export-and-quantize.py`. Every file is listed in `manifest.json` with its",
        "size and SHA-256, and the app refuses one that differs. Each model keeps its upstream license:",
        "",
        "| Model | File | License |",
        "| --- | --- | --- |",
    ]
    for model in manifest["models"]:
        variant = model["variants"][VARIANT]
        where = "this repository" if variant["url"].startswith("https://huggingface.co/" + REPO_ID) else f"[upstream]({variant['url']})"
        lines.append(f"| {model['name']} | {where} | [{model['license']}]({model['license_url']}) |")
    lines += [
        "",
        "**Non-commercial terms.** GFPGAN v1.4 includes StyleGAN2-derived code under the NVIDIA Source",
        "Code License and DFDNet-derived code under CC BY-NC-SA 4.0, so treat commercial use as not",
        "cleared. RMBG-1.4 is BRIA's, licensed for non-commercial use; it is not hosted here, and the",
        "manifest points at BRIA's own repository at a pinned commit.",
        "",
    ]
    return "\n".join(lines)


def stage(models: Path, stage_dir: Path, base_url: str) -> dict:
    """The files to upload, with manifest.json and README.md, for the given base URL."""
    if stage_dir.exists():
        shutil.rmtree(stage_dir)
    manifest = bm.build(
        models,
        stage_dir,
        base_url,
        VERSION,
        only=[*HOSTED, *EXTERNAL],
        variants_wanted={VARIANT},
        external=external_urls(models),
    )
    (stage_dir / "README.md").write_text(model_card(manifest), encoding="utf-8", newline="\n")
    return manifest


def resolve_base(commit: str) -> str:
    return f"https://huggingface.co/{REPO_ID}/resolve/{commit}"


def upload(token: str, models: Path, stage_dir: Path) -> dict:
    """The two commits; returns what scripts/published-models.json records."""
    from huggingface_hub import HfApi

    api = HfApi(token=token)
    api.create_repo(REPO_ID, repo_type="model", private=False, exist_ok=True)

    # Commit 1: the files and the card. The manifest staged beside them names a placeholder base,
    # so it is left out here and rebuilt against this commit below.
    stage(models, stage_dir, resolve_base("pending"))
    (stage_dir / "manifest.json").unlink()
    files = api.upload_folder(repo_id=REPO_ID, folder_path=str(stage_dir), commit_message=f"Revelei FP32 models, manifest version {VERSION}")
    files_commit = files.oid

    # Commit 2: manifest.json with every URL pinned to commit 1.
    manifest = stage(models, stage_dir, resolve_base(files_commit))
    pinned = api.upload_file(
        path_or_fileobj=str(stage_dir / "manifest.json"),
        path_in_repo="manifest.json",
        repo_id=REPO_ID,
        commit_message=f"manifest.json pinned to {files_commit[:12]}",
    )
    manifest_commit = pinned.oid
    shutil.copyfile(stage_dir / "manifest.json", EMBEDDED)
    return {
        "repo": REPO_ID,
        "version": VERSION,
        "files_commit": files_commit,
        "manifest_commit": manifest_commit,
        "manifest_url": f"{resolve_base(manifest_commit)}/manifest.json",
        "models": [m["name"] for m in manifest["models"]],
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Publish the FP32 model set to Hugging Face.")
    parser.add_argument("--token-file", type=Path, help="a file holding a write token; never printed")
    parser.add_argument("--models", type=Path, default=MODELS, help="export output (default: .cache/models)")
    parser.add_argument("--stage", type=Path, default=STAGE, help="folder to stage into (default: .cache/publish-fp32)")
    parser.add_argument("--dry-run", action="store_true", help="stage and write the card and manifest, upload nothing")
    args = parser.parse_args(argv)
    try:
        if args.dry_run:
            manifest = stage(args.models, args.stage, resolve_base("DRY-RUN"))
            print(f"staged {len(manifest['models'])} models in {args.stage}; nothing uploaded")
            return 0
        if args.token_file is None:
            print("ERROR --token-file is required to upload", file=sys.stderr)
            return 2
        record = upload(args.token_file.read_text(encoding="utf-8").strip(), args.models, args.stage)
    except bm.ManifestError as error:
        print(f"ERROR {error}", file=sys.stderr)
        return 1
    RECORD.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"published {len(record['models'])} models; the app pins {record['manifest_url']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
