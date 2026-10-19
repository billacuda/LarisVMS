# AI detection

Object detection on the node: models, hardware, thresholds, snapshots, and the external backend.

Each node can run real-time object detection on its cameras. Detected objects get live bounding boxes, a colored mark on the timeline, a badge on the live tile, and a cropped snapshot. A detection also counts as motion for Motion-mode recording. Detection runs in a separate process, so a GPU or driver problem never stops recording.

Turn it on per camera with **Watch this camera for objects**. Only **5–7 fps per camera** is needed for accurate detection, so cap the [frame rate](#thresholds) there to keep GPU load down. Global defaults are under **Settings → AI detection**. Nodes and cameras can override most of them.

## Hardware

One node package covers every machine. At startup, the vision service picks a backend for the hardware it finds:

| Hardware | Backend | Needs on the node |
|---|---|---|
| NVIDIA GPU | CUDA (falls back to DirectML) | CUDA Toolkit 12.x and cuDNN 9.x |
| AMD / Intel GPU | DirectML | A current GPU driver |
| No GPU | CPU | Nothing |

The node's edit page shows the detected accelerators, and its **Accelerator** setting can force one. Models are never bundled. A node fetches the built-in YOLOX models from the server, and other models go in `C:\ProgramData\LarisVMS\models` on the node.

## Slow backend

If an NVIDIA node can't use CUDA, AI detection falls back to DirectML, which runs several times slower. Fewer frames get analysed, the GPU runs hot, and fast objects such as passing cars can go undetected. The **Nodes** page then shows **⚠️ AI on DirectML** next to the node, with the reason, and Super Admins get a notification.

The usual cause is a missing cuDNN. Install the CUDA Toolkit 12.x and cuDNN 9.x for CUDA 12 on the node, from NVIDIA's cuDNN download or with `pip install nvidia-cudnn-cu12`. The node finds them within 10 minutes, copies them next to its AI backend, and restarts detection on CUDA.

## Model families

The model runs once per node and serves all of that node's cameras, so the model settings are per node, not per camera.

- **Auto / YOLOX**: the default. Fast, needs no letterboxing, and works well on low-power and non-NVIDIA GPUs. **YOLOX size** goes from Nano (fastest) to X (most accurate). Use Nano or Tiny on low-power machines and L or X on strong GPUs.
- **D-FINE**: experimental. Results can be unpredictable, and objects that straddle two tiles in Slice mode can be missed or duplicated. **Weights**: Obj2Coco knows 80 object types (small or medium), and Obj365 knows 365.
- **Custom**: any `.onnx` model dropped into the node's models folder. See [Custom models](#custom-models).

Changing the model restarts each camera's detection.

## TensorRT

**D-FINE TensorRT** only does something when the node has TensorRT enabled (`Vision:EnableTensorRt`) and the TensorRT 10.x SDK installed. **FP32** gives a safe speedup. **FP16** is fastest; if a camera's engine overflows at FP16, it rebuilds at FP32 automatically. The first start at each camera resolution takes several minutes to build the engine.

## Custom models

Drop an `.onnx` file into `C:\ProgramData\LarisVMS\models` on the node. If the model's own metadata describes its decoder, input size, and labels, it just works. Otherwise add a `.json` file with the same name that supplies what's missing. **Refresh** lists every model the nodes found, and flags any whose metadata is incomplete. A model with a warning can be selected but won't run until the warning is fixed.

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

Several objects of the same type in one snapshot show as a count (×2). Each one is also kept separately with its own colors and speed.

## Object colors

Each tracked object's color is read from its clearest frame: one color for vehicles, animals, and other objects, and shirt and pants colors for people. Snapshots show a badge for each, painted in that color, and the **Color**, **Shirt**, and **Pants** sections of the Snapshots filter narrow results to the colors you tick. Detection alerts can filter by color too.

Colors come from the picture, not the model, so they work with any detector. The colors just outside the box are sampled as well and set aside, so the lawn around a dog or the road around a car doesn't win. They're still best-effort guesses: busy scenes, overlapping objects, and lighting can all give the wrong color. Lighting matters most: shade, headlights, and white balance shift colors, and a light gray car may read as silver or white. While a camera is in black-and-white night (IR) mode no colors are recorded. Turn it off with **Object colors** under **Settings → AI detection**.

## Speed traps

A speed trap times objects between two lines across a road. On the camera's **Speed traps** page (from **Cameras → Edit**), click **New speed trap**, click the two ends of line **A**, then the two ends of line **B**, and enter the real distance between the lines, measured along the road. Every tracked object that crosses both lines, in either direction, gets a speed, shown on its live box and its snapshot. **Speed units** (km/h or mph) are under **Settings → AI detection**.

An object a trap times is recorded as moving even if it otherwise looks still, such as a vehicle driving straight toward the camera. The timing uses the bottom middle of each box, where the object touches the ground, so draw the lines on the road surface. For a good reading:

- Draw the lines across the direction of travel, on flat ground, and as far apart as the view allows while objects stay clearly in view.
- Measure the distance carefully: pace it out, or use a map's measuring tool.
- Check it once with a vehicle driven past at a known speed, and adjust the distance if the reading is off.

Readings are estimates. The camera's detection frame rate limits how precisely a crossing is timed, so short traps and fast vehicles are least accurate. Saving a trap restarts the camera's AI detection.

A trap can have a **speed limit** (optional). Speeds over it are shown in red and counted separately in the speed heatmap.

Measured speeds show up in several places:

- **Live view**: on each object's box while it's in view.
- **Snapshots**: a ⏱ badge on the card, and a **Speed** filter ("at or over") in the filter list.
- **Playback**: a tick on the timeline at each measurement (red when over the limit; hover for details), and a badge over the video while the object is on screen.
- **Dashboard**: the **Speed log** widget shows the latest measurements. Click its title for the full log, with sorting, filtering, and CSV download.
- **Activity**: switch to **Speeds** for a heatmap of average speed, top speed, number over the limit, or number measured, by day and hour. History is kept as long as the activity history, long after the footage is gone.
- **Alerts**: a "Speed over a limit" rule.

## Movement

Snapshots, alerts, and the live **Moving** boxes only count objects that are going somewhere. A detector's box wobbles a little from frame to frame even around a parked car, so a few pixels of change isn't enough. **Movement detection** under **Settings → AI detection**, or per camera on **Cameras → Edit**, decides how this is judged.

**Standard** (the default) looks at the shape of each object's recent path:

- Anything that moves steadily in one direction counts within a few frames (half a second or so at 7 fps), whether it's a car crossing the view in under a second or a truck pulling out of a driveway. Each object's steps are compared with how much its own box wobbles, so a large object doesn't have to travel further than a small one.
- Something creeping more slowly than its box wobbles, a fraction of a walking pace, counts once its path has kept to a steady line for a couple of seconds. Wobble goes back and forth, so it never builds a line.
- How far an object has to go grows with its size, but more slowly than the size itself. A large truck doesn't have to cross half its own length first.
- An object coming in at the edge of the picture, or out from behind a pole or a tree, is judged by the side of its box that is in view.
- An object driving straight toward or away from the camera counts as it grows or shrinks.
- A parked car's box changing as someone walks in front of it doesn't count.

**Movement sensitivity** (Standard only) is **Normal** by default. **High** picks up movement sooner and **Low** ignores more wobble. Lower it on a camera where a parked car or a swaying bush is reported as moving, and raise it where slow objects are missed.

A box that just changes shape, like a potted plant's when the detector includes the leaves one moment and not the next, or a parked car's as something passes in front of it, doesn't count. Movement has to build up across the frames, not happen in one jump.

**Legacy** is the earlier rule: an object counts once its centre has moved half its box's size within 2 seconds. Use it if Standard misbehaves on a camera.

An object that was moving and goes out of sight behind a tree, a post, or a parked car stays the same object when it comes out further along the same line within about 30 analysed frames (around 5 seconds at 6 fps), so it's counted once and keeps one path. It also has to be the same kind of object and a similar size, and its color has to match when both can be told. Something that comes back out going a different way, or after longer, is a new object.

## Moving objects

Each moving object's path through the picture is kept with its event, for the **Scene** heatmap on the Activity page.

**Moving objects** under **Settings → AI detection** chooses which kinds of object can count as moving. By default that's **People**, **Vehicles**, and **Animals**. Anything else is still detected, and shows as an idle box with **Idle** on, but never counts as moving, so it can't start a snapshot or an alert. Opt in to other objects one by one in the **Other objects** list: a delivered package usually shows up as a suitcase, backpack, or handbag. For a custom model, type its own object names, separated by commas.

## Jitter

The **jitter threshold** is the smallest movement in pixels that ever counts, whatever the object's size. In Legacy mode, **Stationary-object jitter rejection** smooths out a distant parked vehicle's wobble; it can also hold a genuinely slow-moving object as idle, so it's off by default. It doesn't apply to Standard, which handles wobble itself.

## External backend

**External HTTP service** runs no model on the node. Each frame is sent to an inference server you run, which returns boxes as JSON. Use it for models LarisVMS doesn't bundle, or for nodes without a GPU. Tracking, snapshots, the timeline, and the live overlay work the same way.

The service must expose `POST /v1/detect`, `GET /v1/models`, and `GET /healthz`. **Test** checks the connection and loads the model list. The **API key** is sent as a Bearer token. **Transport**: JPEG works with any service. The raw YUV and BGRA modes skip JPEG encoding but use 4–10× the bandwidth, and only work if the model advertises them.
