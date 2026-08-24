# Model export tooling

Exports permissively-licensed YOLO detection weights to ONNX and adapts them to load in
[YoloDotNet](https://github.com/NickSwardh/YoloDotNet) — the inference library `LarisVMS.Vision`
wraps. Ported unchanged from `aitest` (the standalone reference/debug repo this feature was
prototyped in) so LarisVMS never depends on that repo for its `.onnx` files — see `export.py` and
`onnx_compat.py`'s own doc comments for exactly what each adaptation does and why.

Only families whose *weights* are permissively licensed are exported. Ultralytics YOLOv8/11/26 are
deliberately absent: their weights are AGPL-3.0, and this project is MIT.

## Setup

```
py -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
```

## Run

```
.venv\Scripts\python export.py                # default set (yolo9-t, yolo9-s)
.venv\Scripts\python export.py --models yolo9-t yolo9-s
.venv\Scripts\python export.py --models yolo9-m --imgsz 640
```

Output lands in `models/` at the repo root (gitignored — see `.gitignore`), alongside a
`.LICENSE.txt` note per model recording its actual license/upstream, since the ONNX metadata itself
carries an `ultralytics`-prefixed architecture tag purely as a YoloDotNet compatibility shim (not a
claim about origin or license — `onnx_compat.py`'s `rewrite_metadata` explains why that string is
required). `build-node.ps1` bundles whatever's in `models/` alongside `LarisVMS.Vision.Service.exe`
when packaging a node for deployment.
