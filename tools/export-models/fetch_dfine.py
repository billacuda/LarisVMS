"""Fetches D-FINE detection weights (Apache-2.0) as ready-to-run ONNX from Hugging Face.

Replaces export.py/onnx_compat.py entirely (see git history) — those adapted YOLOv9 weights to
impersonate an Ultralytics architecture so YoloDotNet's own decoder would accept them, which is
exactly the approach LarisVMS.Vision no longer needs: DFineEngine/DFineDecoder run D-FINE's real
ONNX output directly (see DFineEngine's own doc comment for why). There is no export/adaptation
step here at all — onnx-community already publishes D-FINE as plain ONNX, so this is a download
plus a rename plus a license note.

Pinned revisions, not "main": a rebuild months from now must produce byte-identical files, not
whatever the upstream repo happens to contain that day.

Run:
    .venv/Scripts/python fetch_dfine.py                # both default variants (small)
    .venv/Scripts/python fetch_dfine.py --weights obj365
"""

from __future__ import annotations

import argparse
from pathlib import Path

from huggingface_hub import hf_hub_download

REPO_ROOT = Path(__file__).resolve().parents[2]
MODELS_DIR = REPO_ROOT / "models"

# Every onnx-community D-FINE repo names its own export "onnx/model.onnx" — DetectionModelCatalog
# (LarisVMS.Vision/Inference/DetectionModelCatalog.cs) expects these exact renamed filenames
# instead; downloading both variants unrenamed would collide when build-node.ps1 flattens them.
VARIANTS = {
    "obj2coco": {
        "repo_id": "onnx-community/dfine_s_obj2coco-ONNX",
        "revision": "f69c4ca98cba7ca58aa15b3d4600867808fecf1b",
        "filename": "dfine_s_obj2coco.onnx",
        "license": "Apache-2.0",
        "upstream": "ustc-community/dfine-small-obj2coco (Peterande/D-FINE)",
        "extra_note": (
            "Trained on Objects365 then fine-tuned on COCO's 80-class vocabulary — same classes\n"
            "DetectionCategoryMap already resolves for the (now-removed) YOLOv9 pipeline.\n"
        ),
    },
    "obj365": {
        "repo_id": "onnx-community/dfine_s_obj365-ONNX",
        "revision": "a61e4cdfe4f9d3188a305d91e37dbf38688ffbb8",
        "filename": "dfine_s_obj365.onnx",
        "license": "Apache-2.0",
        "upstream": "ustc-community/dfine-small-obj365 (Peterande/D-FINE)",
        "extra_note": (
            "Trained directly on Objects365's 365-class vocabulary (DFineLabels.Obj365).\n\n"
            "Objects365 dataset note: annotations are CC BY 4.0; the source images are under the\n"
            "Flickr Terms of Use and are NOT redistributed here or anywhere in this project — only\n"
            "the derived model weights are. See https://www.objects365.org/download.html.\n"
        ),
    },
}


def write_license(dest: Path, variant: dict) -> None:
    """Drops a licence note beside the model so provenance travels with the artifact."""
    note = dest.with_suffix(".LICENSE.txt")
    note.write_text(
        f"Model: {dest.name}\n"
        f"License: {variant['license']}\n"
        f"Upstream: {variant['upstream']}\n"
        f"Source: https://huggingface.co/{variant['repo_id']} (revision {variant['revision']})\n\n"
        f"{variant['extra_note']}",
        encoding="utf-8",
    )


def fetch_one(name: str, variant: dict) -> Path:
    print(f"\n=== fetching D-FINE {name} ===", flush=True)
    downloaded = hf_hub_download(
        repo_id=variant["repo_id"],
        filename="onnx/model.onnx",
        revision=variant["revision"],
    )

    dest = MODELS_DIR / variant["filename"]
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_bytes(Path(downloaded).read_bytes())
    write_license(dest, variant)
    return dest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--weights", nargs="+", choices=sorted(VARIANTS), default=sorted(VARIANTS))
    args = parser.parse_args()

    fetched = [fetch_one(name, VARIANTS[name]) for name in args.weights]

    print("\nFetched:")
    for path in fetched:
        size_mb = path.stat().st_size / (1024 * 1024)
        print(f"  {path.relative_to(REPO_ROOT)}  ({size_mb:.1f} MB)")


if __name__ == "__main__":
    main()
