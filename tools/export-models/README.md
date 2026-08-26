# Model fetch tooling

Fetches D-FINE detection weights (Apache-2.0, [Peterande/D-FINE](https://github.com/Peterande/D-FINE))
as ready-to-run ONNX from Hugging Face — `LarisVMS.Vision`'s `DFineEngine`/`DFineDecoder` run this
output directly via `Microsoft.ML.OnnxRuntime`, with no export or graph-adaptation step needed (see
`fetch_dfine.py` and `DFineEngine`'s own doc comments for why — this used to adapt YOLOv9 weights to
impersonate an Ultralytics architecture for YoloDotNet's own decoder; that whole approach is gone).

Two weight variants are fetched by default, both the "small" (10.7M param) size:

- `obj2coco` — trained on Objects365 then fine-tuned on COCO's 80-class vocabulary. Default.
- `obj365` — trained directly on Objects365's own 366-class vocabulary (365 real classes + a
  padding slot). Richer, but needs `DetectionCategoryMap`'s broader Objects365 coverage to resolve
  correctly.

## Setup

```
py -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
```

## Run

```
.venv\Scripts\python fetch_dfine.py                # both default variants
.venv\Scripts\python fetch_dfine.py --weights obj365
```

Output lands directly under `models/` at the repo root (gitignored — see `.gitignore`) as
`dfine_s_obj2coco.onnx` / `dfine_s_obj365.onnx` — exact filenames `DetectionModelCatalog`
(`LarisVMS.Vision/Inference/DetectionModelCatalog.cs`) expects, since every `onnx-community`
source repo names its own export the generic `onnx/model.onnx`. Each fetch gets a `.LICENSE.txt`
note recording its real license/upstream/revision, plus (for `obj365`) an explicit note that only
the derived model weights are redistributed here — never the Objects365 dataset's own source
images, which are under the Flickr Terms of Use, not Objects365's own CC BY 4.0 annotation license.
`build-node.ps1` searches `models/` recursively for `.onnx` files and flattens whatever it finds
into the node package alongside `LarisVMS.Vision.Service.exe`.
