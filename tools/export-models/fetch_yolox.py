"""Fetches YOLOX detection models as ready-to-run ONNX.

YOLOX (Apache-2.0, Megvii-BaseDetection/YOLOX) is fully-convolutional, so it runs at any
stride-aligned input size with no letterbox — which is why LarisVMS uses it as the default
detection engine. YoloXEngine/YoloXDecoder (LarisVMS.Vision/Inference) run YOLOX's own ONNX output
directly, so there is no export/adaptation step here — just a download plus a rename plus a
license note, same shape as fetch_dfine.py.

LarisVMS wants the **standard Megvii export** — a single concatenated output tensor
[1, N, 5 + numClasses] with raw box columns (grid offsets + log sizes). YoloXDecoder does the
grid/stride decode and per-class NMS in C# (and auto-detects a pre-decoded model too). Input is
BGR, 0-255, no normalization. Letterbox bars are **black (0)**: Megvii's preproc() fills the pad
with 114, but with LarisVMS's centre-pad geometry (Megvii corner-pads) a 114 fill was tried and
regressed classification badly (cars read as trains/airplanes) — black is stable. Matching Megvii
would take corner-pad + 114 together. If a URL 404s, export with Megvii's tools/export_onnx.py
(default flags) or LibreYOLO (MIT) and re-pin — or just drop the .onnx into the server's
detection-models/ cache directory by hand.

These files are NOT bundled into the node package. They seed the server's detection-models cache
directory ({contentRoot}/detection-models/) — copy models/yolox_*.onnx there on deploy — from which
each node fetches the one size it needs on first use. For local dev, drop them wherever the Vision
Service's models directory points.

Run:
    .venv/Scripts/python fetch_yolox.py                 # all sizes
    .venv/Scripts/python fetch_yolox.py --sizes s m
"""

from __future__ import annotations

import argparse
import hashlib
import sys
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MODELS_DIR = REPO_ROOT / "models"

# Pinned to a specific release tag — a rebuild months from now must produce the same files.
# DetectionModelCatalog.GetYoloXFileName expects these exact names. Each file's SHA-256 is pinned
# too (the same values DetectionModelDistributor checks on the server), so a changed or tampered
# release asset is refused instead of being handed to every node.
_BASE = "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0"
SIZES = {
    "nano": {"filename": "yolox_nano.onnx", "url": f"{_BASE}/yolox_nano.onnx", "input": 416,
             "sha256": "c789161ed43c8269fcd4e67c67eeeb4e80c622da2eb296a20bc6007bd18a0b7d"},
    "tiny": {"filename": "yolox_tiny.onnx", "url": f"{_BASE}/yolox_tiny.onnx", "input": 416,
             "sha256": "427cc366d34e27ff7a03e2899b5e3671425c262ea2291f88bb942bc1cc70b0f7"},
    "s": {"filename": "yolox_s.onnx", "url": f"{_BASE}/yolox_s.onnx", "input": 640,
          "sha256": "c5c2d13e59ae883e6af3b45daea64af4833a4951c92d116ec270d9ddbe998063"},
    "m": {"filename": "yolox_m.onnx", "url": f"{_BASE}/yolox_m.onnx", "input": 640,
          "sha256": "21ff6cfdeb53b013bac2249599e55f00bff3cfdfdab37ed7a4620818c1d15b3f"},
    "l": {"filename": "yolox_l.onnx", "url": f"{_BASE}/yolox_l.onnx", "input": 640,
          "sha256": "7860ae79de6c89a3c1eb72ae9a2756c0ccfbe04b7791bb5880afabd97855a411"},
    "x": {"filename": "yolox_x.onnx", "url": f"{_BASE}/yolox_x.onnx", "input": 640,
          "sha256": "c892d7aaf1c4746d8a4d675bec669a4db4f434b4ee1efb654bc9b353379c7c55"},
}

LICENSE_NOTE = (
    "License: Apache-2.0\n"
    "Upstream: Megvii-BaseDetection/YOLOX (https://github.com/Megvii-BaseDetection/YOLOX)\n"
    "Classes: COCO-80 (DFineLabels.YoloXCoco)\n"
    "Export: standard Megvii ONNX (single output [1, N, 85], raw box columns; grid decode + NMS in C#).\n"
)


def fetch_one(name: str, spec: dict) -> Path:
    print(f"\n=== fetching YOLOX {name} ({spec['input']}x{spec['input']}) ===", flush=True)
    dest = MODELS_DIR / spec["filename"]
    dest.parent.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(spec["url"]) as response:  # noqa: S310 - pinned github release URL
        data = response.read()
    actual = hashlib.sha256(data).hexdigest()
    if actual != spec["sha256"]:
        # Nothing is written: a file that isn't the pinned one never reaches the models folder.
        sys.exit(f"{spec['url']} isn't the expected file (SHA-256 {actual}, expected {spec['sha256']}).")
    dest.write_bytes(data)
    dest.with_suffix(".LICENSE.txt").write_text(
        f"Model: {dest.name}\nSource: {spec['url']}\n\n{LICENSE_NOTE}", encoding="utf-8"
    )
    return dest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sizes", nargs="+", choices=sorted(SIZES), default=sorted(SIZES))
    args = parser.parse_args()

    fetched = [fetch_one(name, SIZES[name]) for name in args.sizes]

    print("\nFetched:")
    for path in fetched:
        size_mb = path.stat().st_size / (1024 * 1024)
        print(f"  {path.relative_to(REPO_ROOT)}  ({size_mb:.1f} MB)")


if __name__ == "__main__":
    main()
