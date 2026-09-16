# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.205.0] - 2026-09-15

### Added

- **New Settings hub** (`Admin → Settings`, `Pages/Admin/Settings/Index.cshtml`) — a permission-gated
  card grid replacing the old navbar's "Admin" dropdown plus the top-level Nodes/Logs nav buttons.
  Every card links to the same real, independently-`[Authorize]`d page those used to; nothing moved,
  it's just reachable from one sidebar item instead of three.

### Changed

- **Reworked the web UI's visual theme**: the top navbar is now a sidebar + topbar shell
  (`Pages/Shared/_Layout.cshtml`), and colors/typography/zero-radius styling are layered over the
  existing Bootstrap 5.3 install via CSS variable overrides (`wwwroot/css/theme.css`) rather than
  replacing Bootstrap. Every other page's markup, forms, modals, dropdowns, and JS-driven widgets are
  unchanged — only the shell and the recolor are new.
- `install-web.ps1` now falls back to `-LegacyIisConfigPath` (default `E:\Sites\LarisVMS\setup-generated.json`,
  `deploy.ps1`'s own default `-DestinationPath`) when `-InstallDir` has no `setup-generated.json` of
  its own yet, and warns loudly (`Write-Warning`, not a `Write-Host` easy to miss in installer output)
  instead of silently skipping migrations and node-build/proxy-build registration.

### Fixed

- **Sidebar didn't follow the light/dark toggle when a Branding `AccentColor` was already
  configured** — the override was applied to the whole sidebar background with `!important`, pinning
  it to that one fixed color in every theme. Scoped to just the brand/logo strip instead, which is
  small enough to reasonably stay a fixed brand color across themes the way the old dark navbar's
  brand link always did.
- `.btn-primary`'s hover/active states reached for the accent color ramp's 600/700 steps, which
  *invert* direction in dark mode (those steps are for text sitting on a tinted fill, not a solid
  button fill) — hovering or pressing a primary button in dark mode made it turn lighter instead of
  darker. Now computed via `color-mix()` relative to the current accent, so it behaves the same in
  both themes.
- Added the `--bs-*-rgb` companion variables (`body-bg`/`secondary-bg`/`tertiary-bg`/`emphasis-color`)
  alongside their hex overrides, so Bootstrap utilities that read the RGB form for opacity math
  (`.bg-body-secondary`, `.bg-body-tertiary`, table hover tint) match the new theme instead of falling
  back to Bootstrap's own defaults.
- Cameras list's capability badges (AI detection, PTZ, Talk, Meta, I/O) used Bootstrap's
  `text-bg-light`, which ignores the theme attribute entirely — switched to a theme-aware background.
- The Setup wizard had no dark mode support at all (no toggle, `theme.css` not even linked) — added.
- Fullscreen "kiosk" mode on `Views/Play` stopped hiding the app shell — it targeted the old navbar's
  `#mainNav` id, removed along with the navbar itself. Retargeted to the new sidebar/topbar ids.

## [0.204.0] - 2026-09-15

### Changed

- **LarisVMS.Web no longer requires IIS.** It now self-hosts Kestrel directly as its own Windows
  Service, the same way the recorder node already does. Install or upgrade with the new
  `install-web.ps1` script instead of `deploy.ps1` and a manually-configured IIS site (`deploy.ps1`
  still works for existing IIS deployments during the transition, but is deprecated). Supports both a
  domain/service account for SQL Server Integrated Security and SQL Authentication under `LocalSystem`
  — see the README's "Installing / upgrading" section. The HTTPS certificate comes from
  `Kestrel:Certificates:Default:Path`/`:Password` in `appsettings.Production.json`, hot-reloaded from
  a file share every 60 seconds the same way the node's own client endpoint already works, falling
  back to a self-signed certificate when none is configured yet so a fresh install is always reachable
  at its setup wizard. The live/playback custom-port setting (Admin → Settings → Live View) now opens
  its own Kestrel listener directly instead of relying on a manually-added IIS site binding — changing
  it requires restarting the service to take effect.

### Fixed

- **Fixed several real-world issues surfaced by the IIS → Kestrel migration above, all specific to
  self-hosting Kestrel directly (IIS/ANCM never exposed the app to any of these).**
  - Live/playback/snapshots could break intermittently: self-hosted Kestrel's dual-stack `ListenAnyIP`
    socket reports an IPv4 client's address in IPv4-mapped-IPv6 form (`::ffff:10.0.0.5`), which every
    media-proxy code path was embedding straight into a node connection URL. `NodeAuthMiddleware` and
    `ProxyAuthMiddleware` now unmap it before it's ever stored as `Node.LastIpAddress` (new
    `IpAllowListPolicy.Unmap` helper, reusing that class's existing unmapping logic).
  - The live-view bounding-box overlay (`/live/{cameraId}/detections`) could silently never connect:
    once a browser has an HTTP/2 connection open to the management port (from the page and its own API
    calls), Chrome multiplexes a same-origin WebSocket onto it via HTTP/2's "extended CONNECT" instead
    of opening a fresh HTTP/1.1 connection — and Kestrel answered that with a bare 405, since this
    configuration doesn't support HTTP/2 WebSockets. The direct-to-node video socket was never affected
    (each node is a fresh origin with no prior HTTP/2 connection for the browser to prefer reusing).
    Kestrel's listeners are now restricted to HTTP/1.1, which is what IIS always effectively presented
    to the app anyway.
  - `appsettings.Production.json.example` was never actually included in a published build — the Web
    SDK's implicit content glob only picks up `*.json`, not `*.json.example` — so `install-web.ps1`'s
    "seed `appsettings.Production.json` from the example on a fresh install" step had nothing to seed
    from. Now published via an explicit `Content` item.
  - `install-node.ps1` unconditionally deleted the client-endpoint firewall rule (direct-to-node video)
    on every run, only recreating it when `-ClientPort` was passed. A plain re-run to change an
    unrelated setting (e.g. `-ServerUrl`) silently left the endpoint listening but unreachable from
    outside. It now only touches that rule when `-ClientPort` is explicitly given, matching
    `-ServiceCredential`'s existing "omit to leave it alone" behavior.

## [0.203.0] - 2026-09-14

### Fixed

- **Fixed incorrect detections on Slice-mode cameras introduced in the previous release.** After
  0.202.0 shipped cross-tile label-agnostic merging, two issues showed up in production: objects
  standing at a camera's true image edge (not a tile seam) could be wrongly absorbed into an unrelated
  neighboring object and disappear entirely, and a fast-path detection mode introduced in the same
  release could silently mislabel where a detection actually was. Cross-tile merging is now gated by
  a semantic category check (visually similar classes may still merge; unrelated classes never do,
  regardless of geometry), the seam-vs-true-frame-edge check is now aware of which tile it's looking
  at, and the fast detection path now numerically verifies its own output against a guaranteed-correct
  fallback before ever being used, falling back automatically if that check doesn't pass. Also fixed a
  pre-existing (not new) case where an object clipped by one tile but seen whole by its neighbor could
  show up as a stray extra box alongside the correct one.

## [0.202.0] - 2026-09-14

### Fixed

- **Added better support for reading metadata from ONNX models.** A model dropped into
  `C:\ProgramData\LarisVMS\models` could come back "metadata not found" even when it carried enough of
  its own embedded information to be usable without a sidecar. `ModelDiscovery` now recognizes more
  real-world embedded-metadata conventions, and falls back to the ONNX graph's own declared input size
  when available — so more models resolve without a hand-written sidecar. A model with genuinely no
  usable embedded information still correctly requires one.
- **`Custom` ONNX models can now be used on cameras configured for the Slice aspect mode.** Previously
  every such camera failed outright — Slice mode threw an error unconditionally for any `Custom` model,
  making the whole feature unusable on a camera fleet where every camera uses it. Detection now runs
  one pass per tile, cropped from the captured frame, which works correctly regardless of how the
  underlying model was exported — unlike the built-in detection models, an arbitrary user-supplied
  model can't be assumed to support a batched forward pass safely.
- **Fixed duplicate and split detections on Slice-mode cameras when two tiles disagreed on an object's
  class.** An object straddling a tile boundary (or sitting in the overlap between two tiles) is
  sometimes classified slightly differently by each tile — visually similar classes are the common
  case. Reuniting an object's detections across tiles previously required both tiles to agree on the
  exact class first, so a disagreement showed up as either two overlapping boxes with different labels,
  or two never-reunited half-boxes instead of the object's true full extent. Tiles are now reunited by
  geometry alone, keeping whichever tile's classification was more confident.

### Added

- **Custom ONNX models on Slice-mode cameras can now automatically use a faster, single-pass detection
  path when the model itself was exported to support it**, instead of always falling back to one pass
  per tile — with an automatic, transparent fallback to the safe per-tile path if the model turns out
  not to actually support it. Nothing to configure: this is detected from the model file itself at
  load time, and the node's own log states which path a given model got.

## [0.201.0] - 2026-09-13

### Changed

- **Relicensed from MIT to Apache-2.0.** Sibling project SideGlance is relicensing from AGPL-3.0 to
  Apache-2.0 at the same time, so code, config conventions, and design patterns (starting with
  SideGlance's model-descriptor/decoder system) can now be shared freely between the two repos —
  previously the AGPL boundary on SideGlance's side meant sharing could only flow one way (as a
  manual, rewritten port). This does not change the license of any model weights a user supplies —
  a model's own weights carry whatever license they came with regardless of this project's license.

### Added

- **The dashboard shows a spinner while a camera's AI-detection engine is still cold-building.**
  `CameraDetectionPipeline.IsEngineBuilding` (derived, no new state) is now surfaced end-to-end: Vision
  Service's new `GET /cameras/status`, polled by `NodeWorker` and folded into each camera's existing
  health report, persisted on two new nullable `CameraStream` columns (`IsEngineBuilding`,
  `EngineBuildFailed` — new `AddCameraStreamEngineStatus` migration), and rendered by `dashboard.js` as
  a second badge alongside the existing recording-status one (a spinner while building, a failure badge
  if it never started) — orthogonal to recording health, since a camera can be recording fine while its
  detection engine is still compiling a TensorRT engine for the first time.
- **Settings changes reach a running camera pipeline in seconds, not up to 30.** Saving a detection
  setting on `Admin → Settings → Detection` or its per-node override on `Admin → Nodes` now triggers a
  new `POST /reconcile-now` on the affected node(s) (fire-and-forget, never blocking the save) which
  wakes `NodeWorker`'s reconcile loop immediately instead of waiting out its own 30-second timer. The
  actual hot-swap (comparing each camera's config signature, rebuilding its detection pipeline if it
  changed) is unchanged — only how soon the loop gets around to running it. A node that's briefly
  unreachable is unaffected: its normal 30s poll remains the correctness backstop.
- **AI-detection backend dropdown: CUDA / TensorRT / DirectML / OpenVINO / CPU / MIGraphX (placeholder).**
  Fixes a real bug along the way: `VisionBackendResolver` previously mapped both "Intel" and "AMD"
  accelerator choices to DirectML, so OpenVINO — already fully implemented, including its own backend
  folder and session-factory support — was reachable only via a hand-set environment variable, never
  from the UI. `AiAccelerator` gains `TensorRt` and `OpenVino` (additive, not renames, so a node on an
  older build during a rolling upgrade can't misparse either as anything worse than a safe `Auto`
  fall-back) and a `MIGraphX` placeholder for AMD's newer-generation accelerator: it's selectable now,
  but `AccelSelection.Choose` always resolves it to "no accelerator" and the Admin UI shows a
  "future feature" notice instead of any backend work. `TensorRt` reuses the CUDA native build and
  turns TensorRT on for *any* local engine via a new `Vision__EnableTensorRt` env var
  (`VisionServiceSupervisor.SetEnableTensorRt`) — previously only D-FINE could use TensorRT at all.
  `OpenVino` forces the explicit OpenVINO backend via a new `Vision__Backend` env var
  (`VisionServiceSupervisor.SetBackendOverride`). "Intel" is dropped from the rendered dropdown (its
  old, buggy DirectML behavior is superseded by explicitly picking DirectML or OpenVINO) but the enum
  value is kept for wire/DB back-compat with an already-persisted node.
- **D-FINE FP16 via TensorRT is enabled, with automatic FP32 fallback on overflow.** The previously
  disabled `Detection.DFineTensorRtMode = FP16` option (both the global setting and its per-node
  override) now runs — the plain FP32 model under `trt_fp16_enable` with D-FINE's LayerNorm subgraphs
  kept in FP32 (`trt_layer_norm_fp32_fallback`) as a mitigation for the transformer-activation overflow
  FP16 can otherwise cause. If a camera's engine still overflows (a frame decoding to zero detections
  with non-finite raw output — previously only logged), `CameraDetectionPipeline` now detects it via
  the new `DFineEngine.HasNonFiniteOverflow` latch and rebuilds that camera's engine at FP32
  automatically in the background (never blocking the capture/detect loop) — no admin action needed.
  This supersedes the separate, still-blocked mixed-precision `*.fp16.onnx` export mechanism (no
  working conversion toolchain produces one), which remains in the code but is no longer what FP16
  means here.
- **Model-agnostic local detection: a new `Custom` `Detection.ModelFamily` runs any ONNX model dropped
  into `C:\ProgramData\LarisVMS\models`**, decoded via metadata read straight from the model (embedded
  ONNX `metadata_props`, all graph outputs) or a same-basename JSON sidecar (`yolox-s.onnx` +
  `yolox-s.json`) when that's missing or incomplete — one dispatch mechanism covers several common
  detection head formats plus D-FINE's two-output `logits`/`pred_boxes` signature, the latter routed
  through the existing `DFineDecoder` behind the same interface rather than a second bespoke decoder.
  New `LarisVMS.Vision.Models` namespace (`ModelDescriptor`, `ModelDiscovery`, `OnnxModelInfo`,
  `DecoderKind`) and `LarisVMS.Vision.Inference.Decoders`, ported and adapted from sibling project
  SideGlance's own descriptor/decoder system, now legal to share since both repos are Apache-2.0. A
  model whose metadata/sidecar can't be resolved is still listed (never hidden) with a plain-language
  warning rather than silently dropped. This first pass (`GenericOnnxEngine`) is single-frame/
  CPU-preprocessing only — no batch or Slice-mode support yet (see its own doc comment for why
  generalizing those without a real model to verify against was deferred).
- **New `Detection.LocalModelName` setting** (Camera → Node → Global chain like every other
  `Detection.*` field) selects which discovered model a `Custom`-family pipeline loads. Admin/Nodes and
  Admin/Settings/Detection both offer a free-typed field with a live "Refresh models" action (a new
  `NodeControlClient` service proxies each node's own new `GET /vision/models`, itself a signed proxy
  of Vision Service's new `GET /models`) that populates a datalist and shows any resolution warning
  inline; the global page fans this out across every online node and shows which ones actually have a
  given model, since a default isn't necessarily present everywhere.
- **Models are no longer bundled into the node package.** `C:\ProgramData\LarisVMS\models` (the same
  `%ProgramData%\LarisVMS` root the TensorRT cache already uses) is now the standard, writable,
  per-machine models directory — `build-node.ps1` no longer copies `models\*.onnx` into the build
  output, and `install-node.ps1` creates the folder (and prints a reminder when it's empty) instead.
  Drop a `.onnx` file in directly; an optional same-basename `.json` sidecar fills any gaps its own
  metadata doesn't cover.

- **Stage 4: the external HTTP inference backend can skip its JPEG codec entirely.** New
  `Detection.ExternalInferenceTransport` setting (Admin → Settings → Detection, node-scoped,
  `Auto`/`Jpeg`/`PixelsYuv420`/`PixelsBgra`): `PixelsYuv420` makes ffmpeg emit nv12 for the external
  backend (the same output `Detection.GpuPreprocessing` already produces for the built-in engine) and
  `HttpDetectionEngine` sends those bytes straight to SideGlance's `pixels_yuv420sp` raw-pixel
  `/v1/detect` form — no JPEG encode on this side, no JPEG decode on SideGlance's, at ~10x JPEG's
  payload size (so only sane on loopback/LAN). `PixelsBgra` skips the JPEG codec too without any
  ffmpeg/pipeline change at all — the capture buffer stays BGRA, just sent raw (`pixels_bgra32`) at
  ~4x JPEG's size — a middle-ground reference mode for a loopback service where bandwidth is free.
  `Auto` (the default) currently always resolves to `Jpeg`: real capability-based auto-negotiation
  (checking the probed service's own `input_modes` and picking accordingly) is deliberately not
  implemented yet, so `Auto` is honestly just today's safe default under a name that leaves room for
  that later without another settings migration. The "Test connection" model picklist now shows each
  model's advertised `input_modes`, and the settings page warns (without blocking Save) when the
  chosen transport isn't in the selected model's own advertised list. Slice mode works with either
  raw-pixel transport unchanged — SideGlance's raw-pixel endpoint accepts the same `slice=` tile plan
  as its JPEG form, so nothing about the tiling/merge story changes, only the wire bytes. New tests:
  `Detect_with_pixels_yuv420_transport_sends_the_raw_nv12_body_with_layout_query_params`,
  `Detect_with_pixels_bgra_transport_sends_the_raw_bgra_body_with_layout_query_params`,
  `Detect_default_transport_still_sends_jpeg` (`HttpDetectionEngineRequestTests.cs`). Requires
  SideGlance's own raw-pixel `/v1/detect` support (its [Unreleased] entry) — an older SideGlance
  rejects an unrecognized `layout=` query value with a 400 naming the field, a clean failure rather
  than a silent one, so picking a raw transport against an unpatched service fails loudly on the
  first frame instead of degrading quietly.

- `HttpDetectionEngine.LastInferenceMilliseconds` (the whole `Detect` round trip, unchanged in
  meaning) is now split into `LastEncodeMilliseconds` (JPEG-encode share) and
  `LastTransportMilliseconds` (POST + response read/parse/map share), both surfaced on the
  per-camera cadence log line as `Encode {ms} ms, transport {ms} ms.` when the external HTTP backend
  is active. Built to attribute the gap between SideGlance's own reported `last_run_ms` (a few
  milliseconds — see its CHANGELOG's matching per-stage timing entry) and the much larger round trip
  this line already logged: `transport` minus SideGlance's own `last_read_ms + last_decode_ms +
  last_pack_ms + last_run_ms + last_postprocess_ms` (from its `GET /v1/models`) is network + server
  queueing time neither side previously named. No behavior change — pure measurement.

### Changed

- The external HTTP inference backend's detect traffic now uses its own named `HttpClient`
  (`CameraPipelineManager.ExternalInferenceHttpClientName`), separate from the one that carries the
  Node callbacks (`POST /detections`, `/detections/crop`). Previously both shared one connection
  pool, so a 5-second-timeout detect request piling up against a slow or overloaded external
  inference service competed for connections with report traffic that has nothing to do with it.

- **The external HTTP inference backend (`Detection.Backend = "ExternalHttp"`) no longer sends
  base64-encoded JSON.** Every frame now goes out as the raw JPEG bytes, with confidence/iou/slice
  carried as query parameters instead of JSON body fields (SideGlance's own raw-bytes endpoint
  gained the ability to accept them there in the same release — see its CHANGELOG). This removes a
  `Convert.ToBase64String` allocation, a JSON serialization, and the associated intermediate buffers
  from every single detection request; the JPEG bytes SkiaSharp already produced are now the request
  body directly, with no encoding step in between. Requires SideGlance to have the query-parameter
  raw-body support (its own [Unreleased] entry) — this is a coordinated upgrade, not a
  negotiated/backward-compatible one: an older, unpatched standalone SideGlance instance would
  receive the confidence/iou/slice fields as ordinary (and, in its old code, silently ignored) query
  parameters, running every request at the model's own default thresholds and never activating slice
  mode even when the camera pipeline is configured for it — a silent behavior change, not a clean
  error, for exactly the deployment shape (external inference + Slice mode) this whole effort started
  from. Upgrade SideGlance alongside this build. New `HttpDetectionEngineRequestTests.cs` pins the
  actual request shape end-to-end (content type, query parameters, the slice value format, the
  bearer header) — no prior test exercised the real HTTP request `HttpDetectionEngine` sends at all.

- **Detection frame-rate cap (`Detection.MaxFps`) is now per-camera**, not just node-wide. Every
  camera on a node previously shared one ffmpeg `fps=` ceiling regardless of how much detection
  traffic that specific camera actually needed — a driveway camera watched only for a parked-vehicle
  alert paid the same decode/preprocess/inference cost per frame as a busy street camera tracking
  fast-moving traffic. Resolves Camera → Node → Global, the same chain `Detection.Confidence`/
  `Detection.MotionJitterPixels` already use, with a new override control on Cameras → Edit
  (0 = no cap, same convention the existing node-wide setting already had). A camera-level override
  reduces cost on both this node (ffmpeg decode/scale, preprocess) and, for the external HTTP
  backend, the far side of the network too — the only lever in this whole effort that reduces cost
  on both sides of that boundary at once. Covered by new tests
  (`NodeServiceMaxFpsTests`) pinning the Camera → Node → Global resolution and confirming a
  camera-scoped override doesn't leak onto its siblings or displace the neighbouring positional
  fields in `NodeConfigCameraDto` (a long tail of same-typed `int` parameters where a misplaced
  argument compiles fine and silently sends the wrong value to the wrong field).

### Fixed

- **The external HTTP inference backend's model picklist silently ignored the service's real input
  size, and the health probe always failed.** `ExternalModelInfo`'s `inputSize`/`classCount`/
  `batchMode` were declared as camelCase JSON properties, but SideGlance's actual serializer emits
  snake_case (`input_size`/`class_count`/`batch_mode`) — `PropertyNameCaseInsensitive` only ignores
  letter case, not the underscore, so the two spellings never matched and `InputSize` silently fell
  back to its 640 default no matter what the connected model actually reported. A model configured
  at any other input size (SideGlance's example config ships models up to 1280) had every single
  frame rejected with a 400 from the far side, indistinguishable in the logs from a permanently
  broken engine. Separately, `ExternalHealthResponse.ModelsReady`/`ModelsFailed` were typed as
  string lists against SideGlance's actual integer counts, so every `/healthz` probe threw and was
  silently swallowed (by design, as best-effort context) — the "Test connection" health line never
  rendered for a real service. Both DTOs now match SideGlance's actual wire shapes; the
  previously-dead health-count display on Admin → Settings → AI detection now renders. Covered by a
  new round-trip test that serializes with SideGlance's real naming policy before deserializing
  through these DTOs, replacing fixtures that had encoded the mismatch as expected behavior.

- **A snapshot taken while running the external (SideGlance) backend in Slice mode had no context
  margin at all** — cropped exactly to the union of reported detection boxes, tighter than every
  other combination of backend and pixel format, which used a 12% or (nv12) 30% margin depending on
  path. Unified under a new `Detection.SnapshotMarginPercent` setting (Admin → Settings → AI
  detection, default 12 = the previous non-slice default), applied identically to both the BGRA and
  nv12 crop helpers in both the plain and sliced eager-snapshot paths, regardless of backend.

## [0.200.0] - 2026-09-11

### Fixed

- **A per-node override on Admin → Nodes could be silently lost on save.** Saving a node's row from a
  stale copy of the page — a browser tab left open, or reopened via back/forward, from before an
  override was set — cleared every override field that was blank in that stale snapshot back to
  "inherit the global default," overwriting whatever had been set there since, with no warning. This
  is how a working **Detection.Backend → External HTTP service** override (with its URL/model/API
  key) could vanish and quietly fall back to the built-in model, looking like it had "reset itself."
  Saving that page now hashes every field the row can edit — name, storage/archive paths, AI
  accelerator, all Detection.\* and Archive.\* overrides, streaming/failover settings — as of when the
  page was loaded, and re-checks it against the node's actual current state before writing anything.
  If anything changed underneath in the meantime, the save is rejected outright (nothing is written)
  with a message asking you to reload and re-apply the edit, instead of clobbering it.

- **Admin → Settings → AI detection could silently fail to save at all.** Its Save button had no
  handler of its own, so after clicking "Test connection" (which does carry a handler, leaving the
  page on `?handler=TestConnection`) the next Save posted right back to that same handler — it
  re-probed the external service, showed the same "Connected…" success message Save itself never
  gets, and never actually saved. Any edit made after a test connection — not just the external
  backend fields, every field on that page — was silently discarded, so a refresh showed the old
  values (Backend back to Built-in) with no error. The Save button now posts to its own handler, so
  it always saves regardless of what was clicked before it. The same handler-less-Save bug existed
  on Cameras → Edit (there, triggered by "Re-probe" instead of "Test connection") and is fixed the
  same way.

### Note

- Web-only — no recorder node/proxy code changed. `LarisVMS.Node`/`LarisVMS.Proxy`/`LarisVMS.NodeUpdater`
  still had their base `<Version>` bumped to match, only to satisfy `deploy.ps1`'s version-sync guard.
  Schema migration `BumpVersion0_200_0` (version-tracking row only) — `dotnet ef database update` /
  redeploy applies it.

## [0.199.0] - 2026-09-09

### Changed

- **Snapshots now shows detections only.** The Snapshots browser lists AI object detections,
  camera-native object classes (person / vehicle / face / …), and custom event tags. Plain motion
  ("motion detected" with no object class) is no longer listed there. Motion is unaffected
  everywhere else — the Playback timeline still draws its motion bands and live view still shows the
  motion badge exactly as before, and no motion history is deleted.
- The **Admin → Settings → Events → "Snapshots browser"** section loses its **Motion** checkbox. The
  per-class checkboxes (person, vehicle, face, …) still hide those specific detections from the
  browser.

### Fixed

- An **AI object detection** could be missing from Snapshots if the old "Snapshots browser → Motion"
  checkbox had been unticked — AI detections were incorrectly gated by that motion toggle.

### Note

- Schema migrations `RetireSnapshotMotionVisibilitySetting` (removes the retired
  `Snapshots.Enabled.Motion` setting row — no table changes, no motion data touched) and
  `BumpVersion0_199_0` (version-tracking row only). `dotnet ef database update` / redeploy applies
  them.
- Also documented (README): if live video stops streaming in a background Chrome window, launch
  Chrome with `--disable-backgrounding-occluded-windows --disable-background-timer-throttling`.

## [0.198.0] - 2026-09-09

### Fixed

- **Live view spent its time catching up, pausing, then catching up again** — the "1.5×" badge
  flickered on and off and playback froze for a beat every few seconds, while recorded playback of
  the same cameras stayed smooth. Two causes, both fixed:
  - The recorder's live stream emitted one fMP4 fragment per camera keyframe, so a camera with a
    2–4 s keyframe interval delivered video to the browser in 2–4 s bursts. The browser's buffered
    end lurched forward in those same steps while playback advanced smoothly, so the "how far behind
    live am I" measurement sawtoothed across the catch-up threshold on every keyframe. The live
    stream (only — recorded segment files are unchanged) is now muxed to flush a fragment about
    every 500 ms, so it arrives in a smooth trickle.
  - The client chased the live edge with almost no buffer, correcting with a blunt fixed 1.5× speed
    boost on a 3 s timer — which overshot to the edge, ran out of buffered video, stalled, and fell
    behind again. It now positions playback ~2 s behind the live edge on connect (a jitter buffer)
    and trims speed gently to hold it there (never above 1.25×, occasionally easing below 1.0× to
    rebuild the buffer) on a 1 s timer. Live view now sits ~2–4 s behind real time and plays
    continuously; the "catching up" badge appears only during a real sustained catch-up, not once
    per keyframe. The drift check no longer runs until that initial positioning seek has happened —
    otherwise a freshly opened tile (sitting at time 0 while the stream is minutes in) read as
    "hundreds of seconds behind" and tore itself down and reconnected in a tight loop.
- **A brief network stall on one live tile could corrupt its stream and force a jarring
  reconnect.** When a viewer's connection couldn't keep up, the node used to drop the oldest queued
  video fragment, punching an unrecoverable hole into the middle of that browser's stream (which
  surfaced only as an opaque decode error). The node now closes that one viewer's socket cleanly
  instead, and the client reconnects fresh — recording and every other viewer are unaffected, as
  before.

### Note

- Recorder nodes auto-update to this build (Vision Service / node binaries only); no
  `install-node.ps1` re-run and no settings changes. The live-stream fragment-cadence change takes
  effect on a camera the next time its recording session restarts (within ~5 minutes of the node
  updating, or immediately on a manual restart). The client-side changes apply on the next browser
  reload of the web app.
- Schema migration `BumpVersion0_198_0` (version-tracking row only, no table changes) —
  `dotnet ef database update` / redeploy applies it.

## [0.197.0] - 2026-09-09

### Fixed

- **A fast-moving object got no live AI-detection box unless the "Idle" overlay toggle was also
  on** — on Views → Play with only "Moving" enabled, a car crossing frame stayed boxless even
  though it still produced a snapshot (proof the pipeline had classified it Moving at least once).
  Slow movers were fine. The detection pipeline pruned every track's centroid history once per
  frame against only the tracks that matched a detection *that* frame — but ByteTrack briefly
  coasts a track (Kalman-predicted, same id) through a missed detection frame so its second
  association pass can re-acquire it. A moving object is exactly what drops the odd detection frame
  (motion blur, a fraction-of-a-second occlusion, lower frame-to-frame overlap), so its history was
  wiped on every gap and `MovementClassifier` re-reported it **Idle** — no live box — until it
  rebuilt a two-second window of samples, which a fast mover rarely stays on screen long enough to
  do. Per-track state (movement history, the stable-label arbiter, the snapshot-dedup set) is now
  pruned against every track ByteTrack still considers alive, including the ones it is coasting for
  re-acquisition. A moving object now keeps its live box for the whole time it is in frame; parked
  objects still appear only under "Idle".
- As a smaller consequence of the same change, a briefly-occluded track no longer re-runs label
  arbitration from scratch or fires a second eager snapshot when it is re-acquired.

### Note

- Recorder nodes auto-update to this build (Vision Service / node binaries only); no
  `install-node.ps1` re-run and no settings changes. The fix takes effect on a camera the next time
  its detection watch starts (within ~5 minutes of the node updating).
- Schema migration `BumpVersion0_197_0` (version-tracking row only, no table changes) —
  `dotnet ef database update` / redeploy applies it.

## [0.196.0] - 2026-09-08

### Fixed

- **The storage watermark backstop deleted archive-enabled footage even when the archive volume was
  healthy and had terabytes free.** 0.195.0 made the watermark pass (the "primary disk is nearly
  full regardless of settings" backstop) *always* delete rather than archive — so a primary volume
  that crossed the watermark before its footage aged out (a long primary retention window, or a
  volume sized on the assumption that the archive absorbs the overflow) permanently lost
  still-in-policy footage from archive-enabled cameras, and the log line blamed an archive volume
  that was actually fine (the pass never tried to write to it). Separately, a single transient
  SMB/USB blip gave up immediately at three points — the once-per-sweep archive read/write probe,
  the archive free-space check, and each per-file copy — so one dropped share session sent a whole
  sweep's aged-out footage to deletion instead of the archive. Now:
  - the archive reachability probe is retried over ~6.5s before the volume is declared unreachable,
    the free-space check re-measures a few times before concluding "full", and each archive copy is
    retried on a 300 ms / 1 s / 3 s backoff (the existing `StorageRetry` schedule) before a segment
    is given up on;
  - the watermark pass again **moves** an archive-enabled camera's oldest footage to the archive
    volume while the primary still has headroom — the primary copy is reclaimed when the relocation
    report lands, typically the next sweep;
  - it falls back to deleting only when the primary volume is within 7% of full, the archive volume
    is genuinely unreachable or full, or the copy keeps failing — preserving 0.195.0's guarantee
    that the node keeps recording no matter what;
  - the warning/error log lines now name which of those happened, and an "archived, primary space
    frees next sweep" outcome is logged at information level rather than as an error.

### Note

- Recorder nodes auto-update to this build (Node binaries only); no `install-node.ps1` re-run and
  no settings changes. A node that had been deleting archive-enabled footage under disk pressure
  starts moving it to the archive volume on its next storage sweep (within ~5 minutes).
- This stops the footage loss on transient blips, but if a primary volume is genuinely filling
  faster than primary retention ages footage out you will still want to shorten `Retention.Days`
  (so footage moves to the archive sooner) or grow the primary volume — the new information-level
  "moved N to archive" / "still over the watermark" log lines make that visible.
- Schema migration `BumpVersion0_196_0` (version-tracking row only, no table changes) —
  `dotnet ef database update` / redeploy applies it.

## [0.195.0] - 2026-09-08

### Changed

- **Detection-box jitter rejection is now opt-in, per camera, and tunable.** The wobble-rejection
  half of **Admin → Settings → Detection → Snapshot motion accuracy** (added in 0.188.0) was
  holding parked vehicles idle by over-smoothing the movement check — but on a noisier model
  (switching YOLOX from L to M) it did the opposite, reporting a parked truck and car as moving
  all night, worse than before any jitter fix existed. It is now its own setting,
  **`Detection.RejectMotionJitter`**, **off by default** (restoring the proven pre-0.188 movement
  classification: a single oldest-vs-newest box-centre comparison), resolvable globally *and per
  camera* on a camera's own Edit page — turn it on only for the cameras that actually have a
  parked vehicle in view. Its pixel sensitivity is exposed as **`Detection.MotionJitterPixels`**
  (1–15, default 3), also global + per-camera.
- **`Detection.SnapshotMotionAccuracy`** now governs only the other half — finalizing a
  detection's snapshot promptly once its object leaves frame — and its grace window, previously a
  hard-coded 5 seconds, is now the **`Detection.DepartureGraceSeconds`** setting (1–10, default 5).
- Changing any of these four restarts each affected camera's detection pipeline, same as the
  existing detection settings.

### Fixed

- **AI-detection reporting logged a constant stream of `MotionSpans` duplicate-key errors.** A
  single motion-span batch from a node routinely carries two reports for one still-open detection —
  Vision's 15-second checkpoint and that span's close-report, both with the same backdated
  `StartUtc`. `NodeService.RecordMotionSpansAsync` decided new-vs-extend with database queries only,
  which can't see a row added earlier in the same batch but not yet saved, so it inserted two rows
  with the same `(CameraId, DetectedObjectLabel, StartUtc)` and `SaveChanges` failed the unique
  index `IX_MotionSpans_CameraId_DetectedObjectLabel_StartUtc` (SQL error 2601). A retry loop
  recovered the data, but EF Core logged the violation at Error level on every occurrence — many
  per minute on a busy camera. The upsert now also checks the spans added so far in the current
  batch (exact-identity and coalesce alike) and folds the second report onto the pending row.
- **A recorder node's primary drive could fill to 100% when its archive volume was unreachable.**
  The watermark backstop (the "disk is nearly full regardless of settings" pass) tried to *archive*
  the oldest footage rather than delete it — but a file moved to the archive volume stays on the
  primary drive until its relocation is confirmed by the server at the end of the sweep, so it freed
  no space when the drive was actually out of room, and froze entirely when the archive volume was
  down or rejecting writes. The watermark pass now always **deletes** to free real space and keep the
  node recording, logs a warning naming how much was deleted (and how much belonged to archive-enabled
  cameras and is therefore gone for good), and logs an error if the drive is still over the watermark
  afterwards. The per-sweep "archive volume unreachable" warning now says plainly that aged-out
  footage will be deleted, not preserved, until the archive volume is restored.
  *(0.196.0 revisits this: the watermark pass moves footage to a healthy archive volume again, with
  deletion as the fallback when the primary is critically full or the archive is unusable.)*

### Note

- Recorder nodes auto-update to this build (Node + Vision Service binaries); no `install-node.ps1`
  re-run. After upgrade, box-jitter rejection is **off** for every camera — if you had turned
  "Snapshot motion accuracy" on for it, re-enable it as **Reject stationary-object jitter** only on
  the cameras that need it (**Admin → Settings → Detection**, or per camera on the camera's Edit
  page). The prompt snapshot-finalization half of the old toggle is unchanged and still on by
  default.
- Schema migration `BumpVersion0_195_0` (version-tracking row only, no table changes) —
  `dotnet ef database update` / redeploy applies it.

## [0.194.0] - 2026-09-08

### Added

- **Recording failover.** Assign a recorder node a **backup node** on **Admin → Nodes**. If the node
  goes down, its cameras move to the backup for recording (and live view) until it returns — footage
  the backup records during the outage stays on the backup and plays back transparently, no
  migration. Two nodes can back each other up.
  - The decision is a **quorum**, not one opinion: the backup node, this server, and any media proxy
    assigned to the node each actively probe its `/health`, and a **majority of the voters that
    responded** must agree the service is down before anything moves (never fewer than two). A
    server↔node disagreement across a flaky WAN link changes nothing while the backup, on the same
    LAN, still sees the node. Verdicts are held across a sustain window to damp flapping.
  - **Maintenance mode** — a per-node button (behind a confirm dialog) that fails a node's cameras
    over to its backup immediately and holds them there until you turn it off, even if the node's own
    health stays green the whole time. If the node has no backup, the dialog warns that recording
    will stop.
  - **Recording only** — a per-node switch that turns AI object detection off entirely for that node
    (its own cameras and any it adopts during a failover). It keeps recording continuously and
    tagging ONVIF events — the pre-AI behaviour — so a low-power backup node that suddenly picks up
    many cameras keeps recording rather than collapsing under inference load.
  - A **hardware-mismatch warning** on the Nodes page when a backup node has different acceleration
    hardware (or is itself in recording-only mode), and a new **Node failover activated** alert
    condition on **Admin → Alerts**.
- Recorder nodes expose an unauthenticated `GET /health` on their LAN port (version, recording
  camera count, uptime) for the failover quorum probes.

### Note

- Recorder nodes auto-update to this build. Failover needs a backup node assigned on **Admin →
  Nodes** — nothing changes for a node without one. The tuning defaults (90s to fail over, 120s to
  fail back) are conservative; a failover briefly interrupts live view for the affected cameras
  while the stream reconnects through this server.

## [0.193.0] - 2026-09-07

### Added

- **Media proxies auto-update**, exactly like recorder nodes. `deploy.ps1 -BuildProxy` registers
  each new proxy build on **Admin → Node Builds** (platform `proxy-win-x64`); once an admin approves
  it, every proxy whose reported version is older downloads it, verifies its SHA-256, and swaps its
  own binary on its next check-in — no more re-running `install-proxy.ps1` on each relay machine.
  The proxy package now bundles `LarisVMS.NodeUpdater.exe` (the same detached swap helper the node
  uses) for this. Controlled by the same **Auto-update recorder nodes and media proxies** setting on
  **Admin → Settings → Nodes**; an update briefly drops in-flight live/playback connections while
  the service restarts, and browsers reconnect through the fallback chain in the meantime.
- **Admin → Media proxies** has a 📋 copy button next to the `install-proxy.ps1` command.

### Fixed

- Turning on archive storage for a node with a large backlog of existing footage could stall: the
  first sweep tried to report every archived (or deleted) segment to the server in one request and
  the web host rejected it with **413 Payload Too Large**, so nothing drained. Those reports now go
  out in batches, and each batch that lands has its work committed before the next is sent.

## [0.192.0] - 2026-09-07

### Added

- **Media proxy tier.** A media proxy is a standalone relay (`LarisVMS.Proxy`) that sits between
  browsers and recorder nodes for live view and playback — a dumb TLS-terminating pass-through. It
  holds no camera credentials and no per-node secret. Use it when a proxy machine can hold a real
  certificate but the recorder nodes can't, when browsers can reach a proxy but not the nodes
  directly, or just to keep media traffic off this server. Build it with `build-proxy.ps1` (or
  `deploy.ps1 -BuildProxy`) and install it with `install-proxy.ps1`, which reuses the same
  registration key a recorder node uses.
- **Admin → Media proxies** — a proxy shows up here after its first check-in; set its routable host,
  its certificate (a `.pfx` path/password, or self-signed for testing), and enable it. This server
  health-checks every proxy every ~15 seconds.
- On **Admin → Nodes**, each node can be assigned a **primary** and a **backup** media proxy.
  Browsers are then routed through the first healthy proxy, falling back to direct-to-node (if that
  node has a client endpoint) and finally to this server. A proxy this server can't health-check is
  never handed to a client, so assigning one can't break a working stream.
- Recorder-node live and playback log lines now name the camera, not just its GUID.

## [0.191.0] - 2026-09-06

### Added

- **Direct-to-node streaming.** Live view and playback can now hand the browser a connection
  straight to the recorder node over HTTPS instead of relaying every byte through this server,
  taking the central hop out of the media path. It is **off by default**: turn it on globally at
  **Admin → Settings → Live View → Direct streaming**, or per node on **Admin → Nodes**. A camera
  whose node has not reported a healthy client endpoint automatically stays on the proxy, so
  enabling the toggle can't break a stream that isn't ready.
- Recorder nodes gained an optional second HTTPS listener for that direct traffic.
  `install-node.ps1` has new switches — `-ClientPort`, `-ClientPfxPath`, `-ClientPfxPassword`,
  `-ClientAllowInsecure` — and the certificate path/password can also be managed centrally on
  **Admin → Nodes**. A supplied `.pfx` is hot-reloaded when the file changes, with no node restart.
- For initial setup and testing only, **Allow insecure client endpoint** lets a node come up on an
  auto-generated, self-signed certificate. Viewers must click through a one-time browser warning
  (the live view offers a "trust the recorder" link), and every affected stream is flagged
  insecure in the UI. Use a real certificate in production.
- **Admin → Nodes** shows a streaming-relay counter (live streams still relayed, playback segments
  proxied, bytes) so the proxy-vs-direct change can be measured — the numbers trend toward zero as
  cameras move to direct. The live player also reports a time-to-first-frame measurement.
- Recorder nodes log each viewer that attaches to a camera's live stream — camera, client address,
  whether the connection came straight from a browser (direct) or via this server (proxy), and how
  long it lasted — plus direct playback fetches. The Content Security Policy is widened at runtime
  with each node's own client-endpoint origin so a direct `wss://`/`https://` connection isn't
  blocked (and stays exactly as before when no node has a client endpoint).

### Fixed

- In a fullscreen camera tile, scrolling the mouse wheel over the timeline strip zoomed the video
  image as well as the timeline. The timeline now consumes those wheel events so only it zooms.

### Note

- Enabling a node's direct client endpoint needs `install-node.ps1` re-run on that node (with
  `-ClientPort …`). While direct streaming is on, that node's live/playback briefly drops during
  each auto-update as the second listener restarts — the same short interruption an update already
  causes for recording.

## [0.190.0] - 2026-09-06

### Added

- Recorder-node check-ins are now replay-hardened. Every heartbeat carries a single-use rolling
  nonce issued in the previous reply; a replayed heartbeat is rejected and recorded in the audit
  log as `Node.AuthReplay`. A node whose nonce gets out of sync — a heartbeat reply it never
  received — keeps resending the stale value and locks itself out; the new **Reset auth** button on
  **Admin → Nodes** clears it so the node re-syncs on its next check-in. No effect on a healthy node.
- Bearer-secret rotation for recorder nodes. It is wired end to end but dormant by default: set a
  node's `SecretRotationDays` above `0` (in the database) to have the server hand that node a fresh
  secret once the current one reaches that age. The server keeps accepting the previous secret until
  the node first authenticates with the new one, so a rotation causes no downtime, and a new secret
  is only ever issued on a heartbeat that already passed the nonce check.
- One-shot media tokens (playback segment, hover thumbnail, AI-detection snapshot crop, node service
  restart) now carry a per-request id and are refused on a second use within their 30–60 second
  lifetime. Live view is unaffected — its token is deliberately reused across the video and overlay
  sockets. Tokens issued by this build to a not-yet-updated node are accepted without the check, and
  playback or thumbnails from a node may briefly return an authorization error during the few
  seconds it takes that node to auto-update to this build.

## [0.189.0] - 2026-09-06

### Fixed

- Moving people and animals are reliably tagged as "moving" again. The 0.188.0 "Snapshot motion
  accuracy" check added a directedness test that a rigid vehicle box passed but a person's or
  animal's naturally unsteady detection box often failed, so real movers were logged as idle and
  produced no snapshot or timeline event. The movement check now keys only on how far the object's
  centre travels across the two-second window; a parked vehicle is still held idle by the same
  displacement floor. The **Admin → Settings → Detection → "Snapshot motion accuracy"** toggle is
  unchanged.
- The registration key on **Admin → Settings → Nodes** shows its value again — it had been rendered
  as a blank field (nothing revealed by the eye toggle, and no masking dots either) since the key
  was first masked in 0.171.0.

### Changed

- Hover thumbnails and AI-detection snapshot images are now stored as WebP, which is smaller and
  loads faster. Images already cached as JPEG keep being served and are replaced with WebP the next
  time each one is regenerated — there is no bulk re-encode. A recorder node whose bundled ffmpeg
  has no WebP encoder automatically keeps producing JPEG.
- The per-volume "days of retention left" estimate on **Admin → Nodes** now also shows for a node's
  archive volume, and both the primary and archive figures say "runway not yet estimated" while
  there is too little recent activity to project one, instead of showing nothing.

## [0.188.0] - 2026-09-06

### Changed

- **Storage configuration is now per recorder node.** Each node has its own recording path and its
  own optional archive path, set when the node is installed (`install-node.ps1 -StorageRoot … [-ArchiveRoot …]`)
  or afterwards on **Admin → Nodes**. The old global "storage root" setting is retired — on upgrade,
  any node that was inheriting it keeps that path automatically. A node left with no storage path
  records nothing (and is flagged on Admin → Nodes) until one is set. The first-run Setup wizard's
  Storage step is now informational only.

### Added

- **Archive storage tier.** A recorder node can be pointed at a secondary volume — an SMB share or a
  USB drive — for older footage. When primary retention would delete a segment and archiving is
  enabled, the node **moves** the file to that node's archive volume instead of deleting it, and
  keeps it there until a separate archive retention (counted from the recording date) is reached.
  Playback, thumbnails, snapshots and export all work transparently from either volume; the timeline
  says "Loading from archive…" for a cold-storage segment. If a node's primary volume passes the
  storage watermark, its oldest footage is archived **early** rather than deleted, so nothing is lost
  while there's another place to put it — with a warning shown next to that node. Enable it and set
  the retention on **Admin → Settings → Storage and retention** (overridable per node and per
  camera); set each node's archive volume path on **Admin → Nodes** or at install time
  (`install-node.ps1 -ArchiveRoot …`). A segment's cached hover thumbnails and AI-detection snapshot
  crops are moved to the archive volume alongside the video, so archived footage keeps its fast
  previews and they expire together with it. If you repoint a node's storage to a new volume and
  demote the old one to the archive volume, the node notices footage that's now on the archive
  volume and re-labels those recordings as archived on its own.
- Playback and thumbnails keep working for footage recorded while a node's storage path was
  temporarily pointed somewhere else and then changed back — a recording is served from wherever its
  file actually is, not only from the currently-configured storage/archive roots.
- **Node build numbers.** Every recorder-node package now carries a monotonically increasing build
  number as a 4th version component (e.g. `0.188.0.244`), so rebuilding for a test deploy without
  changing the version still registers as a newer build that nodes auto-update to.
- A snapshot badge now shows a count (e.g. "Human ×2", "Vehicle ×3") when several objects of the
  same type were moving in it — previously two people walking past together, or a car following a
  truck, showed only a single "Human" or "Vehicle" badge.
- New **Admin → Settings → Detection → Snapshot motion accuracy** toggle (on by default) governing
  the two detection-quality fixes below; turn it off to compare against the previous behaviour.

### Fixed

- The Snapshots filter tree no longer shows a redundant "Person" entry nested under "Human" — "Human"
  is now a single checkbox, matching how the label reads elsewhere in the app.
- A stationary vehicle parked in a camera's view no longer intermittently registers as "moving" and
  produces stray snapshots. The movement check now ignores the small frame-to-frame wobble of a
  distant detection box and only reacts to sustained, directed travel.
- A moving object that leaves the scene now finalizes its own snapshot within a few seconds instead
  of being held open long enough for a later, unrelated object of the same type on the same camera
  to be merged into it — so two vehicles passing 30 seconds apart produce two separate snapshots,
  each cropped to its own vehicle, rather than one combined snapshot missing the first.

## [0.187.2] - 2026-09-05

### Fixed

- Dragging the recording timeline no longer flashes the video black between positions. The last
  decoded frame now stays on screen until the frame at the new position loads, so the picture
  follows the scrubber instead of strobing. Black still appears where there is genuinely no
  recording for that moment.

### Changed

- Database queries that load a camera together with its groups and streams now run as split
  queries, avoiding a slow combined join on cameras that belong to many groups.

## [0.187.1] - 2026-09-05

### Fixed

- **A snapshot with several tagged objects now crops around all of them, not just one.** The
  eager crop always covers the union of every moving object in the frame — the previous
  fallback that discarded the union and framed only the single highest-confidence object when
  they were spread across the scene is gone. The frame that becomes the snapshot is now chosen
  by how well it captures the whole moving set: the frame with the most moving objects wins,
  and ties break on their combined confidence and size, so a later frame showing one object
  up close no longer replaces an earlier one that framed everything. Per-object peak
  confidence is still reported as before.

### Changed

- AI detections of people are now labelled just **"Human"** instead of "Human — person" on
  timeline cards, snapshot cards and the live-view box overlay. Categories with a specific
  class that adds detail ("Vehicle — truck", "Object — backpack") are unchanged.

## [0.187.0] - 2026-09-05

### Added

- **The D-FINE detection engine can now run on TensorRT at FP32**, selected from Admin > Settings >
  Detection with the new "D-FINE TensorRT" control (`Off` / `FP32`) and a per-node override on
  Admin > Nodes. `Off` (the default) keeps D-FINE on plain CUDA. `FP32` runs it through TensorRT for
  graph fusion and kernel selection with no FP16 math — a safe speedup, no precision risk. This is
  the first time D-FINE can use TensorRT at all: it has ignored it by default since 0.182.0 because
  a straight FP16 cast overflows its transformer decoder.
- An `FP16` option is present in the control but **disabled and not selectable yet**. Making D-FINE
  FP16-safe needs a mixed-precision model (backbone FP16, decoder FP32) that no available conversion
  tool produces correctly. The setting, per-node override, model-filename resolution
  (`*.fp16.onnx`), engine cache key and the `trt_layer_norm_fp32_fallback` provider option are all
  wired, so FP16 activates automatically for a node once such a model is bundled in its package —
  until then a node set to FP16 transparently runs FP32 and logs a warning.

### Changed

- **The D-FINE detection model is now labelled "experimental" in Admin > Settings > Detection and
  the per-node override.** It runs, but results can be unpredictable, and an object that straddles
  two tiles under the Slice aspect-fitting option is currently missed or duplicated. YOLOX remains
  the recommended default.
- The node-scoped `Detection.DFineTensorRtMode` setting supersedes the machine-local
  `Vision:DFineTensorRtMode` environment variable / appsettings key, which is now only a fallback
  for a node that has not yet received the pushed value. The machine-local `Vision:EnableTensorRt`
  master switch and TensorRT SDK paths are unchanged — they stay per-machine.
- When a D-FINE frame decodes to zero detections on non-finite model output, the vision-log warning
  now points at `Detection.DFineTensorRtMode` in the web UI rather than the old node-local env var.

### Notes

- D-FINE FP32 TensorRT needs the node to have `Vision:EnableTensorRt` set and the TensorRT 10.x SDK
  installed — the setting does nothing on a node without them.
- The first start after enabling `FP32` for a node recompiles the TensorRT engine — a multi-minute
  cold build per distinct camera resolution, serialized across cameras, same as the YOLOX/Slice
  first-start already documented in 0.186.2. Subsequent starts load from cache.
- Recorder nodes pick this up via auto-update (Node + Vision Service binaries); no
  `install-node.ps1` re-run.

## [0.186.3] - 2026-09-05

### Changed

- **The vision log now identifies a camera by its name rather than its GUID.** Every line was
  prefixed `Vision[2d970617-cb4f-4866-9ffc-7f3d773def04]` — 36 characters of identifier on lines
  meant to be read a column at a time — and the detection cadence, slice layout and engine build
  messages repeated it in the body. They now read `Vision[Driveway]`. The full camera id is still
  logged once when a pipeline starts, and on every failure, so a line can always be tied back to a
  camera row. A camera with no name falls back to the first block of its id rather than an empty
  label. Renaming a camera deliberately does *not* restart its detection pipeline — that would mean
  a multi-minute TensorRT engine rebuild for a cosmetic change — so the new name reaches the log on
  that camera's next restart for some other reason.

## [0.186.2] - 2026-09-05

### Fixed

- **On a node with TensorRT enabled, only the first camera switched to the Slice aspect-fitting
  mode worked; every other one produced no detections at all and failed on every single frame**
  with `TensorRT EP failed to call setInputShape() for input 'nv12'`. ONNX Runtime keys its
  compiled-engine cache on a hash built from the model's file name and the *names* of the graph's
  inputs and nodes — never their shapes, and never initializer values. The slicing preprocessing
  head is built in memory (so there is no file name) and used one fixed set of node names for every
  camera, while everything that actually differs between two cameras of different resolutions — the
  frame input's shape and the per-slice cut coordinates — was invisible to that hash. Two such
  cameras therefore collided on one cached engine file: the second camera loaded the first camera's
  engine in under a second and then could not bind its own differently sized frames. Each graph
  variant is now named for the exact capture size and slice count it was built for, and is given its
  own explicit engine cache prefix, so every camera gets the engine it actually needs. The same
  latent collision existed between two non-sliced GPU-preprocessing models of different input sizes
  and is fixed the same way.
- A camera's engine build logged "the TensorRT engine cache is warm, so this is a load rather than
  a compile" whenever the cache directory held any engine at all, which from the second camera
  onward was always true. A three-minute cold compile was being reported as a warm load. It now
  probes for an engine belonging to that camera's own graph variant.
- An inference failure was logged in full, with its stack trace, on every affected frame — ten times
  a second per camera for a persistent fault, which buried every other line in the vision log. Only
  the first is logged in full now; the rest are counted on the detection cadence line.

### Added

- The vision log now records a Slice camera's resolved geometry when its pipeline starts: capture
  buffer size, how many slices, where each one begins, and how much neighbouring slices overlap —
  which is the widest an object can be and still be seen whole by a single slice.
- The detection cadence line now reports, for a Slice camera, how many raw detections each
  individual slice produced, how many were merged as ordinary duplicates versus merged across a
  slice seam, and how many reported boxes ended up too wide to fit in any one slice. Together these
  separate "the model never saw the object" from "both halves were seen but never reunited", which
  previously looked identical from the outside.

### Notes

- Because each camera resolution now correctly gets its own TensorRT engine, the first start after
  updating compiles one engine per distinct resolution rather than one in total. Engine builds are
  serialized across cameras, so allow several minutes per distinct resolution on that first start;
  subsequent starts load from cache in under a second each. Clearing
  `%ProgramData%\LarisVMS\trt-cache` once after updating is recommended — it holds engines saved
  under the old ambiguous naming.

## [0.186.1] - 2026-09-05

### Fixed

- Clicking Play on Playback before a camera's stream finished loading no longer left it
  silently paused. The still-loading tile's own eventual startup checked the autoplay
  flag captured back when that load began — always false for a fresh page load —
  instead of the Play/Pause button's current state, so nothing actually started until
  the user clicked Pause then Play again to notice. It now re-checks live state and
  starts the stream itself once it finishes loading.

## [0.186.0] - 2026-09-05

### Added

- **A new "Slice" aspect-fitting option for AI detection** (Admin > Settings > Detection, with a
  per-node override on Admin > Nodes), alongside the existing Letterbox and Stretch. Instead of
  padding a wide or tall camera into the detector's square input with black bars — and downscaling
  the whole frame 2-3x in the process — Slice scales the camera's short edge to the model's own
  input size and cuts the long edge into 2 or more overlapping squares, each run through the
  detector at full resolution and merged back into one result. Meant to catch small or distant
  objects a letterboxed camera loses to the padding and downscale.
- The D-FINE and YOLOX detection engines can now be built directly from an accelerator-side
  preprocessing head that also performs this slicing, entirely on the GPU (nv12 decode → colour
  convert → normalize → cut into slices → batch, all inside the ONNX graph) — no CPU round trip to
  cut or reassemble frames.

### Changed

- Selecting Slice forces GPU frame preprocessing on for that camera, regardless of the
  Detection.GpuPreprocessing setting's own value — there is no CPU fallback for frame slicing, so
  a node without a usable GPU cannot run Slice mode (it can still run Letterbox/Stretch).

### Notes

- Slicing multiplies inference work by the slice count — roughly double for a typical 16:9
  camera. Watch the vision log's cadence line for drop%, and lower Detection.MaxFps if it climbs.
- Nodes pick this up via auto-update (Node + Vision Service binaries); no `install-node.ps1`
  re-run.

## [0.185.0] - 2026-09-05

### Changed

- **Internal: the D-FINE and YOLOX detection engines can now run a real batched inference pass —
  one ONNX Runtime call over multiple images at once instead of one call per image.** Lays the
  groundwork for the planned frame-slicing feature; nothing turns batching on yet, so every
  camera still runs exactly one image per pass and there is no behavior or performance change on
  this release. YOLOX's pinned ONNX export is hardcoded to a batch of 1; it's rewritten in memory
  at load time to accept a batch dimension only when one is actually requested (verified to
  produce output bit-identical to running the same images one at a time). D-FINE's own export was
  already batch-capable and needed no such rewrite.

## [0.184.0] - 2026-09-05

### Changed

- **The AI-detection snapshot for a moving object now keeps upgrading toward the clearest view
  seen so far, instead of freezing on its first sighting.** The eager Sub-stream crop (pass G)
  used to fire once per track and stay final for the rest of that span. A later frame now
  replaces the staged crop whenever its confidence-weighted box score (the same score already
  used to rank the fallback best-frame candidate) clearly beats whichever one is currently
  kept, so a distant or blurry first sighting gets replaced once the object passes closer or
  comes into better light — right up until it stops moving or leaves the scene, at which point
  the span closes and the next one starts its own fresh contest. No setting to configure.

## [0.183.0] - 2026-09-05

### Removed

- **High-resolution re-detection (`Detection.EnableHighResReDetection`) and high-resolution
  snapshots (`Detection.HiResSnapshots`) — neither worked reliably, and the former was the
  largest CPU cost measured on a busy node.** High-res re-detection re-decoded a full
  Main-stream keyframe from the node's ring buffer per trigger, tiled it, and ran a
  loop-based (not genuinely batched) inference pass — confirmed live as the cause of a node
  running at ~180% Vision Service CPU, dropping to ~20-25% with the feature off. Both
  settings, their Admin > Settings > Detection controls, the node's Main-stream ring buffer,
  and the `/internal/main-frame` loopback route are removed.
- **`Detection.EnableVisionDebugImages` (the "Save vision debug images" checkbox and "Purge
  debug images" button) removed with them** — its only purpose was diagnosing high-res
  re-detection's snapshot alignment, so it had nothing left to gate.

### Notes

- The per-track eager Sub-stream snapshot crop (pass G) is unaffected and remains the source
  of every AI-detection snapshot.
- Nodes pick this up via auto-update (Node + Vision Service binaries); no `install-node.ps1`
  re-run.

## [0.182.0] - 2026-09-04

### Fixed

- **A camera using the D-FINE detection engine on a node with TensorRT enabled produced no
  detections and no bounding boxes — with nothing in any log.** D-FINE is a DETR/transformer;
  its LayerNorm and attention activations overflow FP16's range under the TensorRT builder, so
  `logits`/`pred_boxes` came back `NaN`/`Inf` and the decoder dropped every candidate as a
  degenerate box. TensorRT was validated against YOLOX (a CNN), not D-FINE. D-FINE now ignores
  TensorRT and runs on plain CUDA — its validated path — by default. A new node-local
  `Vision:DFineTensorRtMode` setting (`Off` default, `Fp32`, `Fp16`) can opt D-FINE back onto
  TensorRT. YOLOX and its TensorRT path are unchanged.
- **D-FINE inference that decodes to zero detections now checks its raw output for non-finite
  values and writes a warning to the vision log** naming the cause and the fix, instead of
  failing silently. The check only runs on the zero-result path, so a healthy pipeline is
  unaffected.
- A vision-log line reported the TensorRT engine cache as "cold" (a multi-minute compile) on
  every start when `Vision:TensorRtEngineCachePath` was left at its default, even with the
  default directory fully populated. It now probes the resolved path.

### Changed

- **The Cameras page row actions — Zones, Event tags, Schedule, Re-probe — are now
  emoji-only buttons** (🚧 🏷️ 🗓️ 🔍). Each keeps its text label as a hover tooltip and an
  accessible name. The list filter box now matches these cells on the glyph rather than on
  every row containing the words "zones"/"schedule"/etc.

### Notes

- Recorder nodes pick up the D-FINE fix via auto-update (it is in the Vision Service binary);
  no `install-node.ps1` re-run.

## [0.181.0] - 2026-09-04

### Added

- **Snapshot badges now show each AI detection's confidence**, in muted text after the label —
  `🚶 Human — person 81%`. Grouped cards (several overlapping detections in one frame) show each
  label's own highest-scoring confidence. Only AI detections carry a score; motion, camera-classified
  and custom-tag badges are unchanged.

### Fixed

- **Snapshot badges and card borders now follow the Events settings colors.** An AI-detected object
  (Human/Vehicle/Animal/Object) was colored by an internal, auto-assigned hex that never appeared in
  the Events tab, instead of the color an admin chose there — so a camera-classified detection and an
  AI-detected one for the same kind of object showed up in two different colors, on both the
  Snapshots grid and the live-view box overlay. Both now resolve through the same Events palette.

### Notes

- Web-only change. No recorder node rebuild or `install-node.ps1` re-run.

## [0.180.0] - 2026-09-03

### Added

- **Live view can now label each AI detection box with its confidence.** A new Confidence switch in
  the view toolbar appends each box's score as a percentage to its label (`car — vehicle 81%`). It
  stays greyed out while both Moving and Idle are off, since there are no boxes for it to annotate,
  and its own setting is remembered independently — turning the boxes back on restores your
  confidence choice with them.

### Changed

- **The live-view Moving and Idle detection controls are now switches** rather than checkboxes,
  matching the new Confidence control beside them. All three are off by default and saved to your
  account, so they survive a refresh and follow you to another browser or device.

### Notes

- Web-only change. No recorder node rebuild or `install-node.ps1` re-run.

## [0.179.1] - 2026-09-03

### Fixed

- Enabling TensorRT (`Vision:EnableTensorRt`) stopped recording and live view on the node. The
  detection engine was built inside the Vision Service's `/start` request handler, so with TensorRT
  the first cold build — minutes, saturating every core — ran once per camera concurrently. That
  starved the node's recording pipeline: each camera's recorder is a single ffmpeg writing a `tee`
  to both the segment files and the live pipe, so when the pipe drain stopped being scheduled the
  muxer blocked and both legs stopped, silently, until the builds finished. Engines are now built off
  the request path and one at a time process-wide, at below-normal process priority while a build is
  in flight.
- A Vision Service `/start` call that outran its timeout dropped the node's record of the watch and
  re-sent the start on the next reconcile, tearing down the in-progress engine build and starting
  another. Starts are now guarded against overlapping, and the loopback client has an explicit 15s
  timeout instead of the 100s default.
- The live-detection overlay poll shared that same 100s timeout at 6.7 Hz per viewer; it now gets a
  2s budget and skips a tick rather than queueing.
- A camera the Vision Service is not actually watching — after an in-tick restart, or a detection
  engine that failed to load — stayed silently unwatched because the node still believed it was
  running. The node now reconciles against the service's watched-camera list.

### Changed

- TensorRT's builder is bounded to a 2 GiB workspace (`trt_max_workspace_size`) instead of being free
  to claim the whole GPU while the node is also decoding on it, with
  `trt_builder_optimization_level` available as a per-node tuning knob.
- `Vision:TensorRtEngineCachePath` is now optional, defaulting to `%ProgramData%\LarisVMS\trt-cache`.
  Leaving it unset used to silently fall back to plain CUDA.
- The vision log now reports whether a TensorRT engine build was a cache hit or a cold compile, and
  how long it took — the "execution provider appended" line is written before the compile starts and
  was being read as a healthy startup.

## [0.179.0] - 2026-09-03

### Added

- **One recorder node package now runs on any hardware.** The Vision Service bundles the DirectML
  and CPU ONNX Runtime backends (plus the small CUDA files) and picks one at startup for the
  accelerator the node detected — CUDA on an NVIDIA GPU when the CUDA Toolkit is present, otherwise
  DirectML (any Direct3D 12 GPU, Intel iGPUs included), otherwise CPU — degrading gracefully when a
  toolkit is missing instead of failing every camera. `build-node.ps1` and `deploy.ps1` no longer
  take an accelerator flag.
- **The 320 MB CUDA provider library is downloaded from the server on demand**, not shipped in every
  node package. `deploy.ps1` seeds it onto the server and an NVIDIA node with the CUDA Toolkit
  fetches it once — CPU/DirectML-only installs no longer carry it.
- **`install-node.ps1` reports the AI backend a node will use** and, on an NVIDIA box missing the
  CUDA Toolkit, prints exactly what to install; the node runs DirectML in the meantime.

### Fixed

- An Intel-iGPU node whose package was built for CUDA failed AI detection on every camera with
  `OrtSessionOptionsAppendExecutionProvider_Cuda: Failed to load shared library`. The backend is now
  resolved at runtime per machine, so this can't happen.
- TensorRT (`Vision:EnableTensorRt`) was calling an ONNX Runtime API that 1.23 no longer accepts
  (`Unknown provider name 'Tensorrt'`), and any failure in the CUDA setup path dropped the whole
  node to CPU. It now uses the supported `AppendExecutionProvider_Tensorrt` API with `ORT_TENSORRT_*`
  configuration, and a TensorRT failure falls back to plain CUDA instead of CPU. Requires TensorRT
  10.x (this build links `nvinfer_10.dll`).

### Notes

- Recorder nodes must be updated with `install-node.ps1` (not auto-update) to pick up the bundled
  backends — auto-update only swaps the Vision Service executable, not the native runtimes.

## [0.178.0] - 2026-09-02

### Changed

- **Playback timeline bookmark markers now have a thin black outline**, so the amber flag stays
  legible where it sits over a colored recording bucket or directly under the white playhead.

### Notes

- Web-only change. No recorder node rebuild or `install-node.ps1` re-run.

## [0.177.0] - 2026-09-02

### Added

- **The Snapshots filter tree now remembers its state per user.** Collapsing a category, or
  unchecking a whole category or a single object label, used to be forgotten on the next refresh
  unless you also clicked Filter. Those toggles are now saved to your account as you make them and
  restored on the next visit — on any device — while a URL that already carries a filter (a Filter
  submit, a shared link, a pagination click) still wins and is left untouched. Clearing the filter
  also clears the remembered tree state.

### Notes

- Web-only change. No recorder node rebuild or `install-node.ps1` re-run.

## [0.176.0] - 2026-09-02

### Changed

- **Snapshot cards are now framed more prominently in their badge color(s).** Every card's border is
  thicker (3px), and a card that groups overlapping detections on one camera (a person and a vehicle
  in the same frame, for example) is framed in a gradient running through each distinct badge
  color — two colors blend corner to corner, three or four anchor one per corner — instead of only
  showing the first. Cards with a single detection type keep a plain solid border, just wider.

### Notes

- Web-only change. No recorder node rebuild or `install-node.ps1` re-run.

## [0.175.0] - 2026-09-02

### Fixed

- **Lowering the AI detection confidence had no effect below 0.6.** Object tracking refused to start
  a new track for anything scoring under a fixed 0.6, independently of
  **Settings > Detection > Confidence** — so setting the confidence to 0.3 to pick up dimmer or more
  distant objects did nothing, because the detections it let through were then discarded by a gate
  further down. Objects *already* being tracked were unaffected, since tracking deliberately holds an
  established object through weak detections; only newly-appearing ones were lost.

  The tracker's thresholds now follow the configured confidence, keeping the relationship between
  them that the tracking algorithm's own reference uses. A deployment left on the 0.5 default is
  unaffected. One that had lowered its confidence now gets what it asked for — including the false
  positives, so a very low value is worth revisiting: around 0.3 is a reasonable floor.

### Added

- **The detection cadence log line now reports what the model found versus what survived tracking** —
  peak detections per frame, how many of those became tracks, the best raw score seen in the window,
  the score a new track actually has to clear, and the boxes the live overlay is currently drawing
  with their labels, movement states and scores. This is the pair of numbers that localises "there is
  clearly something on screen and it never gets a box": a healthy detection count against zero tracks
  means the model sees the object and tracking is refusing it, while zero detections means the model
  genuinely does not see it. Neither was observable before.

### Notes

- Recorder node / detection-service change — rebuild and deploy the node package. No
  `install-node.ps1` re-run needed.
- The confidence fix above is a real defect but it is **not** the cause of a recorder that has
  stopped reporting AI detections entirely — an object scoring above the configured confidence was
  never affected by it.

## [0.174.2] - 2026-09-02

### Changed

- **AI detection no longer allocates its model input buffer per frame.** 0.174.1 removed two large
  per-frame allocations; instrumentation on a live six-camera recorder then showed the process still
  allocating 140-255 MB/sec and running 4-7 gen2 garbage collections *per second*, essentially all of
  it one line: the input tensor handed to the model, freshly allocated at 4.92 MB every inference and
  large enough to land on the .NET Large Object Heap every time. It is now built once per camera and
  refilled. D-FINE keeps two such buffers rather than one, because its high-resolution re-detection
  runs on a separate task alongside the continuous loop and the two must not share.

### Notes

- Recorder node / detection-service change — rebuild and deploy the node package. No
  `install-node.ps1` re-run needed.

## [0.174.1] - 2026-09-02

### Changed

- **The AI detection loop no longer allocates two large buffers per frame.** Each processed frame
  copied itself into a freshly allocated buffer (1.56 MB at the detection frame size), and YOLOX
  copied its entire output tensor out again before decoding it (2.86 MB). Both are large enough to
  go straight onto the .NET Large Object Heap, so a six-camera recorder was generating on the order
  of 200 MB/sec of large-object garbage purely to move data it already had. The frame is now copied
  into one buffer reused for the life of the pipeline, and the decoder reads the output tensor's own
  memory in place. Neither changes what the model sees.
- **The detection cadence line now also reports process-wide gen2 collection count and allocation
  rate** over the same window. Inference time on a recorder was seen swinging five- to eightfold in
  lockstep across every camera, which is the signature of a process-wide stall rather than anything
  per-camera; garbage collection and GPU contention from the recorder's own hardware-encoded
  recordings look identical from inside the detection service, and these two counters are what
  distinguish them.

### Notes

- Recorder node / detection-service change — rebuild and deploy the node package. No
  `install-node.ps1` re-run needed.

## [0.174.0] - 2026-09-02

### Added

- **A per-camera AI detection orientation** (Cameras > Edit > "Camera orientation override", with a
  deployment default on Admin > Settings > Detection). Some corridor-mounted cameras advertise a
  landscape detection stream over ONVIF (704x480) while actually sending portrait video (480x704).
  Left on the default `Auto`, the recorder builds its ffmpeg scale/pad chain for the advertised
  shape and squashes the real frame into it — so the model runs on a horizontally stretched image
  and every snapshot crop comes off that same distorted buffer. Setting `Portrait` corrects the
  dimensions before the detection profile is built, which fixes the stretched crops **and** the
  detection quality on those cameras. The detection service names this setting in its log when it
  sees a stream whose real shape disagrees with what the watch was started with.
- **A per-camera detection cadence log line**, every 30 seconds per watched camera: frames captured
  per second, frames actually reaching the model per second, how many were dropped, and the last
  inference's duration. The frame rate the inference loop achieves decides whether a moving object
  can be tracked at all — the tracker matches an object between consecutive processed frames, and a
  vehicle crossing frame stops overlapping itself once those frames are too far apart, where a
  parked one never does. Nothing surfaced that number before, and it cannot be recovered from ffmpeg
  (which emits no progress output here even at Debug).

### Fixed

- **AI detection no longer restarts a camera's watch in a loop.** 0.172.0 fixed stretched snapshot
  crops by having the detection service measure the stream's real resolution and report it back for
  the recorder to persist. That correction could not hold: a camera re-probe overwrites the stored
  stream dimensions from ONVIF, and a re-probe runs on every server restart, so the corrected value
  was reverted and the watch restarted — over and over, discarding that camera's object-tracking
  state each time and losing detections around every cycle. The measurement is now a warning that
  names the new orientation setting, which lives where a re-probe cannot reach it.

### Notes

- Recorder node / detection-service change — rebuild and deploy the node package. No
  `install-node.ps1` re-run needed; nodes pick this up through the existing auto-update path.
- An affected camera needs its orientation set explicitly once (Cameras > Edit). Cameras that report
  their shape correctly need no change — `Auto` is the default and keeps today's behavior.

## [0.173.1] - 2026-09-01

### Security

- Camera RTSP credentials are no longer written to log files. ffmpeg echoes the full stream URL —
  username and password included — into its own output, and the recorder logged those lines
  verbatim (every line at Debug, and any "authorization failed" / 401 line at Warning). All ffmpeg
  output is now scrubbed of embedded `user:password@` before it is logged, across the recording,
  motion, live, and AI-detection stream sessions. Existing log files may still contain credentials
  and should be rotated.

## [0.173.0] - 2026-09-01

### Added

- A **medium D-FINE model** (`Obj2Coco (medium)`) in the detection model selection, alongside the
  existing small Obj2Coco / Obj365 options (Admin > Settings > Detection, and per-node on
  Admin > Nodes). Same 80-class COCO vocabulary as small Obj2Coco with a larger backbone — more
  accurate, and more work per frame, so pair it with a capable GPU and a lower detection
  frame-rate cap. The model is not bundled by default; a node package built with it present
  (`tools/export-models/fetch_dfine.py --weights medium-obj2coco`) picks it up automatically.

## [0.172.2] - 2026-09-01

### Changed

- Detection span reports from a recorder are now processed one batch at a time per node, and a
  report that loses a race to another (both inserting the same detection span) is merged into the
  winner instead of being logged as an error and dropped. Hardening around the same area as the
  0.172.1 fix — no visible behavior change on its own.

## [0.172.1] - 2026-09-01

### Fixed

- AI object detection could silently stop producing spans, snapshots, and timeline tags for a busy
  camera — a moving object crossing frame would be tracked live but never recorded — while static
  objects on the same camera kept working. The fragment-coalescing logic backdated an existing
  span's start time onto a value another detection row already held, which violated a unique index;
  the resulting error was caught by dropping the row, so a scene with any real activity stopped
  logging AI detections entirely. Coalescing no longer moves a span's start time (it only ever
  extends the end). Most visible right after upgrading to 0.172.0, which restarted every detection
  pipeline at once.

## [0.172.0] - 2026-09-01

### Fixed

- Portrait and corridor-mounted cameras no longer produce severely horizontally-stretched
  AI-detection snapshot crops. The detection pipeline assumed a 1280x720 landscape shape for any
  camera whose watch stream had never been resolution-probed (which is every manually-added camera,
  and any camera whose ONVIF metadata disagrees with the delivered stream), squashing a portrait
  frame into a landscape buffer before the detector and every snapshot crop ever saw it. It now
  falls back to the camera's real main-stream dimensions for the aspect ratio, and the recorder
  learns and stores the watch stream's true resolution directly from ffmpeg on connect — so an
  affected camera self-corrects within a reconcile cycle and its resolution now shows on the
  dashboard.

## [0.171.0] - 2026-09-01

### Added

- **A detection frame-rate cap** (Admin > Settings > Detection > "Max detection frame rate", default
  10, per-node overridable). The recorder still decodes each camera's Sub stream in real time, but an
  `fps=` filter drops the surplus before inference — so on a busy node the GPU idles between frames
  instead of running flat out, which is what was causing thermal throttling with the larger YOLOX
  sizes. No effect on a camera whose Sub stream is already at or below the cap.
- **YOLOX is the default detection engine.** It is fully-convolutional (no letterboxing needed), runs
  well on low-power and non-Nvidia GPUs, uses an Nvidia GPU fully where present, and ByteTrack was
  designed against it. `Auto` now resolves to YOLOX on every accelerator; D-FINE stays fully
  supported but opt-in (Admin > Settings > Detection). A per-node **YOLOX model size** picker
  (Nano / Tiny / S / M / L / X) matches the model to a node's hardware, global default plus per-node
  override on Admin > Nodes. YOLOX models are not bundled in the node package — a node fetches the
  size it needs from the server on first use and caches it; the server fetches once from a pinned
  upstream (overridable via `DetectionModels:yolox:<size>` configuration) or serves a file seeded
  into its `detection-models/` cache directory. `tools/export-models/fetch_yolox.py` produces those
  files.
- **Playback: a "Now" button** jumps every camera back to the current time in one click.
- **A "catching up" badge** — a recycle icon with the catch-up speed — appears on a live or playback
  tile whose video is running faster than 1x to close a gap: live drift catch-up at 1.5x, playback
  stepped fast-forward, or a playback drift resync.
- **Snapshots: pagination above the grid as well as below**, so paging is reachable wherever the
  page is scrolled.
- **High-resolution snapshots** (Admin > Settings > Detection > "High-resolution snapshots", per-node,
  off by default). The AI-detection snapshot is normally cropped from the reduced-size frame the
  detector runs on. With this on, the recorder decodes the camera's Sub stream at up to its native
  resolution (long edge capped at 1280) and crops the snapshot from that larger frame instead, while
  the detector keeps running at its usual size. It only sharpens snapshots on cameras whose Sub
  stream is itself larger than the detector input — on a small Sub stream it is a no-op. Costs a
  little extra CPU and memory per camera, no extra GPU, and cannot be combined with GPU frame
  preprocessing (switched off per camera while this is on).

### Fixed

- **AI-detection snapshots are now cropped from the exact frame the model detected the object on**,
  not resolved by seeking a timestamp into recorded footage. The first frame a new object starts
  moving, the Vision Service crops a JPEG straight from that frame — covering every object moving in
  it — and makes it that event's snapshot. The box came from those pixels, so the object cannot have
  moved off the crop (the recurring "snapshot doesn't show what was detected" complaint). At the
  detection buffer's size that's a ~150–300 px image; the optional "High-resolution snapshots" setting
  (above) crops from a larger frame where the Sub stream allows. The recorded-segment seek stays as
  the fallback when the crop is missing.
- **Snapshots: overlapping detections on one camera are one card.** A person and a dog crossing frame
  together used to be two cards each cropped to one animal; they now collapse into a single card
  showing that one shared snapshot with a badge per object.
- **AI-detection snapshots were consistently taken a fraction of a second too late.** The detection
  instant was `DateTime.UtcNow` read *after* the frame had waited for an inference slot and the model
  had run — so every snapshot's timestamp trailed the moment its boxes actually described, and the
  crop landed where the object *had been*. The frame now carries the instant the reader captured it,
  used as the detection time throughout.
- **A snapshot that 502'd on its exact-instant seek used to silently fall back to the recording
  segment's first frame** — right crop box, wrong moment. It still falls back (a stale image beats a
  broken thumbnail), but now logs a warning naming the span and sets an `X-Snapshot-Approximate`
  response header.

### Changed

- **Admin > Settings > Nodes: the registration key is masked** behind an eye toggle that reveals it
  until the page is refreshed; generating a new key reveals it automatically.
- **Snapshots: the object filter tree gained expand/collapse carets and guide lines** connecting each
  category to its labels, in place of the browser's default disclosure marker.
- **Framework log noise is now hidden at Information rather than pinned at Warning.** The
  `Microsoft.AspNetCore` request-pipeline lines, the `Microsoft.Hosting.Lifetime` startup banner, the
  `HttpClient` play-by-play, and — on the server — Entity Framework Core's per-statement SQL echo
  (the Logs page's "Executed DbCommand" flood) are filtered out while the deployment-wide log level
  is Information or higher, and come back at Debug or Trace. Applies to the server, every recorder
  node, and every node's detection service.
- **FFmpeg is no longer bundled or copied into the node install.** Install it per node
  (`winget install ffmpeg --scope machine`); the node discovers it on `PATH` or under the WinGet
  package store at every startup, so an ffmpeg upgrade needs no re-install. `install-node.ps1`
  preflight-checks that it is present and drops its `-InstallFfmpeg` switch; `-FfmpegPath` stays as an
  explicit override.
- **Playback: single-camera playback hides the "All cameras" timeline** and its now-redundant
  "Selected camera" label, giving that vertical space back to the video. The merged timeline returns
  for multi-camera views.
- Dropdowns are now only as wide as their widest option instead of stretching to fill their row.

### Notes

- Recorder node / detection-service change (log filtering, ffmpeg discovery, snapshot capture
  instant) — rebuild and deploy the node package and **re-run `install-node.ps1` on each recorder
  node**; ffmpeg must be installed there first (`winget install ffmpeg --scope machine`).
- **YOLOX default:** an install left on the default `Detection.ModelFamily = Auto` switches from
  D-FINE to YOLOX on upgrade — every accelerator, Nvidia included. An install with an explicit
  `DFine` setting is unaffected. The server needs one-time outbound access to the pinned YOLOX model
  host (or the `yolox_*.onnx` files seeded into its `detection-models/` cache); nodes fetch only
  from the server. D-FINE model files stay bundled in the node package as before.

## [0.170.0] - 2026-08-31

### Changed

- **High-resolution re-detection now does its pixel work on the accelerator, not the CPU.** On a
  multi-camera node this path — which decodes the full Main-stream keyframe, tiles it, and runs the
  model over each tile every time a new object is tracked — was the detection service's real CPU
  cost (0.169.x's per-frame preprocessing work turned out to be minor next to it). The rework:
  - `MainFrameDecoder` decodes to packed nv12 (`W*H*3/2` bytes) instead of BGRA (`W*H*4`), via
    NVDEC + `scale_cuda`/`hwdownload` where the node has CUDA — no 19 MB frame downloaded to host,
    no software 4K decode.
  - Tiles are carved out of the nv12 buffer with plain byte copies; the whole-frame SAHI pass is a
    single small CPU letterbox-resize (~1 ms). No more SkiaSharp scaling or cropping of a full
    frame.
  - The nv12 → RGB → normalize step runs in the ONNX preprocessing head on the accelerator (the
    same head 0.169.0 added for the continuous path), so the per-pixel CPU normalize loop is gone
    from this path too.
  - With GPU preprocessing on, the detection engine now holds **one** `InferenceSession` per camera
    instead of two — fixing the ~2× model-weights VRAM and ~1.2 GB host RAM that 0.169.0 introduced.
  Behaviour is unchanged: the same tiles and whole-frame pass, the same NMS, the same re-detected
  box and eager snapshot crop. High-res re-detection stays opt-in and off by default.
- The detection service's framework logging (`Microsoft.AspNetCore` request-pipeline lines — six per
  camera-status poll, several times a second — and the `HttpClient` request play-by-play) is now
  filtered to Warning; it was drowning the vision log. The node's own Kestrel request logging gets
  the same treatment.

### Added

- **A deployment-wide log level** — Admin > Settings > Logs > "Minimum log level"
  (Trace / Debug / Information / Warning / Error, default Information). It applies to this server,
  every recorder node, and every node's detection service, and takes effect **without a restart**:
  the server immediately, nodes on their next check-in (~30 s), which also push it to their Vision
  Service over a new loopback `/log-level` endpoint. Framework request-pipeline chatter stays at
  Warning regardless. Set it to Debug/Trace to get the detailed per-frame diagnostics back when
  troubleshooting.

### Notes

- Recorder node / detection-service change — rebuild and deploy the node package; nodes auto-update
  on their next heartbeat.
- Migration `BumpVersion0_170_0` — `AppVersions` row only, no table changes.
- New, still-untested-on-GPU: `scale_cuda`/`hwdownload` on a piped fMP4 fragment. Fails safe — if
  NVDEC can't decode a fragment, that trigger produces nothing and the Sub-stream best frame stays.

## [0.169.1] - 2026-08-31

### Changed

- **The Main-stream keyframe decode in high-resolution re-detection now runs on NVDEC** (`-hwaccel
  cuda`) where the node has CUDA. Profiling on a real multi-camera node showed the high-res
  re-detection path — not the per-frame preprocessing 0.169.0 targeted — is what actually pegs the
  CPU: per trigger it software-decodes the full 3–4K Main-stream keyframe, Skia-scales and tiles it,
  and CPU-preprocesses the batch, thousands of times an hour. The software HEVC decode of a 4K frame
  was the biggest single piece of that. A fuller GPU rework of the tiling and batch preprocessing is
  planned separately.
- The per-trigger high-res detection dump drops from Information to Debug, and the `HttpClient`
  request logging (four lines per Node callback) is filtered to Warning — both were flooding the
  vision log on a busy camera. Turn the Vision Service's log level up to get them back.

### Notes

- Recorder node / detection-service change — rebuild and deploy the node package; nodes auto-update
  on their next heartbeat.
- Migration `BumpVersion0_169_1` also backfills the `AppVersions` row for 0.169.0 (which shipped
  without its own version-bump migration).

## [0.169.0] - 2026-08-30

### Added

- **GPU frame preprocessing** (Admin > Settings > Detection, node-scoped, off by default). The
  per-frame colour conversion (YUV → RGB) and normalization that AI detection does before every
  inference used to run as a CPU pixel loop — roughly 1.2 million float writes per frame, plus
  ffmpeg's own `swscale` YUV→BGRA conversion. This setting moves both onto the accelerator: a small
  preprocessing head (standard ONNX ops) is merged into the detection model at load, so ONNX Runtime
  schedules it on the same execution provider as the model — CUDA, DirectML or OpenVINO. ffmpeg then
  emits packed nv12 straight through with no `swscale` step, and the detection frame flows as raw
  bytes rather than a decoded bitmap (also removing an `SKBitmap` allocation and copy per frame).
  Vendor-neutral by construction — the same head runs on NVIDIA, AMD and Intel hardware with no
  vendor-specific code. Toggling the setting restarts each camera's detection pipeline. Aimed at
  lowering CPU load on nodes running several cameras.

### Changed

- The detection frame handoff (`LatestFrameSlot`) and the detection-engine input contract now carry
  a raw frame buffer instead of a Skia bitmap. The high-resolution re-detection path (pass 3b) is
  unchanged — it still preprocesses its tiles on the CPU against a plain model session, so with GPU
  preprocessing on, the Vision Service holds two sessions over the same model file (one head-merged,
  one plain).

### Notes

- Recorder node and detection-service change — rebuild and deploy the node package; nodes auto-update
  on their next heartbeat (no `install-node.ps1` re-run).
- Schema migration `BumpVersion0_169_0` — AppVersions row only, no table changes. No `Setting` seed:
  the feature is opt-in and off is the right default for every install.
- New build/runtime dependencies, all permissive: `Google.Protobuf` (BSD-3-Clause) and a vendored,
  build-compiled `onnx-ml.proto` (Apache-2.0) for editing the model graph; `Grpc.Tools` (Apache-2.0)
  is build-only and pulls in no gRPC runtime.
- Colour matrix is BT.601 limited range (the near-universal case for camera sub-streams at ≤720p).
  A stream that declares BT.709 or full range would want the six coefficients in
  `OnnxPreprocessHead` adjusted — verify against a reference frame when enabling on a new node.

## [0.168.0] - 2026-08-30

### Added

- **Vision debug images are now a toggle** under Admin > Settings > Detection, with a button to
  purge the images already written to every recorder node's `logs\vision-debug\` folder. The
  setting is node-scoped and defaults off for new installs; existing installs keep it on (a global
  `Setting` row is seeded by this release's migration) so the current diagnostic workflow is
  uninterrupted. `SaveDebugImage` in the detection pipeline is now a no-op unless the setting is on.

### Changed

- **Snapshot images are cropped from the exact detection frame when high-resolution re-detection is
  enabled.** The re-detection pass already decodes the precise Main-stream frame it re-runs
  inference against (the same frame the vision-debug images come from); it now also crops the
  snapshot from that frame — using the *re-detected* box, not the possibly-stale trigger centroid —
  and ships it to the node. The node stages it and promotes it into the normal span-keyed snapshot
  cache the first time the card is viewed, so retention and the orphaned-snapshot sweep govern it
  exactly like every other snapshot; it is never pruned before its footage. Spans with no
  re-detection result fall back to the recorded-segment crop, which now seeks with millisecond
  precision instead of truncating to a whole second (the other cause of a snapshot not lining up
  with a moving object).
- `MainFrameDecoder` forces its ffmpeg output to the requested dimensions, so a fragment whose real
  resolution has drifted from the probed Main-stream size produces a correctly-framed (at worst
  slightly rescaled) frame instead of a sheared one — which is why a few re-detection crops missed
  their object entirely.
- A high-resolution re-detection trigger that has waited more than 3 seconds behind the process-wide
  concurrency gate is now dropped rather than run against a stale instant.

### Fixed

- **Duplicate Snapshots cards for a single object.** Tracker-ID churn, a per-frame label flip
  (car/truck), a brief occlusion, or a detection-service restart each closed one sighting and
  opened a new span a few seconds later, surfacing as several near-identical cards. The server now
  coalesces AI detections of the same object and camera whose spans start within one idle-timeout
  gap of each other into a single span. A filtered unique index on
  `(CameraId, DetectedObjectLabel, StartUtc)` makes the underlying insert race-safe; this release's
  migration removes any exact-duplicate rows already in the table before creating it.
- **The same object reported by both a camera's own analytics and LarisVMS AI detection** produced
  two Snapshots cards. A camera-native Human/Vehicle/Animal span is now hidden from Snapshots when
  an overlapping LarisVMS AI detection of the same category exists for that camera (the AI span
  carries the real cropped image). The timeline still shows both.

### Notes

- Recorder node and detection-service change — the node package must be rebuilt and deployed. Nodes
  auto-update on their next heartbeat; no `install-node.ps1` re-run is needed (no service-identity
  change).
- Schema migration `SnapshotAlignmentAndDedup_0_168_0`: the filtered unique index, the
  `Detection.EnableVisionDebugImages` setting seed, and a one-time cleanup of exact-duplicate
  AI-detection spans. `deploy.ps1` applies it automatically.

## [0.167.3] - 2026-08-30

### Fixed

- **Grid mode's mask, size, sensitivity, and active-mode choice never survived a page refresh** —
  reported live as two symptoms ("cells don't save" and "always reopens in Grid mode") with one real
  cause: `CameraService.GetAsync` builds its `Camera` result through a hand-written field-by-field
  projection (`ProjectWithoutCredentials`, there specifically to keep encrypted credential columns out
  of the query entirely), and it was never updated when `MotionRegionMode`/`MotionGridSize`/
  `MotionGridMask`/`MotionGridSensitivity` were added. Skipping a field there doesn't fail loudly — the
  resulting object just silently gets that property's plain C# default instead of its real stored
  value, for every caller of `GetAsync`, including the Zones page's own `GET /motion-region` endpoint.
  The actual `Save` button was working correctly the whole time; nothing ever read the saved values
  back. Fixed by adding the four missing fields to the projection.

### Notes

- Web-tier change only.
- No schema migration needed.
- `NodeService.GetConfigAsync` (what the node itself uses to actually run Grid-mode detection) was
  never affected — it loads the full `Camera` entity via `.Include(...)`, not this same projection, so
  Grid mode itself was detecting motion correctly the whole time; only the editor's own read-back was
  broken.

## [0.167.2] - 2026-08-30

### Added

- **Drag-select for the Grid editor's cells**, instead of one click per cell — masking a real area on
  a 64x64 grid (4096 cells) one click at a time didn't scale. Pressing down on a cell decides the
  direction for the whole drag from that cell's own current state (unmasked → painting masked, masked
  → painting unmasked); every other cell the pointer crosses while the button stays down is set to
  that same target state, not toggled individually, so re-crossing a cell mid-drag can't flip it back
  the other way. A plain click with no movement still works as a single-cell toggle, unchanged.

### Notes

- Web-tier (client-side only) change.
- No schema migration needed.

## [0.167.1] - 2026-08-30

### Changed

- **Merged the Grid and Polygon zone editors onto one Zones page**, per direct feedback that two
  separate pages for two mutually-exclusive methods didn't make sense. A single toggle now switches
  which editor is shown *and* immediately activates that method in the same click — no separate
  "make this active" step, and no separate "this isn't the active method" banner, since the toggle's
  own visual state already shows that. The `MotionGrid` page is gone; `zones-editor.js` now owns both
  editors, sharing the same canvas and live video underneath.
- **Grid cell edits now have an actual Save step.** Clicking cells (and changing the size or
  sensitivity) only updates local state and marks it unsaved, exactly like the polygon zone form
  already works — nothing reaches the server until Save is clicked. Previously every click saved
  immediately with no visible confirmation, which read as if nothing had happened.
- **New cameras now default to Grid mode** (previously Polygon) — simpler to get started with than
  drawing polygons. Existing cameras are unaffected: the schema migration that introduced Grid mode
  already backfilled every existing row to Polygon explicitly, so nothing here changes what an
  already-configured camera is currently watching.

### Notes

- Web-tier change only.
- No schema migration needed — this only changes an in-memory default for newly-created cameras, not
  the database column's own backfilled value.

## [0.167.0] - 2026-08-30

### Added

- **The Grid editor UI for pass 3c-2** — completes the checkpoint 0.166.0 shipped the backend for.
  New `Pages/Cameras/MotionGrid` (linked from `Cameras/Index`'s row actions, alongside Zones and Event
  tags): live video with a click-to-mask cell grid drawn over it, a 16/32/64 size selector (changing
  size clears the mask, with a confirmation), and a sensitivity slider matching a Polygon zone's own.
  Cells wash amber when their live score is over threshold, red when masked, and get no fill at all
  when quiet — only their grid line — so the video underneath stays readable. A "Make this the active
  method" control switches `Camera.MotionRegionMode`; both the Zones and MotionGrid editors now show a
  banner when they aren't the currently active method, so a camera's other, saved-but-inactive
  configuration is never mistaken for what's actually running.
- New endpoints: `GET/PUT /api/cameras/{id}/motion-region` (mode) and
  `PUT /api/cameras/{id}/motion-region/grid` (size/mask/sensitivity), `Cameras.Edit`-gated like every
  other zone-editor endpoint.
- This completes pass 3c-2 and, with it, the full planned pass 3 checkpoint sequence (3a, 3b, 3d,
  3c-1, 3c-2).

### Notes

- Web-tier change only — no Node/Vision Service behavior changes in this piece (0.166.0 already
  shipped the Node-side Grid-mode detection logic this UI now drives).
- No schema migration needed (the columns shipped in 0.166.0's `AddMotionGridColumns`).
- To verify: open Motion grid on a camera with a real tree/wind problem, mask the offending cells,
  click "Make this the active method," and confirm `MotionSpans` for that camera stop firing from
  those cells — then switch back to Polygon and confirm the original zones are untouched and still
  scoring.

## [0.166.0] - 2026-08-30

### Added

- **Backend for pass 3c-2 (Grid mode) of the detection/hardware-acceleration overhaul** — the
  alternative to hand-drawn polygon zones, and the primary motion-tuning mechanism on nodes with no
  GPU accelerator (where ServerMotion frame-diff is the *only* detection mechanism running at all).
  No editor UI yet in this change (that's the next, separate checkpoint) — this ships the plumbing a
  UI needs to actually do something:
  - New `Camera.MotionRegionMode` (`Polygon` default | `Grid`), `MotionGridSize` (16/32/64, default
    32), `MotionGridMask` (a base64 cell bitset), and `MotionGridSensitivity` (the grid's own
    equivalent of a zone's Sensitivity — needed since its single aggregate region still has to compare
    against *something* before opening a span). Switching modes never touches the inactive method's
    own configuration — every `Zone` row survives a switch to Grid, and the grid mask survives a
    switch back to Polygon.
  - New `MotionGrid` (Media): a pure bitset mask plus a single-pass per-cell scorer, reusing
    `ZoneRasterizer`'s exact `bool[width*height]` mask shape so `MotionSession`/`MotionDetector.Score`
    need no knowledge of which mode produced a mask — one masking mechanism, two editors, never two
    mechanisms that can disagree. Per-cell scoring is a single O(width*height) pass regardless of grid
    size, not one `MotionDetector.Score` call per cell (which would rescan the whole frame for each
    cell's own handful of pixels).
  - `NodeWorker.ReconcileMotion` now branches on region mode: Grid mode feeds `MotionSession` one
    aggregate region (the whole frame minus masked cells) instead of a zone-per-ServerMotion-zone
    list, reports spans with `ZoneId = null` (already a supported shape — camera-pushed motion spans
    already do this), and a fully-masked grid stops the session the same way zero enabled zones
    already does for Polygon mode. Mode, grid size, mask, and sensitivity are all in the session
    restart signature, so a live change actually takes effect rather than waiting for an unrelated
    restart.
  - `/live/{cameraId}/motion-zones` now wraps its payload (`{zones, cellScores}` instead of 3c-1's
    bare array) so Grid mode's live per-cell scores can ride the same message instead of a second
    socket — `zones-editor.js` updated to unwrap the new shape; a Polygon-mode camera's own zone wash
    is unaffected.

### Notes

- Requires a database migration (`AddMotionGridColumns`) — new nullable/defaulted columns only, no
  data loss, existing cameras default to `Polygon` mode (today's exact behavior) on upgrade.
- Still to come: the actual Grid editor page (click-to-mask canvas over live video, a size selector,
  a `Cameras/Index` row-action link, and "mode not active" banners on both editors) — nothing in this
  release lets an operator actually switch a camera into Grid mode yet.

## [0.165.1] - 2026-08-30

### Fixed

- **Zone `Kind` silently serialized as a bare integer, not its name, over `/api/cameras/{id}/zones`
  and zone creation** — the `Zone` entity has no `[JsonConverter]` on `ZoneKind` and this app registers
  no global `JsonStringEnumConverter`, so returning the entity directly (as these endpoints always
  have) sent `kind: 0` instead of `kind: "ServerMotion"`. Confirmed live as two separate-looking bugs
  with one real cause: `zones-editor.js`'s `KIND_COLORS`/`KIND_LABELS` lookups and its zone-edit form's
  Kind `<select>` are all keyed by the string name, so every zone silently rendered with whichever
  color `0`/`undefined` happened to fall back to (ServerMotion's own amber — an Ignore zone never
  actually showed its intended red), and re-opening any non-ServerMotion zone's edit form never
  selected the right option, always defaulting back to ServerMotion. New `ZoneDto` (Core) fixes this at
  the response boundary, the same `Kind`-as-string convention `SaveZoneRequest` already uses for the
  request side.
- **0.165.0's live per-zone wash was never fully transparent even at rest**, which masked whether the
  live score feed was actually reaching the browser at all — a continuous ratio-based fade (this
  session's own deviation from the original plan's spec) looked identical whether scores were flowing
  or the socket had never connected. Switched to the plan's actual spec: no fill below a zone's own
  Sensitivity, a fixed muted yellow at or above it. A zone that now stays fully invisible at rest and
  visibly lights up on real motion confirms the feed is live; one that never lights up at all points at
  `ServerMotionEnabled` not being on for that camera, or the socket not connecting — not at this code.

### Notes

- Web-tier change only — no Node/Vision Service rebuild needed, though the version stays in lockstep
  as usual.
- No schema migration needed.
- Still not built: `Camera.MotionRegionMode`'s `Polygon`/`Grid` toggle and the cell-grid mask editor
  the original plan describes as a separate item (3c-2) — today's Zones editor only ever supports the
  polygon method. Flagged live as missing; it's a real, larger follow-up checkpoint, not a bug in what
  shipped as 3c-1.

## [0.165.0] - 2026-08-30

### Added

- **Checkpoint 4 (3c-1) of the detection/hardware-acceleration overhaul: the Zones editor now shows
  real live video with each Motion zone washed by its own live motion score**, instead of one static
  snapshot with fixed-opacity polygons. `MotionSession` now tracks every zone's most recent per-frame
  score (`GetCurrentZoneScores`); a new `/live/{cameraId}/motion-zones` WebSocket (Node, poll-driven
  off that snapshot, same "never block the hot loop" shape as the existing AI-detection overlay) and
  a matching Web-tier proxy relay it to the browser. `zones-editor.js` now starts real live video
  (reusing `live-view.js`'s existing MSE player) behind its existing polygon canvas, falling back to
  today's static snapshot until the first live frame arrives — or permanently, on a camera that can't
  stream live at all. Each ServerMotion zone's fill opacity now rises and falls continuously with its
  live score relative to its own Sensitivity, so tuning a zone against real trees moving is visible in
  real time; Ignore/CameraMotion/Privacy zones (which have no live score of their own) keep today's
  fixed look unchanged.
- New `MotionZoneScoreDto` (Core) and `MotionZoneOverlayHandler` (Node) — no per-tick augmentation
  needed here (unlike the AI-detection overlay), since the Zones editor already has each zone's own
  Sensitivity/Kind/enabled state loaded from `/api/cameras/{id}/zones`; the Web-tier proxy for this
  one is a plain relay.

### Notes

- Rebuild/redeploy `LarisVMS.Node` (JS/Razor changes ship with the Web app as usual; no Vision Service
  change this time).
- No schema migration needed.
- This completes the planned pass 3 checkpoint sequence (3a -> 3b -> 3d -> 3c-1).

## [0.164.0] - 2026-08-30

### Added

- **Checkpoint 3d of the detection/hardware-acceleration overhaul: pass 3b's high-res re-detection
  result now actually feeds the snapshot a viewer sees**, instead of only being logged. Turned out not
  to need a new eager-write file cache at all — the reported detection box was already normalized
  (0-1) and persisted per span (`MotionSpan.BestBoxX/Y/W/H`), with the existing `/snapshot-image` route
  already cropping lazily from the recorded Main-stream segment at request time. So the box competing
  for that spot just needed a better candidate: `ProcessHighResTriggerAsync`'s merged, native-scale
  result is now matched (by overlap, not by label — a native-scale re-detection can genuinely disagree
  with the stabilized label on what an object *is*, as already confirmed live on a cat) against the
  track that triggered it, and fed into the same `LabelBestFrameTracker` competition the continuous
  Sub-stream pass already uses. No match (the object moved on before the result came back) leaves
  today's coarser candidate in place — a wash, never a regression.
- Applied from inside the continuous inference loop's own thread, not from the high-res task directly
  — `LabelBestFrameTracker` is documented single-owner/not-thread-safe, so the result is queued
  (`_pendingHighResResults`) and drained each frame, discarding anything whose track has since
  disappeared (a stale result winning a since-started, unrelated span for the same label would be
  worse than just not applying it).
- `Nms.FindBestMatch` (new, unit-tested): finds the best-overlapping candidate in a result set against
  a target box, or null if nothing overlaps at all.

### Notes

- Vision Service change only — rebuild/redeploy `LarisVMS.Vision.Service`.
- No schema migration needed.
- Next up: checkpoint 3c-1 (live per-zone motion wash on the Zones editor).

## [0.163.4] - 2026-08-30

### Fixed

- **Nothing bounded how many high-res re-detection triggers (pass 3b) could run at once across
  cameras** — each trigger spawns its own ffmpeg process against the Main-stream ring buffer plus a
  batched inference call, and every camera's detection pipeline drains its own trigger queue
  independently. A busy moment on several cameras at the same time (or a cluttered scene producing
  many separate tracks in quick succession) could pile up that many concurrent ffmpeg decodes with
  nothing to throttle them, which is a very plausible cause of node CPU pegging reported live —
  and, via degraded frame processing feeding back into tracker ID churn, of duplicated/fragmented
  snapshot events for what should be one continuous real-world object (label flips like the same
  vehicle alternating between "car" and "truck" across different track IDs bypass the per-track
  `TrackLabelArbiter`'s stabilization, since each new track id starts that arbiter fresh). Added a
  single process-wide gate (`CameraPipelineManager`'s own `SemaphoreSlim`, shared into every camera's
  `CameraDetectionPipeline`) so only one high-res re-detection operation runs at a time, regardless of
  how many cameras trigger simultaneously. There's no snapshot latency cost to serializing this yet —
  this pass only logs its merged result; checkpoint 3d (not yet built) is what will actually persist
  it, at which point this limit may need revisiting.
- Confirmed via debug images (enabled by 0.163.3): the whole-frame + native-tile box math is placing
  boxes correctly — a moving cat got a tight, correctly-positioned bounding box, just an inaccurate
  label (car/truck/bird), which is ordinary object-detection model behavior on a small/atypical
  subject at native scale, not a pipeline bug.

### Notes

- Vision Service change only — rebuild/redeploy `LarisVMS.Vision.Service` (or let auto-update pick it
  up alongside Node).
- No schema migration needed.
- If CPU and duplicate-snapshot symptoms persist after this ships, that would point away from
  cross-camera concurrency and toward something else (e.g. the continuous Sub-stream pipeline's own
  CPU cost, which is what the master plan's pass 4 — a separate, much larger piece of work — targets;
  pass 4 does not touch this trigger path at all, so it wouldn't fix this specific gap either way).

## [0.163.3] - 2026-08-30

### Fixed

- **0.163.2's own debug image dump silently failed to write anything, and gave no indication why** —
  the exact mistake 0.163.1 had just fixed for Vision Service's own visibility generally, repeated
  locally: the failure was logged at `LogDebug`, which sits below Vision Service's own
  `FileLoggerProvider` `Information` minimum, so if the JPEG write was failing, the reason was
  invisible. Bumped to `LogWarning`. Also bumped the "could not reach this node's main-frame buffer"
  failure path in the same method for the same reason — a genuine network-level failure to reach the
  ring buffer is worth seeing, not just the common/expected 404 case (which is intentionally still
  silent).
- **Good news surfacing while chasing this**: live confirmation that pass 3b's whole-frame pass
  correctly identified a real moving car, matching what was actually driving through frame at the
  time — alongside the earlier confirmed match on a stationary "bus" that turned out to genuinely be
  the reporting camera's own view of a parked fifth-wheel trailer. What looked like an implausible
  pile of detections earlier was very likely a busy real scene (several parked vehicles plus the
  moving one), not corrupted decode — the debug images (once this fix reveals why they weren't
  writing) will confirm box alignment precisely.

### Notes

- Vision Service change only — rebuild/redeploy `LarisVMS.Vision.Service` (or let auto-update pick it
  up alongside Node).
- No schema migration needed.

## [0.163.2] - 2026-08-30

### Fixed

- **The Node process crashed entirely (not just one background task) after an ordinary HTTPS timeout
  talking to the web tier** — reported live. `StorageManager`, `NodeWorker`, and
  `ThumbnailBackfillService` (all `BackgroundService`s) caught failures with
  `catch (Exception ex) when (ex is not OperationCanceledException)`, intending to let a genuine
  shutdown-triggered cancellation pass through uncaught while still catching real failures. But
  `HttpClient.Timeout` also throws a `TaskCanceledException` — which *is* an `OperationCanceledException`
  — so every one of those filters excluded an ordinary network timeout from being caught at all,
  letting it propagate out of `ExecuteAsync` and trigger the Generic Host's default
  `BackgroundServiceExceptionBehavior.StopHost`, tearing down the whole node over a transient SSL
  connection drop. Fixed at all 7 affected call sites by checking `!ct.IsCancellationRequested`
  instead of the exception's static type — the same correct idiom already used elsewhere in this
  exact codebase (`MainFrameDecoder`, `SnapshotImageCapture`, `ThumbnailCapture`), just not
  consistently in these three files. Three lower-severity occurrences of the identical pattern
  remain in `DetectionOverlayHandler.cs`/`ExportRunner.cs`/`CameraEventSession.cs` — none of those
  run inside a `BackgroundService`, so a failure there can't take down the whole process the same
  way, but the underlying reasoning is the same and they're worth the same fix eventually.

### Added

- **Temporary diagnostic**: pass 3b's high-res re-detection now dumps the decoded Main-stream frame,
  the letterboxed whole-frame bitmap, and every native tile crop as JPEGs under
  `%ProgramData%\LarisVMS\logs\vision-debug\`, one set per trigger — added to visually confirm
  reported boxes actually line up with real objects in the scene (rather than trusting labels/
  coordinates alone), given `MainFrameDecoder`'s ffmpeg-fed-via-a-pipe decode is the one genuinely
  new, never-tested-on-real-hardware piece in this whole pass. Not meant to stay past verifying this
  checkpoint — no retention sweep covers this folder, so it will need to be cleared manually or
  removed in code once no longer needed.

### Notes

- Node + Web Service change — rebuild/redeploy `LarisVMS.Node` and `LarisVMS.Vision.Service`.
- No schema migration needed.

## [0.163.1] - 2026-08-30

### Fixed

- **`LarisVMS.Vision.Service` has never had its own log file — reported live while trying to verify
  0.163.0's high-res re-detection.** Its console output is captured by
  `VisionServiceSupervisor.DrainOutputAsync` and re-logged into the Node process's own logger, but
  always at `Debug` severity regardless of the line's real level, while Node's own file logger is
  configured at an `Information` minimum — so every line Vision Service ever produced, including this
  pass's own "High-res re-detection..." diagnostics, was silently dropped before reaching any log
  file. Not something 0.163.0 introduced; this made a pre-existing gap impossible to miss. Vision
  Service now has its own `FileLoggerProvider` (the same shared class Node already uses, just with an
  `Information` minimum and a `vision-` file prefix), writing into the same shared logs directory as
  Node's own log. `StorageManager`'s 14-day log-retention sweep now covers `vision-*.log` files too,
  not just `node-*.log`.

### Notes

- Node change — rebuild and re-run `install-node.ps1` (or let auto-update pick it up).
- No schema migration needed.

## [0.163.0] - 2026-08-30

### Added

- **Motion-guided native-scale re-detection (pass 3b of the detection/hardware-acceleration
  overhaul)** — new opt-in (off by default) "High-resolution re-detection" toggle on
  `Admin → Settings → Detection`. When on, the first frame a newly-tracked object starts moving,
  its pipeline fetches the corresponding instant from pass 3a's Main-stream ring buffer, decodes it
  once, and runs one batched detection pass over the whole frame (letterboxed) plus native-scale
  640×640 tiles placed at the track's own centroid (SAHI-style) — merged with a new greedy NMS
  (nothing like it existed anywhere in `LarisVMS.Vision` before this; D-FINE's own single-pass output
  never produces duplicates, which stops being true the moment two separate passes can see the same
  object). Catches small/distant objects a squashed lower-resolution frame misses, and finds a large
  one whole instead of clipped into tile fragments. New `TileLayout`/`Nms` (pure, unit-tested),
  `DFineEngine.DetectBatch` (batches N same-sized images into one ONNX forward pass), and
  `MainFrameDecoder` (the one new ffmpeg-fed-via-a-pipe code path in this pass — decodes a Main-stream
  frame from ring-buffer bytes; not the same risk class as the deferred pass 4b's raw-NVDEC work,
  since this hands ffmpeg's own mp4 demuxer a standard fragmented-MP4 byte stream and lets it handle
  codec framing itself).
- **This checkpoint stops at logging the merged, corrected result** (camera, track, label, confidence,
  which pass — whole-frame or which tile — produced the winning box, and the Main-stream pixel
  coordinates) rather than wiring it into a snapshot file yet. Today's cache file naming keys by a
  `MotionSpan` id that doesn't exist yet at this point in the pipeline — that's a real design decision
  (a leading candidate is keying by `(CameraId, StartUtc)` instead, both already known and stable the
  instant a span opens) left to the next checkpoint (pass 3d), deliberately, rather than guessed at
  here.

### Notes

- Node change — rebuild and re-run `install-node.ps1` (or let auto-update pick it up). Enable the new
  toggle on one node first; watch that node's Vision Service log for the new
  "High-res re-detection..." lines to confirm it's actually finding both a near and a distant object
  in the same panoramic frame, per this pass's own verification ask.
- No schema migration needed — the new toggle is a `Setting` row (`Detection.EnableHighResReDetection`),
  not a new column, same as `Detection.AspectMode` before it.

## [0.162.0] - 2026-08-30

### Added

- **Main-stream fragment ring buffer (pass 3a of the detection/hardware-acceleration overhaul)** — the
  first piece of replacing today's lazy, after-the-fact snapshot cropping (which seeks into an
  already-written segment file, and is the underlying reason a `Segment.DurationMs`-vs-real-duration
  drift could 502 a snapshot, worked around in v0.161.6) with eager capture from the high-res Main
  stream at the moment of detection. New `MainFrameRingBuffer` subscribes to `RecordingSession`'s
  existing live-tee fanout (the same fragments `LiveViewerHandler` already relays to browser live
  viewers) and keeps a short (10s / 32MB per camera) in-memory window of recent, independently
  decodable fMP4 fragments — no new RTSP session, no change to `RecordingSession`'s ffmpeg arguments.
  New loopback-only node route `GET /internal/main-frame/{cameraId}?atUtc=...` hands the buffered
  bytes to `LarisVMS.Vision.Service` on request; the node itself never decodes anything, keeping it
  GPU/ONNX-free per the standing architecture constraint.
- This checkpoint only buffers and serves bytes — nothing decodes or uses them yet. That's pass 3b
  (motion-guided native-scale re-detection), the next checkpoint in this arc.

### Notes

- Node change — rebuild and re-run `install-node.ps1` (or let auto-update pick it up).
- No schema migration needed — this adds no columns, only an in-memory buffer and a new endpoint.

## [0.161.6] - 2026-08-30

### Fixed

- **The last 2 of 24 Snapshots cards that still 502'd after 0.161.5** (confirmed live to fail in
  under a second — not gate contention, a genuine per-span capture failure). `Segment.DurationMs` is
  derived from wall-clock `EndUtc - StartUtc` (`NodeService.RecordSegmentsAsync`), not a
  re-measurement of the file's actual encoded duration, and `GetSnapshotImageInfoAsync`'s offset is
  clamped against that same possibly-inflated value — so a best-frame instant near a short/truncated
  segment's believed end can still land past the file's real content. This exact class of drift was
  already identified and fixed once, for `/playback-thumbnail`'s sibling lookup, with a retry at
  offset 0 on a `502` — `/snapshot-image`'s proxy never got the same fix. `ProxySnapshotImageAsync`
  now retries once at offset 0 on the same segment/crop box, trading exact-instant precision (which
  frame within the recording) for a guaranteed hit, same as the thumbnail path already does.
- **Diagnostic follow-up**: `SnapshotImageCapture.CaptureAsync` now logs ffmpeg's own stderr output
  (not just "produced no bytes") when a capture still fails after the retry, so any future case is
  diagnosable directly from the node's log instead of by elimination.

### Notes

- Web + Node change — rebuild/redeploy the web app and re-run `install-node.ps1` (or let auto-update
  pick up the node build) for the new logging.

## [0.161.5] - 2026-08-30

### Fixed

- **Found via 0.161.4's new logging**: the "No thumbnail available" cards from that release were
  concurrency, not data — a handful of AI-detection Snapshots cards timed out with a `502` on every
  load, and it looked exactly like a sticky, deterministic bug because the *same* cards kept losing
  the same race on every reload. The Snapshots page requests up to a full page (24) of AI-detection
  crops in one burst; `LarisVMS.Node`'s `/snapshot-image` capture was gated to **2 concurrent ffmpeg
  captures with a 3-second give-up**, inherited unchanged from the hover-scrub-preview gate it was
  modeled on. A single capture measured live at 3.5-4.4 seconds, so at most the first couple of a
  24-card burst could ever get a slot before the 3s window closed — every other card 502'd, logged
  (as of 0.161.4) as "ffmpeg produced no cropped frame," which is misleading: ffmpeg was never even
  invoked for those, they just never got a turn. The hover-scrub case this gate was designed for has
  a fundamentally different shape — one request at a time, and a request that can't get served
  quickly really is stale — which doesn't apply to a static page where every one of 24 requested
  cards still wants its image. `OnDemandSnapshotGate` (used only by Snapshots-page AI-detection
  crops, nothing else) is now sized 6 concurrent with a 30s timeout instead of 2/3s. The sibling
  `OnDemandThumbnailGate` (shared with live hover-scrub, where the original 2/3s reasoning still
  applies) is unchanged. Both gates' give-up paths now log which one timed out and after how long, so
  this class of failure is never silently indistinguishable from a real ffmpeg failure again.

### Notes

- Node change — rebuild and re-run `install-node.ps1` (or let auto-update pick it up).

## [0.161.4] - 2026-08-30

### Added

- **Diagnostic logging for a separate, still-open issue**: some Snapshots cards show "No thumbnail
  available" even though their footage plays fine via Playback, and reloading doesn't fix it — a
  sticky, per-card failure distinct from the footage-coverage guard fixed in 0.161.0-0.161.3.
  `LarisVMS.Node`'s `/playback-thumbnail` and `/snapshot-image` routes previously returned a bare
  404/502 with no log line at all on every failure path, so there was no way to tell which of several
  possible causes was actually firing. Both routes now log a warning identifying which case occurred:
  the requested segment path not matching the node's *current* storage root (e.g. after a per-node
  storage root override was changed, since `Segment.FilePath` is baked in at record time and never
  rewritten), the segment file genuinely missing from disk despite its `Segment` row, or ffmpeg
  producing no frame (timeout, corrupt segment, an offset/crop past the file's actual content). No
  behavior change — purely additive logging to narrow down the real cause from the node's own log the
  next time a card fails this way.

### Notes

- Node change — rebuild and re-run `install-node.ps1` (or let auto-update pick it up) to get the new
  log lines.

## [0.161.3] - 2026-08-30

### Fixed

- **The Snapshots page still timed out (`SqlException: Execution Timeout Expired`) after 0.161.2's
  index.** The index was not the real problem. Pass 2c's footage-coverage test is an *interval-overlap*
  test (`segment.StartUtc < span.EndUtc AND segment.EndUtc > span's play-from point`), and a B-tree
  index can only seek on one range boundary — the other side is always a residual scan. Expressed as a
  correlated subquery it therefore runs per candidate row, and `CountAsync` makes "candidate" mean
  every `MotionSpan` the page's filters allow (tens of thousands) against a `Segments` table holding a
  row per minute per camera. No index makes that shape fast; three successive attempts to fix it by
  rewriting the query (0.161.0 through 0.161.2) each failed live for a different reason.
  **The coverage check now runs after pagination, over just the (at most 24) rows actually being
  rendered**, where every predicate is a constant and each check is an ordinary index seek. Same
  visible behavior — cards whose footage is gone, including spans that fall inside a genuine recording
  gap, still don't appear — and it now uses each camera's own resolved pre-roll rather than one global
  value.
- Deliberate trade-off, documented in the code: the page's total/page count is computed *without* the
  coverage filter, so a page can render fewer than 24 cards and the count can slightly overstate.
  Making the count exact requires the coverage test back inside the counted query, which is precisely
  the thing that cannot be made fast. An approximate count is worth a page that loads.

### Notes

- Web-only fix (`TimelineService.cs`) — no Node change, no schema change. 0.161.2's
  `IX_Segments_CameraId_EndUtc` index is kept: it did not fix the timeout on its own, but it does make
  the new per-row coverage seeks (`CameraId` + `EndUtc >`) efficient, and it is a non-clustered index
  on a table written once per completed recording segment, so it costs effectively nothing.

## [0.161.2] - 2026-08-30

### Fixed

- **Attempted fix for the Snapshots page timing out on every load
  (`SqlException: Execution Timeout Expired`)**, reported live immediately after 0.161.1: added a
  `(CameraId, EndUtc)` index on `Segment`, on the theory that the correlated overlap check was scanning
  for want of an index on `EndUtc`. **This did not fix it** — see 0.161.3 for the actual cause (the
  overlap predicate is a range-on-both-sides test that no B-tree can fully seek, evaluated per row by
  `CountAsync`). The index itself is kept, since it does make 0.161.3's bounded per-row checks
  efficient.

### Notes

- Schema migration: new non-clustered index `IX_Segments_CameraId_EndUtc`. Building it may take a
  moment on a `Segments` table with a large history — expect the migration step of the next deploy to
  pause briefly rather than complete instantly.

## [0.161.1] - 2026-08-30

### Fixed

- **The Snapshots page threw an unhandled exception on every load** (`InvalidOperationException:
  ... could not be translated`), reported live immediately after deploying 0.161.0. Pass 2c's
  new query-time footage guard (`GetSnapshotsAsync`) used a shape SQL Server's EF Core provider
  can't translate — first a correlated `Any()` combined with a `Min()` subquery in one `Where`
  predicate, then (after a first attempted fix) a `Join` against a `GroupBy` followed by
  `DateTime`-minus-`TimeSpan` arithmetic on the joined column, which also failed to translate.
  Rewritten (this release) as a single, plain correlated `Any()` (`EXISTS`) testing for an
  actually-overlapping segment — the simplest shape that reliably translates on every provider.
  **This version timed out live under real data volume** (missing supporting index) — fixed in
  0.161.2 by adding the index, not by changing this query again.
- **Many plain-motion/zone Snapshots cards still showed "No thumbnail available" even once the page
  loaded**, reported live in the same session — unrelated to anything Pass 3 addresses (that pass
  is about AI-detection crop quality, not plain-motion thumbnail lookup). The guard's first working
  version only checked "is this span newer than the camera's *overall* earliest remaining segment,"
  which is a real gap, not just an approximation: a span can be newer than a camera's oldest
  surviving footage while its own specific instant still falls inside a genuine recording gap (a
  node restart, a dropped RTSP connection) with nothing actually covering it. This release's `Any()`
  guard is what actually fixes this correctly (once 0.161.2's index makes it fast enough to ship).

### Notes

- Web-only fix (`TimelineService.cs`) — no Node change, no schema change.

## [0.161.0] - 2026-08-29

### Fixed

- **One moving object could produce several snapshots.** D-FINE's per-frame class guess can flicker
  for a single physical object (a cat crossing frame read as cat → dog → cow → horse), and the
  detection pipeline's hysteresis/best-frame/reporting state was keyed by that raw per-frame label —
  each label flickered through opened its own independent span and its own snapshot. Pass 2a of the
  detection/hardware-acceleration overhaul. New `TrackLabelArbiter` resolves each ByteTrack track id
  to one stable label (the highest peak-confidence label seen for that track, with a confidence
  margin a challenger must clear to take over) before hysteresis/best-frame tracking/reporting ever
  see it — the live detection overlay keeps showing the model's raw, unarbitrated per-frame guess.
- **Clicking a Snapshots card dropped the viewer in at the exact detection instant, with no lead-in**
  — the object had often already entered frame during the recording's own pre-roll, but Playback
  started at the moment it was confirmed. Pass 2b. `SnapshotDto` gains `PlayFromUtc`
  (`StartUtc` minus the camera's configured pre-roll, applied uniformly across every span kind),
  separate from the existing `AtUtc` (the thumbnail's own sample point, whose AI/classified branch is
  deliberately pre-roll-less — reverting that once was a confirmed live regression, so it is
  untouched). Both Playback links on the Snapshots page now use `PlayFromUtc`; the thumbnail image
  itself still uses `AtUtc`.
- **A `MotionSpan` row lived forever even after its footage aged out of retention**, leaving a
  permanent "No thumbnail available" placeholder card. Pass 2c. New `MotionSpanRetentionService`
  mirrors `BookmarkRetentionService`'s own "entries expire with the footage they point at" pattern
  (6-hour cadence, same staleness rule — compared against `PlayFromUtc`, not `StartUtc`, since the
  pre-roll lead-in is part of what the card promises to play) but batches via keyset pagination
  rather than loading the whole table, since `MotionSpans` runs far higher-volume than `Bookmarks`.
  `GetSnapshotsAsync` also gained a query-time guard hiding an aged-out span immediately, rather than
  waiting for the next sweep. A new node-authenticated `GET /api/nodes/snapshots/span-ids` plus a
  `StorageManager` reconciliation sweep (mirroring the existing segment-reconciliation pattern, with
  the same reachability-probe/re-check/implausible-mass-deletion guards) self-heals any cached
  snapshot crop file left behind once its owning row is gone.

### Notes

- Node change (pass 2a is Vision Service, 2c's reconciliation sweep is `LarisVMS.Node`) — rebuild and
  re-run `install-node.ps1` on every recorder.
- No schema migration needed — pass 2c adds no new columns, only a new background sweep and a new
  read-only endpoint. A version-bump migration (`AppVersions` insert only) still needs generating:
  `dotnet ef migrations add BumpVersion0_161_0 --project src/LarisVMS.Infrastructure --startup-project src/LarisVMS.Web`
  (empty scaffold since there's no pending model change — add the `INSERT INTO AppVersions`/
  `DELETE FROM AppVersions` SQL to its `Up()`/`Down()` by hand, matching every other BumpVersion
  migration in this file's history) — not generated as part of this change, same standing reason as
  earlier passes' migrations.

## [0.160.0] - 2026-08-29

### Changed

- **AI detection now fits each camera's own real aspect ratio into D-FINE's square input, instead of
  decoding every camera to one fixed global resolution and stretching whatever came out into a
  square regardless of shape.** Pass 1 of the detection/hardware-acceleration overhaul. A new
  `InferenceProfile` (per camera) computes the network-input geometry from that camera's real
  Sub-stream dimensions (the ffmpeg-probed ground truth, `CameraStream.Width/Height` — see
  `RecordingSession.TryParseVideoStreamLine`'s own reasoning for why that beats ONVIF's advertised
  value) and the deployment's chosen aspect mode:
  - **Letterbox** (new default): preserves the camera's own aspect ratio, padding with black bars to
    fill the square — a portrait or panoramic camera keeps correct proportions instead of being
    squashed. `VisionSession`'s own ffmpeg filter chain now does the aspect-preserving scale and pad
    directly (a plain, always-CPU `pad` filter — deliberately not attempted inside `scale_cuda`'s own
    GPU filter chain, an unverified pairing this codebase has already been burned by once with
    privacy-mask burn-in), so the captured frame arrives pre-sized and pre-letterboxed.
  - **Stretch**: today's pre-existing behavior (independent per-axis scale, no padding), kept so the
    change is bisectable.
  - A third mode, **AspectMatched**, is reserved for a possible future pass but not implemented —
    requesting it throws.
- **`SKBitmap.Resize` is gone from the AI-detection hot path.** Before this, `VisionSession` always
  decoded to a fixed 1280x720 and `DFineEngine.Preprocess` did a second, separate non-aspect-
  preserving resize down to D-FINE's 640x640 input. Now `VisionSession`'s ffmpeg filter chain decodes
  directly to the exact network input size (stretched or letterboxed, per the mode above), so
  `Preprocess` only ever packs pixels — a real CPU reduction on top of pass 0's motion-decode fix,
  measured per detection-enabled camera rather than per ServerMotion camera.
- New per-node setting, "Aspect fitting" (`Detection.AspectMode`, global default on
  `Admin → Settings → Detection`, per-node override on `Admin → Nodes`, same shape as the existing
  detection-model picker) — Letterbox or Stretch.
- **Retired the global `Detection.Width`/`Detection.Height` settings** (a single decode resolution
  every camera shared regardless of its own shape) — decode resolution is now always derived
  per-camera from its own real stream dimensions. Nothing to migrate: these were never exposed in the
  admin UI, so no stored value needs reconciling.
- Live-detection overlay boxes (`live-view.js`) now line up correctly on non-16:9 cameras — their
  coordinates are normalized against the camera's own real aspect ratio rather than the old fixed
  global decode resolution, which visibly drifted the more a camera's shape differed from 16:9.

### Notes

- Node change — rebuild and re-run `install-node.ps1` on every recorder to pick up the letterbox/
  stretch decode path and the new setting.
- No schema migration needed — `Detection.AspectMode` is a `Setting`/`SettingOverride` row, not a new
  column. A version-bump migration (`AppVersions` insert only) still needs generating:
  `dotnet ef migrations add BumpVersion0_160_0 --project src/LarisVMS.Infrastructure --startup-project src/LarisVMS.Web`
  (this produces an empty scaffold since there's no pending model change — add the
  `INSERT INTO AppVersions`/`DELETE FROM AppVersions` SQL to its `Up()`/`Down()` by hand, matching
  every other BumpVersion migration in this file's history) — not generated as part of this change,
  same standing reason as pass 0's migration.

## [0.159.0] - 2026-08-29

### Changed

- **Server-side motion detection (`MotionSession`) now runs on the same NVDEC/CUDA decode path AI
  detection already uses, on nodes with an Nvidia accelerator resolved.** Pass 0 of the detection/
  hardware-acceleration overhaul: this was the single largest CPU cost in the whole detection stack
  — every camera with a ServerMotion zone ran a continuous *software* video decode of its Sub stream
  regardless of the node's accelerator, unlike recording and live view (`-c copy`, near-zero) or AI
  detection (already GPU-decoded). The CUDA path decodes and scales on the GPU (`scale_cuda` +
  `hwdownload`, the same pairing `VisionSession` already proved end to end) and reads the resulting
  nv12 frame's Y (luma) plane directly — no format-specific code downstream, since a plain grayscale
  frame and nv12's Y plane are byte-identical in shape. Any other accelerator (or none) keeps
  today's plain software decode unchanged; Intel/AMD hwaccel decode for this session is future work.
- **New per-camera "Run server-side pixel motion detection" toggle** (`Cameras/Edit`, default **on**
  — no behavior change on upgrade). Added specifically because `MotionDetectionSource` only chooses
  which signal *gates Motion-mode recording*; it was never a switch for whether `MotionSession` runs
  at all. Before this toggle, a camera whose chosen source was AI detection (or another source) still
  paid for a full Sub-stream decode purely to keep tagging the timeline with plain motion as a
  fallback in case something triggered motion without an object being detected — genuinely useful,
  but not something every camera needs. Turning it off now stops that session (and its decode cost)
  outright; leaving it on keeps exactly today's behavior.

### Notes

- Node change — rebuild and re-run `install-node.ps1` on every recorder to pick up the CUDA motion
  decode path and the new toggle.
- **Migration still needed**: `Camera.ServerMotionEnabled` (new column) requires
  `dotnet ef migrations add AddCameraServerMotionEnabled --project src/LarisVMS.Infrastructure --startup-project src/LarisVMS.Web`,
  run before `dotnet ef database update`/deploy — not generated as part of this change (this
  environment doesn't run `dotnet` commands; see the standing project convention on that).
  `ApplicationDbContext` already configures `HasDefaultValue(true)` for the new column, so the
  generated migration should show `defaultValue: true` on its `AddColumn` call — worth a quick look
  at the generated file before applying, since every existing camera needs to land on `true` (no
  behavior change on upgrade) rather than bool's usual `false` default.

## [0.158.0] - 2026-08-26

### Fixed (post-D-FINE follow-up, confirmed against real hardware)

- **`LarisVMS.Vision.Service` CPU usage went up, not down, after the D-FINE switch** — the opposite
  of the expected effect (DETR-style detection has no per-frame NMS, unlike YOLO). Root cause:
  `DFineDecoder` was computing `Sigmoid` (`Math.Exp`) unconditionally for every query×class
  combination every frame (300×80=24,000 calls for the default model, ~110,000 for the Obj365
  variant), and `DFineEngine`'s preprocessing used `SKBitmap.Pixels` (a full per-pixel color-space
  conversion) plus the tensor's generic multi-dimensional indexer, both avoidable per-frame costs.
  Fixed by pre-filtering candidates in logit-space before ever calling `Exp` (sigmoid is monotonic,
  so this is a cheap comparison with identical results) and by reading raw pixel bytes directly into
  the tensor's own flat buffer instead.
- **AI-detection snapshot crops sometimes missed the detected object entirely** (an empty region
  where it would have been) once D-FINE's tighter, more accurate boxes shipped. Root cause: the
  crop margin (`SnapshotImageCapture.DefaultMarginFraction`) was purely relative to the detected
  box's own size — reasonable with YOLOv9's looser boxes, but D-FINE's tighter ones leave far less
  absolute pixel slack to absorb the pre-existing timing gap between Sub-stream detection (where the
  box was computed) and the Main-stream recording (which the crop is actually taken from) — the same
  gap visible on the *live* detection overlay running slightly ahead of the video it's drawn over.
  Increased the margin and added a floor relative to the *frame's* own dimensions (not just the
  box's), so a small/distant object's tiny box still gets meaningful absolute slack instead of
  scaling down to near-zero alongside it.
- Snapshots page cards now show seconds in their displayed timestamp (was minutes-only) — useful for
  lining up exactly when a snapshot's frame was grabbed against the live view or recording, given
  the timing gap noted above.

### Changed

- **Replaced YOLOv9 with D-FINE as the default AI detection model.** YOLOv9's weight license isn't
  permissive enough for this project; D-FINE (Apache-2.0, end to end) is. Rather than adapt D-FINE
  to impersonate a YOLO architecture for YoloDotNet's own decoder (the same graph-surgery trick
  YOLOv9 needed, and the approach already rejected for RT-DETR in this codebase), `DFineEngine`/
  `DFineDecoder` run D-FINE's real ONNX output directly via `Microsoft.ML.OnnxRuntime` — verified
  end-to-end against a real model run (manually replicating the exact preprocessing/decode math
  against the classic COCO "two cats + remote controls" test image correctly recovered both cats,
  the couch, and both remotes, with clean non-duplicated boxes and no NMS needed) before any
  production code was written. `YoloEngine.cs` is deleted; `YoloDotNet` itself stays as a dependency
  only for its `ObjectDetection`/`LabelModel` types (so `ByteTracker`/`MovementClassifier`/
  `CameraDetectionPipeline` needed no changes) and for its per-accelerator native ONNX Runtime
  packaging. D-FINE is also DETR-style (300 queries, no per-frame NMS) rather than YOLO's dense
  anchor-grid decode+NMS, which — beyond the licensing fix — is expected to noticeably reduce the
  CPU load the previous pipeline was putting on the host while leaving the GPU underused; the
  first real-hardware run should confirm this.
- Detection model selection is now a real, user-facing choice — Admin → Settings → AI Detection
  (`Detection.ModelFamily`/`Detection.DFineWeights`, global default + per-node override on Admin →
  Nodes, since one Vision Service process serves every camera on a node from the same loaded
  model). Two D-FINE weight variants are selectable: `Obj2Coco` (80 COCO classes, default) and
  `Obj365` (365 Objects365 classes, a much richer vocabulary). RF-DETR and YOLOX are reserved
  picker slots (rendered disabled) for a future release — Auto's own Intel/AMD/CPU default would
  normally pick YOLOX, but falls back to D-FINE until it actually ships, so a non-Nvidia node keeps
  detecting rather than silently going dark.
- `tools/export-models/export.py`/`onnx_compat.py` (YOLOv9-specific export/graph-adaptation) are
  replaced by `tools/export-models/fetch_dfine.py` — a plain, pinned-revision download from
  Hugging Face, no export/adaptation step at all since D-FINE's own published ONNX already matches
  what `DFineEngine` expects.
- `CocoCategoryMap`'s `Vehicle`/`Animal` word lists now also cover Objects365's much larger
  vocabulary (`SUV`, `Wild Bird`, `Rickshaw`, ...), and D-FINE's own legacy PASCAL-VOC-era COCO
  spellings (`motorbike`, `aeroplane`) — previously absent, so a motorbike or aeroplane detection
  silently fell through to the generic "Object" category instead of "Vehicle".

### Fixed

- **Login was broken site-wide: the password field rendered as plain text and every sign-in attempt
  failed with HTTP 400**, identically across every browser/device. Root cause: `Areas/Identity/Pages/`
  has its own `_ViewStart.cshtml` (routes the login page through this app's shared layout) but no
  `_ViewImports.cshtml` — and Razor's tag-helper registration is picked up by walking up the directory
  tree from the page itself, which never reaches `Pages/_ViewImports.cshtml` since `Areas/Identity/Pages/`
  isn't a descendant of `Pages/`. Without `@addTagHelper` active, `asp-for="Input.Password"` never
  rendered `type="password"` (plain `<input>` defaults to text), and the form's antiforgery hidden
  input — normally auto-injected by the FormTagHelper — never appeared, so every POST failed
  antiforgery validation. Added the missing `Areas/Identity/Pages/_ViewImports.cshtml`.

- AI-detection snapshots reported duplicate spans for a single continuous sighting of the same
  object. `CameraDetectionPipeline`'s per-label `MotionHysteresis` was constructed with `endAfter:
  TimeSpan.Zero`, so a single missed/occluded detection frame — or a track dipping into `Idle` for
  even a moment before resuming `Moving` — closed the span immediately, and the very next detection
  opened a brand-new one. Added a real grace period (`Detection.IdleTimeoutSeconds`, default 10s,
  global setting) so brief flicker no longer fragments one sighting into several snapshots, while a
  genuinely idle/departed object still finalizes its span once the grace period elapses.
- AI-detection "best frame" snapshot cropping could pick the wrong object when multiple detections
  shared a label — e.g. a snapshot for a car driving past a driveway showing the parked truck
  already in frame instead of the car, because the truck's larger, more stable box out-scored the
  car's on every frame. `CameraDetectionPipeline` now only lets a detection compete for "best frame"
  while it's actually contributing to the reported span (`Moving`, not `Idle`), and tracks a
  separate "oversized" candidate pool (boxes covering more than 80% of the frame) that's only used
  as a fallback when no normal-sized candidate exists at all — so a genuine close-up (a face filling
  the frame) still gets captured correctly.
- Every AI-detected snapshot showed the same generic 📦 emoji regardless of category (Human, Vehicle,
  Animal, Object) — `TimelineService.GetSnapshotsAsync`'s AI-category branch hardcoded the emoji
  literal instead of looking it up per category. Added `CocoCategoryMap.Emoji`, mirroring the
  per-class lookup the camera-native `DetectionDisplay.Emoji` already had.
- AI-detection bounding boxes didn't track zoom/pan on a fullscreened live tile — the box overlay is
  a plain `<canvas>` sibling of the `<video>` element, and zoom/pan is applied as a CSS transform
  directly on the video itself, so the boxes stayed fixed at their pre-zoom screen position while the
  video content scaled/slid underneath them. `fullscreen-tile.js` now exposes the exact transform
  string it applies to the video, and the overlay canvas applies the identical string to itself on
  every redraw, so the two can never drift out of sync with each other.
- Live-view tiles never showed a badge for AI-detected objects at all — the badge-polling query only
  ever looked at camera-native `DetectionKind` spans. Badges for AI categories are now derived
  client-side from the same live per-frame WebSocket stream already used to draw detection boxes
  (`live-view.js`), since only that live state — not anything persisted in the database — actually
  knows whether a specific track is moving right now. Shows one badge per unique specific label
  currently moving (a moving car and a moving truck both badge separately), never for idle/stationary
  objects.
- **Clicking a snapshot (or any other autoplay deep link) landed on a paused tile that needed a
  Pause-then-Play click before video actually started.** Root cause, in `playback-player.js`:
  `resolveDeepLink` set the page's "playing" state to true before any tile had a source attached,
  which drove `applySpeed` to call `videoEl.play()` on a source-less `<video>` — per spec that's not
  a no-op, it makes the element "potentially playing" at `readyState = HAVE_NOTHING` and fires a
  `waiting` event, arming the 3-second stall watchdog for a load that hadn't even started. The real
  segment fetch (routinely longer than 3s for a cold/large 4K segment) then tripped that watchdog's
  recovery path, which read `!videoEl.paused` — false, since `teardown()` had legitimately paused the
  element for the reload — as "don't resume autoplay", stranding the tile paused while the toolbar
  still read "Pause". Fixed two ways: `applySpeed` no longer calls `play()` on a tile with no segment
  attached yet (the one call that was arming the watchdog spuriously), and recovery now asks each
  tile whether the *page* currently intends it to be playing instead of trusting the element's
  momentary paused state during a reload — the give-up path (after repeated recovery failures at the
  same target) also resets the page's Play/Pause button to match, rather than leaving it stuck
  showing "Pause" over a tile that stopped trying to resume.

### Changed

- Unified the two separate "a person was detected" vocabularies: camera-native detections
  (`DetectionKind.Person`, from onboard ONVIF/CGI analytics) and the AI pipeline's own `Human`
  category never shared a filter token before, so the Snapshots page showed two separate "person"
  checkboxes for what is conceptually one thing. Renamed the enum member to `DetectionKind.Human`
  (its persisted value is unchanged) so it now shares the same name/filter token/emoji as the AI
  category's `Human`, the same way `Vehicle`/`Animal` already do — the two checkboxes collapse into
  one automatically via the Snapshots page's existing dedupe-by-name rendering.

### Added

- AI detection's confidence/IoU thresholds and which stream it watches (Main/Sub) are now real
  settings, editable on a new AI Detection tab (`Admin/Settings/Detection`) with the same
  global-default + per-camera-override pattern Recording settings already use. Confidence/IoU were
  previously deployment-wide only, with no admin UI to change them at all (`NodeService` read them
  from a `Setting` row nothing ever wrote); which stream feeds detection used to be unconditionally
  hardcoded to Sub in `NodeWorker.ReconcileVision`, with no way to change it. Confidence/IoU are
  entered as sliders (5% steps, 50% centered) rather than free-text boxes, with a live readout next
  to each that tracks the drag as it happens.
- The camera list now shows which cameras have AI detection enabled and which stream it's watching
  (`🤖 Sub`/`🤖 Main` next to the camera name) — previously only visible by opening each camera's own
  Edit page one at a time.

- The Snapshots page filter is now a collapsible tree in a left-side sidebar next to the snapshot
  grid, instead of a flat row of checkboxes above it. Each AI-detection category (Vehicle, Animal,
  ...) that has more than one specific label actually seen expands to show a checkbox per label (car,
  truck, bicycle, ...) — unchecking a whole category still excludes everything under it as before,
  and unchecking one specific label now excludes just that label while its siblings stay included.
  The tree is populated live from what's actually in the database, not a fixed list, since which
  categories/labels exist depends entirely on which detection model produced them.

## [0.157.1] - 2026-08-25

### Fixed

- `build-node.ps1`'s model-bundling step threw `The property 'Count' cannot be found on this object`
  under `Set-StrictMode -Version Latest` whenever exactly one `.onnx` file matched — `Get-ChildItem`
  unwraps a single-item result to a bare `FileInfo` instead of an array, and strict mode turns the
  resulting missing-`.Count` into a terminating error rather than `$null`. Wrapped the result in `@()`
  to keep it an array regardless of match count.
- `install-node.ps1` hit the same `Set-StrictMode`/`.Count` bug as `build-node.ps1` above, in
  `Stop-OrphanedFfmpeg` and `Stop-OrphanedVisionService` — `Get-Process` unwraps to a bare `Process`
  instead of an array when exactly one orphaned process matches, and `.Count` doesn't exist on it
  under strict mode. Same fix, wrapped in `@()`.
- Live-view Moving/Idle AI-detection bounding-box overlay (`live-view.js`'s `startDetectionOverlay`,
  shipped earlier in this same release) was never actually wired up — no checkbox existed anywhere on
  `Views/Play`, and nothing ever called it. Added a Moving/Idle checkbox pair to the Play toolbar and
  wired `view-play.js` to start one overlay per tile and apply both toggles to every tile at once.
  Both toggles persist per-user via the existing server-backed preferences store (not localStorage),
  the same as playback-player.js's own toggles — they follow the viewer across devices/logins rather
  than resetting on every page load.
- **`LarisVMS.Vision.Service` couldn't find ffmpeg.** `VisionServiceSupervisor` never told the child
  process where it lives — `VisionServiceOptions.FfmpegPath` was left unset, so
  `FfmpegPathResolver.Resolve(null)` fell back to a bare `"ffmpeg"` relying on `PATH`, which this
  service (normally LocalSystem) doesn't have. `LarisVMS.Node` itself always knows the real path
  (`--ffmpeg-path`, `LARISVMS_FFMPEG_PATH`, or its own PATH probe, resolved once at its own startup)
  and now passes it straight through via `Vision__FfmpegPath`, so the two processes can't disagree
  about which ffmpeg they're each running.
- **`build-node.ps1` never bundled an exported model.** It searched `models/` non-recursively, but
  `tools/export-models` drives libreyolo, which writes to `models/weights/` next to the `.pt` it
  converted from — so a perfectly good export was reported as "No .onnx model found" and the package
  shipped with an empty `models/` folder. Now searched recursively and flattened into the package.
  `tools/export-models/README.md` corrected too: it claimed output lands in `models/` directly.
- **False gaps in recording coverage — Playback reporting "no recording available" for windows a
  real, intact segment actually covered, even in Continuous mode.** `RecordingSession` computes each
  segment's `StartUtc`/`EndUtc` from `firstRealDataObservedUtc`, a dictionary meant to hold one
  timestamp per file, shared between two independent lookups that are supposed to describe the same
  physical instant: the end of segment N and the start of segment N+1 both mean "when did the file
  that became segment N+1 first receive real data." The lookup for each fell back to that file's raw
  `CreationTimeUtc` when the dictionary had no entry yet — but never wrote that fallback *into* the
  dictionary, so if the preceding segment's `end` resolved via the fallback (file still header-only,
  below `MinRealDataBytes`, on that poll) while the file's *own* `start` was resolved on a later poll
  once real data had actually grown it past the threshold, the two independently-computed values for
  the same instant could — and confirmed live against a production `Segments` table, routinely did —
  diverge, by anywhere from a couple of seconds to nearly a minute. Found via a snapshot that existed
  with no playable recording behind it at the same timestamp: the underlying video was continuous
  throughout — this was purely an indexing gap. Root-caused by directly reading the recorder's Segments
  table and comparing filename-encoded vs. true (NTFS `LastWriteTimeUtc`) segment write times to rule
  out a suspected local/UTC labeling bug in `RecordingSession.StartFfmpeg`'s (deliberately local,
  cosmetic-only, and unrelated) `%Y%m%dT%H%M%SZ` folder/filename pattern before finding the real cause.
  Fixed by routing both lookups through one write-through resolver, so whichever side asks first
  permanently locks in the answer for both — `end(N)` and `start(N+1)` are now provably equal by
  construction. Affected roughly 41% of all segment transitions in one night's recording, repaired
  retroactively across the production `Segments` table (8,531 rows corrected, ~17.8 hours of
  already-recorded footage made playable again; 72 rows left untouched as likely genuine outages,
  distinguishable by size — every false-gap value fell under a minute, while the excluded rows ranged
  from just over a minute to over an hour).
- **Live-view detection boxes never drew, despite the WebSocket streaming real data every tick.**
  `ProxyDetectionOverlayAsync` (Web's `/live/{cameraId}/detections` proxy) re-serialized each tick
  with a bare `JsonSerializer.SerializeToUtf8Bytes(enriched)` — no options, so .NET's PascalCase
  default (`MovementState`, `X`, `Y`, `W`, `H`, `ColorHex`, ...) shipped to the browser instead of
  the camelCase this app's JS everywhere else expects (`Results.Json`'s own Minimal API default, and
  every hand-written anonymous-object endpoint already written in camelCase to match). `live-view.js`'s
  `draw()` reads `box.movementState`/`box.x`/`box.colorHex`/etc. — all `undefined` against the
  PascalCase payload, so `box.x * dw` became `NaN` and `ctx.strokeRect(NaN, NaN, NaN, NaN)` silently
  drew nothing. No exception anywhere in the chain: confirmed live via the browser's own WS inspector
  showing real, correctly-shaped detections arriving every ~150-300ms while the canvas stayed
  empty. Also meant the Moving/Idle checkboxes were never actually filtering anything, since
  `undefined !== 'Moving'`/`'Idle'` never matched either. Fixed at the one serialize call with an
  explicit camelCase `JsonSerializerOptions`, built once per viewer connection rather than per tick.
- **AI detection could never find its model on any node.** `VisionServiceOptions.ModelPath` defaulted
  to the literal filename `models/model.onnx`, but nothing in this project produces that name —
  `tools/export-models` emits the upstream weight name (`yolo9-t.onnx`, `yolo9-s.onnx`) and
  `build-node.ps1` bundles whatever `.onnx` files exist verbatim. So a correctly built, correctly
  installed package with a correctly configured GPU still failed every start with ONNX Runtime's
  `Load model from models/model.onnx failed. File doesn't exist`. Vision Service now discovers the
  bundled model: an explicitly configured path that exists still wins, otherwise it uses what was
  actually bundled in that directory. More than one bundled model is the normal case rather than an
  error (export.py's own default set is two), so it picks deterministically by name and logs which,
  instead of refusing to start; none at all now fails with a message naming the directory and
  pointing at the exporter.
- **Stopping the node service left `LarisVMS.Vision.Service.exe` running.** `NodeWorker`'s shutdown
  stopped the Vision Service child as the *last* step of its `finally` block — behind awaiting every
  recording/motion/event/integration session and four `CancellationToken.None` server flushes, each
  carrying the API client's own timeout. A slow or unreachable server at shutdown pushed that past
  the host's `ShutdownTimeout`, and when that fired the remaining cleanup never ran, so the one step
  that must not be skipped was the first one lost. The orphan then held its own exe and the
  CUDA/cuDNN natives beside it open, failing the next in-place upgrade with "being used by another
  process". Stopping the child is now the first thing the `finally` does, and the child is
  additionally placed in a Windows job object with `KILL_ON_JOB_CLOSE` so the OS terminates it
  whenever the node process goes away — covering the paths no shutdown code can reach at all
  (`ShutdownTimeout` expiry, a crash, `taskkill`, the SCM giving up on a hung stop).
- `install-node.ps1` now checks the AI-detection native dependencies at install time and, where it
  can, fixes them: it reads which accelerator the package was actually built for from the provider
  DLL beside the exe, verifies each required CUDA/cuDNN library resolves the way the OS loader would
  (install directory, system directories, and the machine `PATH` from the registry rather than a
  possibly-stale `$env:PATH`), and copies anything missing in from where these libraries actually
  land — a pip `nvidia-*` site-packages `bin`, NVIDIA's own cuDNN layout, or the CUDA Toolkit's
  `bin`. cuDNN copies bring the whole `cudnn*.dll` set, since `cudnn64_9.dll` is a dispatcher that
  loads its siblings at runtime and copying only the DLL named in a loader error just moves the
  failure to the next one. Copying beside the exe rather than editing `PATH` is deliberate: a
  Windows Service inherits its environment from `services.exe` at boot and would not see a new
  `PATH` entry until the machine is rebooted. Anything genuinely absent is reported with where to
  get it, and a missing `.onnx` model is called out too. Never fatal — a node without GPU libraries
  still records normally.
- A `LarisVMS.Vision.Service` startup failure was undiagnosable from the node's own log, which said
  only `Vision Service returned InternalServerError` once per reconcile tick while the actual reason
  existed nowhere but the Windows Application event log. Vision Service's `/start` now catches the
  failure and returns the flattened exception message chain as ProblemDetails, and `NodeWorker` reads
  that body into its warning — so a machine-level setup problem (a missing CUDA runtime DLL, an
  absent `.onnx` model, a GPU the driver won't hand out) names itself in `node-*.log` directly.
  Found via a recorder that was missing `cublasLt64_12.dll`: the CUDA Toolkit runtime has to be
  installed on any node running a `-Accel Cuda` build, since neither the NuGet packages nor
  `install-node.ps1` provide it. README gains a "Recorder node dependencies" section covering this —
  what each `-Accel` variant needs installed on the node, and that both `deploy.ps1 -NodeAccel` and
  `build-node.ps1 -Accel` silently default to `Cpu`.
- `Cameras/Edit`'s "Watch this camera for AI object detection" checkbox and "Motion detection source"
  dropdown always reset to unchecked/auto on the next page load, even though the save itself worked —
  `CameraService`'s hand-rolled `ProjectWithoutCredentials` projection (used by every `GetAsync`/
  `ListAsync` read) explicitly lists which `Camera` columns to carry over, and the two columns this
  release added were never added to that list, so every read silently came back with the type's
  default (`false`/`null`) regardless of what was actually stored. The Node-side config path reads
  `Camera` directly and was unaffected — recorders had the correct value the whole time, only the web
  UI's own display of it was wrong.

Node change — rebuild and re-run `install-node.ps1` on every recorder to pick up the CUDA/cuDNN
detection, ffmpeg-path, model-discovery, and Vision Service shutdown fixes above; a node not yet
rebuilt keeps working exactly as it did under 0.157.0 (recording unaffected either way). No new
migration content beyond this version-bump row — the Segments repair above was applied directly, not
through a schema change.

## [0.157.0] - 2026-08-24

### Added

- **Native AI object detection.** LarisVMS now runs its own real-time YOLO/ByteTrack detection
  pipeline per node instead of depending on onboard camera analytics or a separately-run debug
  process (`aitest`, the standalone repo this was prototyped and validated in — now untouched,
  read-only reference). The existing onboard-classification object detection (ONVIF PullPoint /
  Dahua-Amcrest CGI, see 0.30.0-era entries) is unaffected and keeps working exactly as before; this
  is a second, independent detection source layered alongside it, not a replacement.
  - **Capture**: a new `VisionSession` (`LarisVMS.Vision`) opens a *second*, independent RTSP session
    against a camera's Sub stream — the same established pattern `MotionSession`/`SubLiveSession`
    already use for exactly this kind of analysis workload — decoding via a GPU-hybrid ffmpeg pipeline
    (`-hwaccel cuda -hwaccel_output_format cuda` + `scale_cuda`, falling back to plain CPU scale on
    other accelerators). Inference (YoloDotNet/ONNX Runtime) and tracking (a full MIT-licensed
    ByteTrack port — Kalman filter, track matching, the works) run against its frames, with a new
    `MovementClassifier` splitting each tracked object into Moving/Idle by centroid displacement and
    keeping the single best-scoring frame (`confidence × normalized box area`) seen across its whole
    lifetime for later use as a snapshot.
  - **Runs as a sibling process, not inside `LarisVMS.Node.exe`.** A new `LarisVMS.Vision.Service`
    executable (localhost-only control API) carries the GPU/ONNX Runtime native dependencies, so a
    site that never enables AI detection pays nothing for it, and a bad GPU/driver interaction can
    never take down camera recording itself — `NodeWorker`'s new `VisionServiceSupervisor` starts,
    supervises, and restarts it as a child process, same "desired-state reconcile" shape every other
    node-side session already uses. `LarisVMS.Node` still has **zero** reference to the GPU-dependent
    `LarisVMS.Vision` library.
  - **Hardware accelerator is a per-node setting** (`Admin → Nodes`: Auto / Nvidia / Intel / AMD /
    CPU), not per-camera — a node has one physical machine's worth of hardware. A new
    `AccelCapabilityProber` probes what's actually present at startup and reports it back on every
    heartbeat (`Admin → Nodes`' new readout column); Auto picks the best of what's detected
    (Nvidia > Intel > AMD priority) and never silently falls back to CPU. No usable accelerator (or
    AI detection simply not enabled) means no `LarisVMS.Vision.Service` instance runs at all, logged
    once — every other detection path already configured for every camera on that node is completely
    unaffected. `LarisVMS.Vision.Service` is published once per execution provider
    (`build-node.ps1 -Accel Cuda|DirectML|OpenVino|Cpu`, same MSBuild switch pattern `aitest` proved
    out) since YoloDotNet only links one provider per process — a site with genuinely different
    hardware across nodes needs a package built per accelerator.
  - **Categories, not 80 individual colors.** Detected classes bucket into a small, auto-colored
    catalog (`DetectedObjectCategory`: Human / Vehicle / Animal / Object today, more as new classes
    are first seen) so the timeline doesn't turn into a wall of near-indistinguishable colors the way
    coloring all ~80 COCO classes individually would. The specific detected label still rides
    alongside on every snapshot/timeline entry ("Vehicle — car", not just "Vehicle") — only the
    *category* drives the badge color. A brand-new category gets the next unused color from a curated
    palette, same "pick a color that hasn't been used yet" shape `DetectionDisplay` already has for
    the older onboard-classification kinds.
  - **Live-view bounding boxes are a separate, client-side-only overlay** — a new small WebSocket
    (`/live/{cameraId}/detections`) feeding a `<canvas>` drawn over the video element, never touching
    recorded bytes. Moving and Idle objects have **independent** show/hide toggles (both off by
    default), so a scene full of static objects doesn't have to compete with what's actually moving
    unless a viewer specifically wants to see it too.
  - **Motion-mode recording gains exactly one *primary* generic motion source.** Previously every
    configured signal (server-side pixel zone, ONVIF camera events, a vendor integration) counted
    independently toward a Motion-mode camera's keep/discard decision and toward tagging the
    timeline — redundant when more than one is configured for the same camera, since they're all
    reporting the same real event. A new per-camera `Motion detection source` setting
    (`Server-side motion` / `Camera events` / `Vendor integration` / `AI detection`, defaulting to
    whichever of the first three is actually configured, richest signal first, if never explicitly
    set) narrows the three generic sources to exactly one active at a time. A named event tag rule and
    AI detection's own object labels are deliberately **outside** this restriction — both always tag
    the timeline and can each independently keep a segment, regardless of which source is chosen as
    primary, the same "always-on OR term" precedent event tag rules already had. Being purely
    additional OR terms throughout, none of this can ever discard footage that would otherwise have
    been kept.
  - **Snapshot images for a detected object are cropped to its bounding box** — a new, genuinely
    separate `snapshots/` cache tier (distinct from the existing hover-thumbnail `thumbs/` cache),
    capped at 720p, generated from whichever frame across the whole detection scored best (confidence
    × box size), with margin added around the tight box so it doesn't read as a context-free sliver.
    `StorageManager` deletes a segment's matching snapshot image alongside it at every eviction path
    (retention, quota, watermark, orphaned-camera sweep) — the same "never outlives its source
    footage" guarantee the thumbnail cache already had.
  - **Model export tooling** (`tools/export-models/`) ported byte-for-byte from `aitest` so LarisVMS
    never depends on that repo for its `.onnx` files — exports permissively-licensed (MIT) YOLOv9
    weights and adapts them to load in YoloDotNet. Deliberately excludes Ultralytics YOLOv8/11/26:
    those weights are AGPL-3.0, and this project is MIT.
  - **Recorder-node auto-update now also keeps an already-installed `LarisVMS.Vision.Service.exe`
    current**, not just `LarisVMS.Node.exe` itself. `deploy.ps1` registers the matching Vision binary
    from the same `build-node.ps1` publish alongside the Node exe (`NodeBuildVersion` gains optional
    `Vision*` columns for it — a build without one, e.g. `-SkipNodeVision`, simply offers nothing extra),
    and `LarisVMS.NodeUpdater` swaps it in the same pass as the Node exe, guarded to only ever touch a
    node that already has a working Vision Service install (its native DLLs and exported model are
    placed by `install-node.ps1`, never by this download/swap path, so swapping the bare exe onto a
    node that's never had it would just crash-loop with nothing to load) — a node's *first* Vision
    Service install still needs one `install-node.ps1` run, same as this release's own rollout below.
    Deliberately best-effort throughout: a Vision Service download/checksum/swap failure is logged and
    retried next heartbeat, never blocking or failing the primary Node exe update it rides alongside.
    ⚠️ One binary per platform row, not per node — a fleet with genuinely mixed hardware (some nodes
    GPU-capable, some not) needs to manage the Vision Service binary on those specific machines by
    hand rather than relying on this, since there's no per-node accelerator-variant selection in the
    approval queue (`Admin → Node Builds` flags it explicitly).

  **Unverified against real GPU hardware or an actual camera end-to-end** — every piece above builds,
  and the pure/isolated logic (movement classification, category/color assignment, accelerator
  selection, motion-source resolution, crop-rectangle math, the Vision Service download/checksum
  staging and updater arg parsing) is unit tested, but the capture→inference→tracking pipeline itself,
  the concurrent-RTSP-session count this adds on top of Main recording/Sub motion/Sub adaptive-live
  (now up to four per camera), the snapshot crop's Main/Sub field-of-view alignment, and the Vision
  Service auto-update path's own real Windows-Service-restart choreography all still need a real run
  against real hardware before this is trusted the way the rest of this app's shipped features are.

**Node change — `install-node.ps1` re-run needed on every recorder, once, for this release.** No
already-installed node has ever had `LarisVMS.Vision.Service.exe` (or its native DLLs/`models/`) at
all before this release, and auto-update's download/swap path is only ever a same-file overwrite —
see above — so there's nothing there yet for it to keep current. `LarisVMS.Node` and
`LarisVMS.NodeUpdater` bumped to 0.157.0 in lockstep. A recorder with no rebuilt/re-installed package
keeps recording exactly as before — AI detection is additive and every camera's other detection paths
are unaffected — it just won't offer AI detection until reinstalled; from the *next* release onward,
an already-Vision-equipped node picks up a newer Vision Service exe the same automatic way it already
picks up a newer Node exe. New `DetectedObjectCategory` table, new columns on
`MotionSpan`/`Camera`/`Node`, and new `NodeBuildVersion` Vision* columns — database migration
required (`deploy.ps1` applies it automatically unless run with `-SkipMigrations`).

## [0.156.1] - 2026-08-23

### Fixed

- **Every single page load threw `ArgumentNullException: Value cannot be null. (Parameter 'ClientId')`
  once 0.154.0 shipped** — reported live, not just an Entra sign-in attempt: `AuthenticationMiddleware`
  builds every registered `IAuthenticationRequestHandler` scheme's options on *every* request (to check
  which scheme, if any, owns the current request path), which includes the "EntraID" OpenIdConnect
  scheme regardless of whether Entra sign-in is enabled. Two compounding bugs, both fixed:
  - `EntraOidcOptionsConfigurator` was registered as `IConfigureNamedOptions<OpenIdConnectOptions>`
    in DI — but `OptionsFactory<T>`'s constructor only takes `IEnumerable<IConfigureOptions<T>>`, and
    the container resolves by the exact registered service type, not by every interface the
    implementation happens to satisfy. Registered under the wrong interface meant `Configure()` never
    ran at all, leaving `ClientId` at `OpenIdConnectOptions`' own `null` default. Now registered as
    `IConfigureOptions<OpenIdConnectOptions>`.
  - Even with that fixed, an unconfigured deployment (no `EntraSsoSettings` row, or a blank ClientId)
    would still fail validation, since the configurator fell back to an *empty* string rather than a
    placeholder — `OpenIdConnectOptions.Validate()` requires ClientId non-empty unconditionally. Now
    falls back to a syntactically valid placeholder GUID, same "harmless placeholder, never reached by
    a real sign-in while unconfigured" reasoning `Authority`'s own fallback already used (Login.cshtml
    only offers the "Sign in with Microsoft" button once `EntraSsoSettings.IsEnabled` is true).

Web-only, no node change.

## [0.156.0] - 2026-08-23

### Fixed

- **Some snapshots stalled forever on Play, spamming the console with repeated "tile stalled...
  recovering" / "recovering... reloading" pairs.** The existing stall watchdog (added to catch a
  seek into an instant that will never be buffered) kept reloading at the *identical* stalled
  instant every time, looping under its own 3s cooldown rather than resolving. It now tracks
  consecutive recoveries at the same target: after the 3rd attempt in a row it falls back once to
  that segment's own start, and if still stuck, gives up with a permanent "Playback stalled at this
  point — try scrubbing elsewhere." message instead of looping indefinitely. The root cause of the
  underlying stall itself (most plausibly a node-side I/O hang on a specific segment file, or a
  `Segment.DurationMs`/real-encoded-length drift the same class as the v0.119.0 thumbnail-502 fix)
  is not yet confirmed — this is a bounded mitigation, not a structural fix; flagged for a future
  pass once real reproduction logs are available.

### Added

- **Snapshots search: filter by single camera, all cameras, a camera group, or a saved view**
  (previously only single-camera or all). `ITimelineService.GetSnapshotsAsync` now takes a camera-id
  set instead of one optional id. A new `ICameraGroupService.GetCameraIdsInSubtreeAsync` resolves a
  selected group to itself and every descendant group's cameras — the same `MaterializedPath`-prefix
  cascade `CameraAccessService` already uses for a Group-scoped access grant — and a selected view
  resolves via the existing `ViewLayout.CameraIds` helper plus `IViewService.GetVisibleToAsync`. No
  `CameraAccess` scoping was added to the new modes, staying consistent with the existing
  single-camera filter, which has never had it either.
- **Playback timeline is taller and auto-hides on mobile.** On a phone-width viewport the timeline
  canvases render taller (44px, up from the desktop 30px) for easier pinch-to-zoom, and the timeline
  strip overlays the video grid instead of pushing it down — reusing the same relocate-and-overlay
  CSS shape the existing fullscreen timeline already uses, rather than a new layout. It fades out
  after 5 seconds of no touch/mouse activity and reappears on the next touch, governed by a new
  "Auto-hide timeline" toggle (phone-only, default on) persisted per-user through the existing
  `UserPreference` mechanism, so it's remembered across devices and logins.

Web-only, no node change.

## [0.155.0] - 2026-08-23

### Fixed

- **Changing an already-recording camera's ONVIF Device Service URI never took effect — the camera
  kept recording (and live-viewing) from the old source until deleted and re-added.** Reported live:
  pointing a camera at another camera's device and back left it permanently stuck serving the
  *other* camera's stream. Root cause: `NodeWorker.Reconcile()`'s Main-stream branch never compared
  a camera's current RTSP URI against the one its already-running `CameraRecorder` was started
  with — only a Privacy-mask or segment-length change ever triggered a restart. The Sub/adaptive-
  stream branch (`ReconcileLiveSub`) already did this correctly; the Main-stream branch now runs the
  same signature-and-restart check, so an already-recording camera picks up a changed URL on its
  next reconcile (~30s) instead of needing a delete-and-re-add.

**LarisVMS.Node change — install-node.ps1 re-run needed on every recorder.**

## [0.154.0] - 2026-08-23

### Added

- **M20 pass 3: "Sign in with Microsoft" (Entra ID).** Starting with Entra rather than the roadmap's
  other identity item (LDAP sync) since this deployment already has a Microsoft Graph email provider
  configured (M15 pass 2) — an Entra tenant and app-registration workflow already exist here; LDAP
  assumes an on-prem Active Directory this deployment shows no sign of having.
  - **Sign-in only, never registration.** This app disabled self-registration app-wide (M14,
    `RegistrationDisabledMiddleware`) specifically because a self-created account gets no role and
    there's no invite/approval step. The packaged Identity UI's default external-login callback
    assumes the opposite — first sign-in for any identity falls through to "confirm your email to
    finish creating an account." Two Identity pages are overridden locally (`Areas/Identity/Pages/
    Account/Login`, `.../ExternalLogin` — Razor Pages' own page-override convention, the same
    mechanism `_ViewStart.cshtml` already used to carry this app's layout onto the packaged pages) so
    a first-ever Entra sign-in is matched by email against an **existing**, admin-provisioned
    `ApplicationUser` (`Admin → Settings → Users`) and linked via ASP.NET Core Identity's own
    `AspNetUserLogins` table — no new table needed. No match means rejected with a clear message, not
    silently registered.
  - New `Admin → Settings → Security` section: enable/disable, Tenant ID, Client ID, Client Secret
    (encrypted at rest, same `SecretProtection` pattern every other credential in this app uses). The
    "Sign in with Microsoft" button only appears once enabled — `Login.cshtml`'s own override filters
    the external-scheme list, independent of the OIDC scheme being registered in the pipeline at all
    times.
  - **Takes effect immediately, no app restart** — a real ASP.NET Core options-pattern subtlety: an
    `IConfigureNamedOptions<OpenIdConnectOptions>` reads `EntraSsoSettings` from the database at
    sign-in time rather than once at boot, but the options pattern caches a named options instance
    after its first resolution regardless, so on its own this would still only run once per process.
    The Security page's own save handler explicitly evicts the cached entry
    (`IOptionsMonitorCache<OpenIdConnectOptions>.TryRemove`) after saving, forcing the next sign-in
    attempt to re-resolve with the freshly saved row.
  - **Deliberately out of scope, flagged not silently skipped**: no Entra group→Role mapping (that's
    the separate LDAP-sync roadmap item's own scope) — roles are still assigned manually on the Users
    page, same as every account today; single-tenant only, matching the Graph email provider's own
    shape; no account-linking UI, since first-sign-in auto-link already covers this app's actual use
    case.

**New package reference**: `Microsoft.AspNetCore.Authentication.OpenIdConnect` — despite most
authentication handlers shipping in the ASP.NET Core shared framework, this one doesn't and needs an
explicit `PackageReference` (added to `Directory.Packages.props`).

Web-only, no node change.

## [0.153.0] - 2026-08-23

### Added

- **Field-level help text collapses behind an ℹ️ icon (hover or click to reveal) instead of always
  sitting under the field**, across every admin/settings page and `Cameras/Edit` where it had piled up
  enough to make the form itself read as more help text than form: `Cameras/Edit`, `Admin > Settings >
  Branding/Recording/Storage and Retention/Nodes/Logs/Events/Security/LiveView/Cameras/Backups`,
  `Admin > Settings > Permissions > Roles`, and `Admin > Alerts > Edit`. New `help-popover.js`
  (Bootstrap Popover, `trigger: 'hover focus'` — a click already focuses the icon, so this covers both
  asks from one config without needing a separate outside-click dismiss handler) sources each popover's
  content from a same-page `.help-text-source` element via an explicit `data-help-target` id, not DOM
  proximity — needed because a few fields (Recording mode override, in particular) already had several
  conditional help blocks stacked after one input. Loaded globally in `_Layout.cshtml`, inert on any
  page with no `.help-icon` elements. Genuinely actionable warnings (a missing Motion zone/schedule
  window/event rule) stay as visible, always-shown text — only the plain explanatory blocks collapse.

### Changed

- **`Cameras/Edit` no longer edits group membership** — it now shows an existing camera's current
  groups as plain badges (built-in "All Cameras" excluded, same as every other "current groups" display
  in this app) with a link to `Cameras/Groups`, which already owns adding/removing a camera to/from a
  group via its "Manage cameras" popup. Removes the multi-select, its site-grouped `<optgroup>`
  rendering, and the `SetCameraGroupsAsync` call from this page's save handler entirely — the "Add
  camera" flow no longer offers an initial group either (every new camera still gets the built-in "All
  Cameras" group automatically, per `CameraService.AddAsync`).

Web-only, no node change.


## Archived history

Only the 10 most recent versions are kept in this file. Older entries are archived in full below
(nothing summarized or dropped), newest first, at most 20 versions per file:

- [v0.144.0 – v0.152.0](changelog-archive/v0.144.0-to-v0.152.0.md) (2026-08-23)
- [v0.131.0 – v0.143.0](changelog-archive/v0.131.0-to-v0.143.0.md) (2026-08-21 – 2026-08-23)
- [v0.111.0 – v0.130.0](changelog-archive/v0.111.0-to-v0.130.0.md) (2026-08-19 – 2026-08-21)
- [v0.91.0 – v0.110.0](changelog-archive/v0.91.0-to-v0.110.0.md) (2026-08-17 – 2026-08-19)
- [v0.81.1 – v0.90.0](changelog-archive/v0.81.1-to-v0.90.0.md) (2026-08-15 – 2026-08-17)
- [v0.61.0 – v0.81.0](changelog-archive/v0.61.0-to-v0.81.0.md) (2026-08-14 – 2026-08-15)
- [v0.41.0 – v0.60.0](changelog-archive/v0.41.0-to-v0.60.0.md) (2026-08-11 – 2026-08-14)
- [v0.21.1 – v0.40.0](changelog-archive/v0.21.1-to-v0.40.0.md) (2026-08-09 – 2026-08-11)
- [v0.1.0 – v0.21.0](changelog-archive/v0.1.0-to-v0.21.0.md) (2026-08-08 – 2026-08-09)
