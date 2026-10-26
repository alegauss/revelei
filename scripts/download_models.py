"""Download the upstream model weights listed in scripts/model-sources.json (PB7).

Each file lands in the cache folder (default .cache/weights at the repository root, which git
ignores: no weights live in the repository). A partial download is resumed from its .part file,
progress is printed, and the finished file is checked against the size and SHA-256 recorded in
model-sources.json before it is renamed into place. A file already present and valid is skipped,
so running this twice downloads nothing the second time.

    python scripts/download_models.py                 # every non-optional model
    python scripts/download_models.py --only gfpgan-v1.4 --include u2net
    python scripts/download_models.py --dest D:\\weights

Standard library only: the export step has its own requirements, this one has none.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SOURCES = REPO / "scripts" / "model-sources.json"
DEFAULT_DEST = REPO / ".cache" / "weights"
CHUNK = 1 << 20


@dataclass(frozen=True)
class Source:
    name: str
    file: str
    url: str
    size: int
    sha256: str
    optional: bool


class DownloadError(Exception):
    """A file could not be fetched, or arrived different from what model-sources.json records."""


def load_sources(path: Path = SOURCES) -> list[Source]:
    """The weights, then the test images export-and-quantize.py validates on (in test-images/)."""
    data = json.loads(path.read_text(encoding="utf-8"))
    entries = [(m, "") for m in data["models"]] + [(m, "test-images/") for m in data.get("test_images", [])]
    return [
        Source(m["name"], folder + m["file"], m["url"], int(m["size"]), m["sha256"].lower(), bool(m.get("optional", False)))
        for m, folder in entries
    ]


def select(sources: list[Source], only: list[str], include: list[str]) -> list[Source]:
    known = {s.name for s in sources}
    unknown = [n for n in [*only, *include] if n not in known]
    if unknown:
        raise DownloadError(f"unknown model(s): {', '.join(unknown)}; known: {', '.join(sorted(known))}")
    if only:
        return [s for s in sources if s.name in only]
    return [s for s in sources if not s.optional or s.name in include]


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(CHUNK):
            digest.update(chunk)
    return digest.hexdigest()


def is_valid(path: Path, source: Source) -> bool:
    return path.is_file() and path.stat().st_size == source.size and sha256_of(path) == source.sha256


def fetch(source: Source, dest: Path, out=sys.stdout) -> str:
    """Make dest/<file> a valid copy of source. Returns 'present' or 'downloaded'."""
    final = dest / source.file
    final.parent.mkdir(parents=True, exist_ok=True)
    if is_valid(final, source):
        print(f"{source.name}: present and valid, skipped", file=out)
        return "present"

    part = dest / (source.file + ".part")
    have = part.stat().st_size if part.exists() else 0
    if have > source.size:
        part.unlink()
        have = 0

    if have < source.size:
        request = urllib.request.Request(source.url, headers={"User-Agent": "revelei-download/1"})
        if have:
            request.add_header("Range", f"bytes={have}-")
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                # A server that ignores Range answers 200 with the whole file: start over.
                if have and response.status != 206:
                    have = 0
                mode = "ab" if have else "wb"
                if have:
                    print(f"{source.name}: resuming at {have:,} of {source.size:,} bytes", file=out)
                with part.open(mode) as f:
                    _copy(response, f, have, source, out)
        except urllib.error.URLError as error:
            raise DownloadError(f"{source.name}: {source.url}: {error}") from error

    size = part.stat().st_size
    if size != source.size:
        raise DownloadError(f"{source.name}: got {size:,} bytes, expected {source.size:,}; rerun to resume")
    actual = sha256_of(part)
    if actual != source.sha256:
        part.unlink()
        raise DownloadError(f"{source.name}: sha256 {actual} does not match {source.sha256}; the partial file was deleted")
    part.replace(final)
    print(f"{source.name}: downloaded and verified -> {final}", file=out)
    return "downloaded"


def _copy(response, f, have: int, source: Source, out) -> None:
    done = have
    last = 0.0
    while chunk := response.read(CHUNK):
        f.write(chunk)
        done += len(chunk)
        now = time.monotonic()
        if now - last >= 1 or done == source.size:
            last = now
            print(f"{source.name}: {done / source.size:6.1%}  {done:,} / {source.size:,} bytes", file=out, flush=True)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Download the upstream model weights Revelei exports to ONNX.")
    parser.add_argument("--dest", type=Path, default=DEFAULT_DEST, help="cache folder (default: .cache/weights)")
    parser.add_argument("--only", action="append", default=[], metavar="NAME", help="download just this model; repeatable")
    parser.add_argument("--include", action="append", default=[], metavar="NAME", help="also download an optional model; repeatable")
    parser.add_argument("--sources", type=Path, default=SOURCES, help=argparse.SUPPRESS)
    args = parser.parse_args(argv)

    try:
        chosen = select(load_sources(args.sources), args.only, args.include)
        failed = []
        for source in chosen:
            try:
                fetch(source, args.dest)
            except DownloadError as error:
                print(f"ERROR {error}", file=sys.stderr)
                failed.append(source.name)
    except DownloadError as error:
        print(f"ERROR {error}", file=sys.stderr)
        return 2

    if failed:
        print(f"{len(failed)} of {len(chosen)} failed: {', '.join(failed)}", file=sys.stderr)
        return 1
    print(f"all {len(chosen)} model(s) present in {args.dest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
