"""ONNX metadata and graph adaptations that make LibreYOLO exports load in YoloDotNet.

Three separate incompatibilities, none of which are bugs in either project -- they are just two
independent implementations that were never written against each other:

  1. metadata 'description' is absent      -> YoloDotNet cannot resolve the architecture at all
  2. metadata 'names' is JSON              -> YoloDotNet's parser raises FormatException
  3. box channels are xyxy                 -> YoloDotNet's V8/V9 decoder assumes cxcywh

The first two are metadata rewrites. The third is a small graph append: four channels are
recombined into centre/size form and concatenated back in front of the class scores, leaving the
tensor shape unchanged at [1, 84, N]. Doing it here rather than in C# keeps YoloDotNet's optimised
decode path -- AVX2 preprocessing, pooled buffers, NMS, drawing and tracker integration -- intact.
"""

from __future__ import annotations

import json
from pathlib import Path

import onnx
from onnx import TensorProto, helper, numpy_helper
import numpy as np


def rewrite_metadata(model: onnx.ModelProto, arch_tag: str, licence: str, upstream: str) -> bool:
    """Stamps 'description' and converts 'names' to the dict form YoloDotNet parses."""
    props = {p.key: p.value for p in model.metadata_props}
    changed = False

    if "description" not in props:
        entry = model.metadata_props.add()
        entry.key = "description"
        entry.value = (
            f"{arch_tag} architecture. "
            f"NOT AN ULTRALYTICS MODEL AND NOT AGPL: these are LibreYOLO weights, "
            f"{licence} licensed, upstream {upstream}. The 'ultralytics' prefix is a "
            f"compatibility shim required by YoloDotNet's ParseOnnxData.GetModelVersion, "
            f"which accepts no other form. Architecture tag only; it makes no claim about "
            f"origin, authorship or licence."
        )
        changed = True
        print(f"  description  -> {arch_tag}")

    names_raw = props.get("names", "")
    if names_raw.lstrip().startswith('{"'):
        names = json.loads(names_raw)
        ordered = sorted(names.items(), key=lambda kv: int(kv[0]))

        # YoloDotNet splits the string on ", " and ": " and strips single quotes, so a label
        # containing any of those would silently corrupt the entire label map.
        for _, label in ordered:
            if ", " in label or ": " in label or "'" in label:
                raise SystemExit(
                    f"label {label!r} contains a separator YoloDotNet's parser cannot handle"
                )

        rewritten = "{" + ", ".join(f"{int(k)}: '{v}'" for k, v in ordered) + "}"
        for entry in model.metadata_props:
            if entry.key == "names":
                entry.value = rewritten
                break

        changed = True
        print(f"  names        -> Ultralytics dict form ({len(ordered)} classes)")

    return changed


def convert_boxes_to_cxcywh(model: onnx.ModelProto, num_classes: int = 80) -> bool:
    """Appends nodes converting the leading 4 box channels from xyxy to cxcywh.

    LibreYOLO's YOLO9 head emits [x1, y1, x2, y2, ...class scores] on a [1, 84, N] tensor.
    YoloDotNet's ObjectDetectionModuleV8 -- which V9 delegates to -- reads those four channels as
    [cx, cy, w, h]. Reinterpreting one as the other yields boxes that are roughly half a frame off
    and often negative, while class predictions still look correct, which makes this a
    particularly easy mismatch to misdiagnose.
    """
    graph = model.graph
    output = graph.output[0]

    if any(node.name == "aitest_cxcywh_concat" for node in graph.node):
        print("  boxes        -> already converted")
        return False

    original_name = output.name
    internal_name = f"{original_name}_xyxy"

    # Re-point the producing node at an internal name, freeing the public output name for the
    # converted tensor so downstream consumers see no interface change.
    for node in graph.node:
        for i, name in enumerate(node.output):
            if name == original_name:
                node.output[i] = internal_name

    split_lengths = numpy_helper.from_array(
        np.array([1, 1, 1, 1, num_classes], dtype=np.int64), name="aitest_split_lengths"
    )
    half = numpy_helper.from_array(
        np.array([0.5], dtype=np.float32), name="aitest_half"
    )
    graph.initializer.extend([split_lengths, half])

    nodes = [
        helper.make_node(
            "Split",
            inputs=[internal_name, "aitest_split_lengths"],
            outputs=["aitest_x1", "aitest_y1", "aitest_x2", "aitest_y2", "aitest_scores"],
            axis=1,
            name="aitest_split",
        ),
        helper.make_node("Add", ["aitest_x1", "aitest_x2"], ["aitest_x_sum"], name="aitest_x_sum_n"),
        helper.make_node("Add", ["aitest_y1", "aitest_y2"], ["aitest_y_sum"], name="aitest_y_sum_n"),
        helper.make_node("Mul", ["aitest_x_sum", "aitest_half"], ["aitest_cx"], name="aitest_cx_n"),
        helper.make_node("Mul", ["aitest_y_sum", "aitest_half"], ["aitest_cy"], name="aitest_cy_n"),
        helper.make_node("Sub", ["aitest_x2", "aitest_x1"], ["aitest_w"], name="aitest_w_n"),
        helper.make_node("Sub", ["aitest_y2", "aitest_y1"], ["aitest_h"], name="aitest_h_n"),
        helper.make_node(
            "Concat",
            inputs=["aitest_cx", "aitest_cy", "aitest_w", "aitest_h", "aitest_scores"],
            outputs=[original_name],
            axis=1,
            name="aitest_cxcywh_concat",
        ),
    ]

    graph.node.extend(nodes)
    print("  boxes        -> xyxy converted to cxcywh")
    return True


def adapt(path: Path, arch_tag: str, licence: str, upstream: str, num_classes: int = 80) -> None:
    """Applies every adaptation and saves in place if anything changed."""
    model = onnx.load(str(path))

    changed = rewrite_metadata(model, arch_tag, licence, upstream)
    changed |= convert_boxes_to_cxcywh(model, num_classes)

    if not changed:
        print("  already compatible")
        return

    onnx.checker.check_model(model)
    onnx.save(model, str(path))
