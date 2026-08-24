"""Export permissively-licensed detection models to ONNX for the .NET service.

Only families whose *weights* are permissively licensed are exported. Ultralytics YOLOv8/11/26 are
deliberately absent: their weights are AGPL-3.0 and this project is MIT.

    yolo9-{t,s,m,c}   MIT        upstream MultimediaTechLab/YOLO

Run:
    .venv/Scripts/python export.py                # default set
    .venv/Scripts/python export.py --models yolo9-t yolo9-s
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

import onnx_compat

REPO_ROOT = Path(__file__).resolve().parents[2]
MODELS_DIR = REPO_ROOT / "models"

# Families verified to decode correctly through YoloDotNet. See README_COMPAT.md for why RT-DETR
# is not in this list.
DEFAULT_MODELS = ["yolo9-t", "yolo9-s"]

# The architecture tag YoloDotNet must see in the ONNX "description" metadata to select a decoder.
# See onnx_compat.py for why this is needed and why the stamped value says loudly that these are
# not Ultralytics weights.
#
# RT-DETR is deliberately absent. LibreYOLO exports it with the DETR-native split outputs
# (pred_logits [1,300,80] + pred_boxes [1,300,4]), while YoloDotNet's RTDETR module expects the
# two fused into a single [1,300,84] tensor the way Ultralytics emits it. That needs a real graph
# merge -- concat plus a sigmoid on the logits and a box rescale -- not just a metadata shim, so
# it is left out until someone verifies it numerically rather than guessing.
ARCH_TAG = {
    "yolo9": "ultralytics yolov9",
}

LICENSES = {
    "yolo9": ("MIT", "MultimediaTechLab/YOLO"),
}


def export_one(model: str, imgsz: int, venv_python: Path) -> Path:
    """Runs the LibreYOLO exporter and returns the path to the produced .onnx."""
    print(f"\n=== exporting {model} ===", flush=True)

    # Invoke the console script next to this interpreter rather than `-m`, since libreyolo.cli is
    # a package without a __main__.
    cli = venv_python.with_name("libreyolo.exe")
    if not cli.exists():
        cli = venv_python.with_name("libreyolo")

    result = subprocess.run(
        [
            str(cli),
            "export", "--model", model, "--format", "onnx", "--imgsz", str(imgsz),
        ],
        cwd=MODELS_DIR,
        capture_output=True,
        text=True,
    )

    if result.returncode != 0:
        print(result.stdout)
        print(result.stderr, file=sys.stderr)
        raise SystemExit(f"export failed for {model}")

    produced = sorted(
        (MODELS_DIR / "weights").glob("*.onnx"), key=lambda p: p.stat().st_mtime
    )
    if not produced:
        raise SystemExit(f"no .onnx produced for {model}")

    return produced[-1]


def make_yolodotnet_compatible(onnx_path: Path, family: str) -> None:
    """Applies the metadata and graph adaptations documented in onnx_compat.py."""
    tag = ARCH_TAG.get(family)
    if tag is None:
        raise SystemExit(f"no architecture tag registered for family '{family}'")

    licence, upstream = LICENSES[family]
    onnx_compat.adapt(onnx_path, tag, licence, upstream)


def write_license(family: str, onnx_path: Path) -> None:
    """Drops a licence note beside the model so provenance travels with the artifact."""
    licence, upstream = LICENSES[family]
    note = onnx_path.with_suffix(".LICENSE.txt")
    note.write_text(
        f"Model: {onnx_path.name}\n"
        f"License: {licence}\n"
        f"Upstream: {upstream}\n"
        f"Redistributed via: https://huggingface.co/LibreYOLO\n\n"
        f"This model is NOT an Ultralytics model and is NOT AGPL licensed. The ONNX\n"
        f"'description' metadata begins with 'ultralytics' purely as a compatibility\n"
        f"shim for YoloDotNet's architecture detection; see export.py for details.\n",
        encoding="utf-8",
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--models", nargs="+", default=DEFAULT_MODELS)
    parser.add_argument("--imgsz", type=int, default=640)
    args = parser.parse_args()

    MODELS_DIR.mkdir(parents=True, exist_ok=True)
    venv_python = Path(sys.executable)

    exported = []
    for model in args.models:
        family = model.split("-")[0]
        if family not in ARCH_TAG:
            raise SystemExit(
                f"'{model}' is not a supported family. Supported: {sorted(ARCH_TAG)}"
            )

        onnx_path = export_one(model, args.imgsz, venv_python)
        make_yolodotnet_compatible(onnx_path, family)
        write_license(family, onnx_path)
        exported.append(onnx_path)

    print("\nExported:")
    for path in exported:
        size_mb = path.stat().st_size / (1024 * 1024)
        print(f"  {path.relative_to(REPO_ROOT)}  ({size_mb:.1f} MB)")


if __name__ == "__main__":
    main()
