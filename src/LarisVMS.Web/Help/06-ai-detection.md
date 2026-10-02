# AI detection

Object detection on the node: models, hardware, thresholds, snapshots and the external backend.

Each node can run real-time object detection on its cameras. Detected objects get live bounding boxes, a colored mark on the timeline, a badge on the live tile and a cropped snapshot. A detection also counts as motion for Motion-mode recording. Detection runs in a separate process, so a GPU or driver problem never stops recording.

Turn it on per camera with **Watch this camera for objects**. Only **5–7 fps per camera** is needed for accurate detection, so cap the [frame rate](#thresholds) there to keep GPU load down. Global defaults are under **Settings → AI detection**. Nodes and cameras can override most of them.

## Hardware

One node package covers every machine. At startup, the vision service picks a backend for the hardware it finds:

| Hardware | Backend | Needs on the node |
|---|---|---|
| NVIDIA GPU | CUDA (falls back to DirectML) | CUDA Toolkit 12.x and cuDNN 9.x |
| AMD / Intel GPU | DirectML | A current GPU driver |
| No GPU | CPU | Nothing |

The node's edit page shows the detected accelerators, and its **Accelerator** setting can force one. Models are never bundled. A node fetches the built-in YOLOX models from the server, and other models go in `C:\ProgramData\LarisVMS\models` on the node.

## Model families

The model runs once per node and serves all of that node's cameras, so the model settings are per node, not per camera.

- **Auto / YOLOX**: the default. Fast, needs no letterboxing, and works well on low-power and non-NVIDIA GPUs. **YOLOX size** goes from Nano (fastest) to X (most accurate). Use Nano or Tiny on low-power machines and L or X on strong GPUs.
- **D-FINE**: experimental. Results can be unpredictable, and objects that straddle two tiles in Slice mode can be missed or duplicated. **Weights**: Obj2Coco knows 80 object types (small or medium), and Obj365 knows 365.
- **Custom**: any `.onnx` model dropped into the node's models folder. See [Custom models](#custom-models).

Changing the model restarts each camera's detection.

## TensorRT

**D-FINE TensorRT** only does something when the node has TensorRT enabled (`Vision:EnableTensorRt`) and the TensorRT 10.x SDK installed. **FP32** gives a safe speedup. **FP16** is fastest; if a camera's engine overflows at FP16, it rebuilds at FP32 automatically. The first start at each camera resolution takes several minutes to build the engine.

## Custom models

Drop an `.onnx` file into `C:\ProgramData\LarisVMS\models` on the node. If the model's own metadata describes its decoder, input size and labels, it just works. Otherwise add a `.json` file with the same name that supplies what's missing. **Refresh** lists every model the nodes found, and flags any whose metadata is incomplete. A model with a warning can be selected but won't run until the warning is fixed.

## Aspect fitting

How a camera's frame fits the model's square input:

- **Letterbox** (default): keeps proportions and pads with black bars.
- **Stretch**: squashes the frame to fill the square, which distorts shapes.
- **Slice**: cuts the frame into overlapping full-resolution squares. It's best for small or distant objects on wide cameras, but costs about 2× the work on a 16:9 camera. It needs GPU preprocessing, and turns that on automatically.

**GPU frame preprocessing** moves colour conversion off the CPU and onto the GPU, which helps on nodes with many cameras.

## Thresholds

- **Confidence**: the minimum score for a detection to count. Lower catches more objects but adds false positives. Raise it on a camera that keeps misdetecting something.
- **IoU**: how much two boxes must overlap to be merged as one object.
- **Detection stream**: Sub is lighter; Main sees more detail.
- **Orientation**: some corridor-mounted cameras report a landscape stream but send portrait video. Set **Portrait** on those cameras so detection and snapshots aren't squashed.
- **Max detection frame rate**: frames per second per camera that reach the model. **5–7 fps is enough for accurate detection and tracking.** Higher rates add GPU load without improving results. 0 is no cap.

## Snapshots

Each tracked object gets a snapshot, cropped from its best frame.

- **Idle timeout**: how long an object can go unseen before its snapshot closes.
- **Report idle objects**: also report objects that are present but not moving, such as a parked car.
- **Snapshot motion accuracy**: closes a snapshot soon after its object leaves, so a later object of the same type isn't merged into it. **Departure grace** is how long to wait. If it's too short, an object that pauses gets split into two snapshots.
- **Snapshot crop margin**: extra room around the object, as a percentage of its box.

## Jitter

A distant parked vehicle's box can wobble from frame to frame and look like movement. **Stationary-object jitter rejection** smooths this out, and the **jitter threshold** is the minimum movement in pixels that counts. It can also hold a genuinely slow-moving object as idle, so it's off by default. Turn it on for the specific cameras that have a parked vehicle in view.

## External backend

**External HTTP service** runs no model on the node. Each frame is sent to an inference server you run, which returns boxes as JSON. Use it for models LarisVMS doesn't bundle, or for nodes without a GPU. Tracking, snapshots, the timeline and the live overlay work the same way.

The service must expose `POST /v1/detect`, `GET /v1/models` and `GET /healthz`. **Test** checks the connection and loads the model list. The **API key** is sent as a Bearer token. **Transport**: JPEG works with any service. The raw YUV and BGRA modes skip JPEG encoding but use 4–10× the bandwidth, and only work if the model advertises them.
