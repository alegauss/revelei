# Shipped Ledger

## Block A — Project foundation

- 🗑 **PB3** **Renderer is not locked down: no contextIsolation, sandboxed preload or restrictive CSP** — abandoned: A native WPF app has no web renderer, so contextIsolation and CSP have nothing to protect.
- 🗑 **PB4** **No typed IPC contract shared by main, preload and renderer** — abandoned: One process replaces main, preload and renderer; the InferenceService API takes the place of IPC.
- ✅ **PB1** **No WPF .NET 10 solution with the Fluent ThemeMode that builds and opens a window** — PhotoBoost.slnx builds App, Imaging, Inference and Render warning-free on .NET 10 x64, and the app opens an empty Fluent window following the system theme.
- ✅ **PB2** **No xunit.v3 test projects, analyzer gate or architecture tests in the build** — run-tests.cmd builds the solution and runs xunit.v3 Imaging, Inference and Architecture tests on Microsoft.Testing.Platform; layering and analyzer-gate rules now fail the run.
- ✅ **PB5** **Unverified which ONNX Runtime package (DirectML, Windows ML, CUDA) runs in a self-contained WPF build** — DirectML 1.24.4 runs exactly in self-contained and single-file publishes and ships; CUDA is rejected and Windows ML is proposed as PB43 (design recorded in `spike/onnx-runtime/README.md`).
- ✅ **PB37** **No design system: Brand, Theme, Appearance and the Fluent page styles from claude-tray are missing** — Brand, Theme, Appearance, Templates and a shared Styles.xaml with the sibling apps' card, header, stat, chip and nav styles now dress the window, and a test refuses colours outside Brand.cs.
- ✅ **PB38** **No winwright Cases project, winwright.json or first case that opens the main window** — The winwright plugin installs at project scope as winwright@winwright, beside roadkeep's, completing the adoption.
- ✅ **PB39** **No command-line flags to open a file, force a provider or point at a models folder** — CommandLine parses the five flags in one place and refuses bad input with exit code 2; --provider already drives detection, proved by a case (design recorded in `docs/COMMAND-LINE.md`).
- ✅ **PB40** **Unverified how to show a 50 MP image on the GPU inside a WPF window with smooth zoom and pan** — A child-HWND flip swapchain shows a 50 MP image with GPU mips in 261 MB and pans and zooms at 60 Hz with no frame over 20 ms, on GPU and WARP (design recorded in `spike/gpu-display/README.md`).
- ✅ **PB57** **The UI cases still work around five winwright gaps that 1.1.4 closed, and the drop zone and Save dialog have no case** — On winwright 1.1.5 a Save as case writes saved.png through the Save dialog and reads it back, green in two guest runs in a row.

## Block B — Model management

- ✅ **PB6** **No verified source URLs for Real-ESRGAN x4plus, GFPGAN v1.4 and RMBG-1.4 weights** — scripts/model-sources.json lists fetched URLs, sizes, SHA-256 and licenses for Real-ESRGAN x4plus and x2plus, GFPGAN v1.4, RMBG-1.4 and the U2-Net fallback.
- ✅ **PB7** **No scripts/download_models.py to fetch the original weights** — scripts/download_models.py fetches the listed weights into .cache/weights, resumes partial files, prints progress, verifies size and SHA-256 and skips valid files.
- ✅ **PB8** **No scripts/export-and-quantize.py to export each model to ONNX with dynamic axes** — export-and-quantize.py now also writes GFPGAN v1.4 at 512x512 and adopts BRIA's RMBG-1.4 ONNX after checking it against PyTorch, each with an FP16 variant kept only above 40 dB.
- ✅ **PB9** **Exported models are not quantized to INT8** — export-and-quantize.py writes int8.onnx for all four models with QUInt8 dynamic quantization, leaving no float Conv or MatMul; INT8 runs slower than FP32, so it buys size only.
- ✅ **PB10** **INT8 models are not validated against FP32 on a test image** — export-and-quantize.py checks INT8 and FP16 against FP32 on each model's upstream sample image, writes PSNR and SSIM to meta.json and fails INT8 below a per-model floor.
- ✅ **PB11** **No models/<name>/{fp32.onnx, int8.onnx, meta.json} layout with SHA256 hashes** — Each .cache/models/<name>/ holds fp32, fp16 and int8 ONNX beside a meta.json with IO, ranges, tiling, SHA-256 per variant, license and validation, held to meta.schema.json and read by ModelMeta.
- ✅ **PB12** **The optional FP32 models have no public location the app can download them from** — The FP32 set is public at alegauss/photo-boost-models with a pinned manifest, and every file downloads anonymously at its SHA-256.
- ✅ **PB51** **The installer carries no models, so a fresh install cannot run any operation** — The installer carries the FP16 set, which the app reads before its downloads folder; every operation ran from it offline (design recorded in `build/stage-models.ps1`).
- ✅ **PB53** **The installer accepts Windows 10 1809, where Windows ML and so every operation cannot run** — The installer refuses Windows older than 10 1903, Windows ML's minimum, and a test holds that floor to the package version it was read for.
- ✅ **PB54** **Face restore on DirectML pastes a flat brown square over each face with the default FP16 GFPGAN** — On a GPU face restore runs GFPGAN's FP32, which the installer now carries, so faces come out right (design recorded in `src/PhotoBoost.Inference/Pipelines/FaceRestore.cs`).
- ✅ **PB13** **The app has no button to download the optional FP32 models with progress and hash verification** — About offers the FP32 set from the pinned manifest, streamed through .part files, SHA-256 checked, skipping files already here or bundled.
- ✅ **PB58** **The installer is 628 MB, 84% of it the models of two operations most photos never use** — The installer carries 100 MB of models; faces and background offer a one-time download with a consent panel, pausable progress, helpful errors and auto-run.
- ✅ **PB60** **A 4x of an 8192 px photo closes the app, and a run's progress sits below a panel taller than the window** — An upscale past 16384 px is refused on the card with what to choose; failures no longer close the app; the run's card is pinned above the operations.
- ✅ **PB59** **No UI case drives a first-use download to the end: progress, pause, a failure and the auto-run are unobserved** — A UI case downloads a stand-in RMBG from a loopback fixture server via --manifest: progress, pause, resume, auto-run and a 404's Try again are now watched.
- ✅ **PB61** **After an undo the run's card still says Done and the status bar still times a result no longer shown** — Undo, redo and opening another photo clear the run's card and the status bar's time, so neither describes a version no longer shown.
- ✅ **PB62** **Remove background says only "Finding the subject…" through a minute of edge matting on a large photo** — Remove background reports the subject, then "Refining the edges: tile N of M" with the time left, counting only the tiles the edge crosses; Cancel lands between tiles.

## Block C — Inference and Real-ESRGAN end-to-end flow

- ✅ **PB14** **Execution provider is neither detected at runtime nor shown in the UI** — ProviderDetector runs a probe session on DirectML on the GPU with the most video memory, falls back to CPU with the reason, and the status bar shows the provider and GPU.
- ✅ **PB15** **No InferenceService queue that runs one inference at a time** — InferenceService runs queued jobs one at a time in order on one background thread, with progress through IProgress, cancellation while queued or between tiles, and failures kept per job.
- ✅ **PB16** **ONNX sessions are not loaded on demand nor released after idle minutes** — SessionCache loads a model variant's session on first use, lends it out by lease, drops the model's other variant on a switch and releases sessions idle for 5 minutes.
- ✅ **PB17** **No preprocessing to and from model tensors, and no unit tests for it** — ImageDecoder now reads any WIC format into RgbaImage upright per EXIF, converted to sRGB, with alpha, grayscale and 16-bit samples kept, tested on JPEG, PNG and WebP.
- ✅ **PB18** **Large images cannot be processed: no tiling with overlap** — Tiler runs a model over 256px tiles with 16px overlap and stitches exact cores, padding odd sizes for even-only models; a neighbour-reading model matches a whole-image run bit for bit.
- ✅ **PB19** **No Real-ESRGAN upscale pipeline for 2x and 4x** — Upscale runs Real-ESRGAN x4plus, or native x2plus for 2x, through the tiler and a ModelLibrary session, with 32 px overlap measured at 45 dB against a whole run and alpha resized bilinearly.
- ✅ **PB20** **UI receives no tile X of Y progress during inference** — Jobs report tile X of Y with a stage and a time-left estimate, throttled to 100 ms with first and last kept, and UpscaleJob queues an upscale wired to that progress.
- ✅ **PB21** **An out-of-memory failure aborts the inference instead of retrying with smaller tiles** — An out-of-memory tile is retried at half size down to 64 px, then the run moves once to a CPU session; a lost GPU goes straight to CPU, and each step is logged.
- ✅ **PB22** **Inference time and the provider used are not logged per operation** — Every queued upscale appends a JSON line with model, variant, provider, adapter, size, tiles, retries, wall time and outcome to a rotating log under LOCALAPPDATA.
- ✅ **PB25** **Results cannot be exported as PNG, JPG or WebP** — An export card saves the result or the opened image as PNG, JPEG or WebP with quality, warns before a transparent JPEG with a background choice, and --export writes after each run.
- ✅ **PB23** **No screen to open an image by drag and drop or file picker** — The window opens on a drop zone with an Open button; a drop, the dialog or --open decode off the UI thread and show the photo upright with its name and size, or say why not.
- ✅ **PB24** **No operations panel to run an upscale and pick 2x or 4x** — An operations panel runs a 2x or 4x upscale on the queue with tile progress, time left and cancel, shows the result, logs it and sums it in the status bar; missing models disable it.
- ✅ **PB41** **Initial tile size ignores the free video memory of the GPU** — An upscale with no tile given starts at the largest of 128 to 1024 px that fits 80% of free VRAM or RAM at 6x the largest activation, measured on DirectML; 416 px on the RTX 4060.
- ✅ **PB44** **Decoding a wide-gamut image to sRGB is untested, so a wrong colour transform would pass the suite** — A Display P3 PNG fixture with a self-built profile decodes to the sRGB that the colorant maths and LittleCMS predict, and one with a broken profile decodes unconverted.
- ✅ **PB45** **Exports drop the source photo's camera, date, copyright and GPS metadata, with no choice to keep it** — A Keep camera details box, off by default, copies camera, lens, exposure, dates, copyright and GPS into a JPEG export with Orientation 1; PNG and WebP say they cannot.
- ✅ **PB42** **Cases have no tiny model fixture, so an inference case would need the real model downloads** — Nearest-neighbour 4x/2x stand-ins under cases/fixtures/models; a case upscales, exports byte-exact, compares at 25 and undoes offline (WW556 workaround).
- ✅ **PB26** **Open, upscale and export flow has no winwright case proving it end to end** — cases/upscale.cases.json opens, upscales 4x on the tiny model, reads progress and the run chip, exports and matches bytes; real model run by hand on DirectML (12.4 s).
- ✅ **PB43** **App cannot use the TensorRT-RTX or OpenVINO providers that Windows ML installs** — --provider winml runs on Windows ML's TensorRT-RTX or OpenVINO and fetches missing ones; auto stays DirectML (design recorded in `src/PhotoBoost.Inference/WindowsMLProviders.cs`).
- ✅ **PB52** **TensorRT-RTX builds its engine for 10 to 17 s at every session creation, so auto cannot prefer it** — Auto runs the upscalers on TensorRT-RTX once their engines are built, the rest on DirectML; a big 4x upscale took 5.4 s, not 47 s (design recorded in `src/PhotoBoost.Inference/ProviderDetector.cs`).
- ✅ **PB55** **On --provider winml, 2x upscale, denoise and refined background removal fail, and engines build inside the first run** — Windows ML builds its engines in the background at startup, and models TensorRT-RTX cannot build run on DirectML instead of failing.
- ✅ **PB56** **A tiled operation throws on a GPU whose DXGI driver answers no video memory budget, as VMware's does** — A GPU that refuses the video memory query reads as unknown, so tiled operations start from the recommended tile; all C# suites pass on the VMware guest.

## Block D — Editor: comparison, history and quality modes

- ✅ **PB28** **No operation history with undo and redo** — Each operation adds a version to ImageHistory, spilled to temp past 1 GiB; undo and redo by button, Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z, and operations chain on the version shown.
- ✅ **PB27** **No before and after slider comparison** — The photo now shows on a D3D11 surface with wheel zoom and drag pan, and compares result with original by divider or side by side, the divider settable through UI Automation.
- ✅ **PB29** **No UI choice between the bundled FP16 models and the downloaded FP32 High quality set** — A Quality card chooses Standard (bundled FP16) or High quality (FP32), offered once downloaded and noting it is faster on the CPU.

## Block E — Face restoration and background removal

- ✅ **PB30** **No face detection and alignment to feed GFPGAN crops and paste results back** — YuNet (MIT, opencv_zoo f12e127) adopted with meta.json; FaceDetection decodes and suppresses; FaceAlignment fits FFHQ 512, crops and pastes back feathered.
- ✅ **PB31** **No GFPGAN face restoration pipeline** — FaceRestore aligns each YuNet face, runs GFPGAN, pastes back by strength, optionally upscales first; Restore faces card states its terms; 2 faces in 3.1 s on DirectML.
- ✅ **PB32** **No background removal pipeline producing a transparent image** — RemoveBackground resizes to RMBG's 1024 square, stretches the mask as BRIA does and sets it as alpha; Remove background card states BRIA's terms; giraffes cut out on DirectML.
- ✅ **PB47** **Background removal leaves faint specks and a soft upscaled edge, with no refinement at the photo's resolution** — A guided filter refines only the mask's unsure band and floors it: faint pixels on example_input.jpg 58248 to 4529, no halo; edge band widened 13481 to 17724, filed.
- ✅ **PB48** **Background removal's edge stays soft where subject and background have the same brightness** — Refine edges runs ViTMatte on RMBG's unsure band in 1024 px tiles, narrowing it from 8699 to 7958 pixels (design recorded in `src/PhotoBoost.Inference/Pipelines/Matting.cs`).

## Block F — Delivery: README, benchmarks and installers

- ✅ **PB35** **Release build produces no self-contained Windows installer** — build\build-installer.cmd publishes self-contained single-file R2R with natives beside, collects package notices, builds a per-user Inno installer; installed app ran DirectML, uninstalls clean.
- ✅ **PB46** **App has no About page listing each model's license and the third-party notices** — An About panel from the status bar lists the version, each installed model with its meta.json license, and the installer's notices; a winwright case reads it.
- ✅ **PB49** **PhotoBoost's site does not say it is a Japode product or link to the brand's site** — The nav carries a "part of Japode" chip to the storefront at the origin's root, the footer a "More from Japode" link and the line that PhotoBoost is a Japode product, in English and Portuguese.
- ✅ **PB50** **PhotoBoost's site declares japode.github.io as its origin, which now only redirects to japode.com** — The site's canonical links, og:url, hreflang, sitemap and robots.txt name https://japode.com/photo-boost/, the address GitHub serves it at under the organization's domain.
- ✅ **PB33** **No README in Portuguese on running, generating models, Python and hardware requirements** — README.md, in Portuguese, covers installing, building and running, generating the models, the installer, tests, providers, hardware and each model's license.
- ✅ **PB34** **No table comparing the FP16 and FP32 sets' size, inference time and quality on GPU and CPU** — tools\PhotoBoost.Bench measures FP16 against FP32 on CPU and DirectML, and the README carries its table, which caught PB54.

## Block G — Phase 2: denoising

- ✅ **PB36** **No denoising operation** — SCUNet real-PSNR (Apache-2.0) vendored with ≤5-D attention after DirectML crashed on upstream's graph; tiled Denoise card; 320 px photo in 7.4 s on DirectML.

## Block H — Editor: basic edits and toolbar

- ✅ **PB63** **The editor has no toolbar: opening, saving, undo and every basic edit live in the side panel or nowhere** — A toolbar above the photo holds Open, Save as, Undo and Redo, with shortcut tooltips and an overflow menu, off without a photo (design recorded in `src/PhotoBoost.App/MainWindow.xaml`).
- ✅ **PB64** **Once a photo is open there is no way to open another: the Open button and the drop zone go with the empty screen** — Another photo opens from the toolbar, Ctrl+O or a drop on the photo; unsaved versions are asked about first, and a bad file is refused over the photo, which stays.
- ✅ **PB65** **A photo cannot be rotated: a sideways scan or a wrong EXIF orientation stays sideways** — Rotate left and right turn the photo a quarter from the toolbar, Ctrl+Shift+R or Ctrl+R, as exact pixel moves; each turn is a version that undoes.
- ✅ **PB66** **A photo cannot be mirrored horizontally or vertically** — Flip horizontal and Flip vertical mirror the photo from the toolbar as exact pixel moves, each a version that undoes; two flips give back the same bytes.
- ✅ **PB67** **A photo cannot be cropped, so the subject cannot be framed before or after an upscale** — Crop (or C) opens an editor on the photo with handles, aspect presets, its pixel size, Apply and Cancel; Apply cuts those pixels into a version that undoes.
- ✅ **PB69** **Dragging a crop corner or edge mostly does nothing: the press lands beside the handle and moves or pans instead** — A crop corner or edge is grabbed within 12 screen pixels at any scale, an edge along its whole length, and the cursor shows the resize or move a press will do.

## Block I — Model manager: catalog, updates and hardware fit

- ✅ **PB70** **The downloadable models are fixed at build time, so a new model or a fixed file needs a new Revelei release** — build-manifest.py writes a schema 2 catalog, sign-catalog.py signs it, the app verifies it; publishing is PB84 (design recorded in `src/Revelei.Inference/ModelCatalog.cs`).
- ✅ **PB71** **An installed model is never known to be out of date, so a fixed or improved version is never offered** — An update is staged in .update, verified, then moved over the old files; a failed one leaves the model as it was (design superseded: a staging folder replaced versioned folders and a pointer).
- ✅ **PB72** **Nothing says whether a model will run well, slowly or not at all on this PC before its gigabytes are downloaded** — Each quantisation gets a verdict (runs well, slower, CPU only, does not fit) with its reason, and the best one is recommended (design recorded in `src/Revelei.Inference/HardwareFit.cs`).
- ✅ **PB73** **A multi-gigabyte download starts without checking free disk, cannot be queued, and is read twice to hash it** — Files models share are kept once by SHA-256 in .shared and removed with the last model that uses them.
- ✅ **PB74** **Downloaded models can only live in %LOCALAPPDATA%, so a 15 GB model fills the system drive** — A chosen models folder is saved in settings and read at launch, models move there by copy, check and delete, and a missing one is said (design recorded in `src/Revelei.Inference/ModelsFolderMove.cs`).
- ✅ **PB75** **Models are managed from a flat text list inside About, with one Download everything button** — A Models panel lists each catalog model as a card with its verdict, version and queued actions; About keeps a summary (design superseded: Check now went to PB84, the picker to PB85).
- ✅ **PB76 (photo operations, from the log)** **The hardware verdict stays an estimate after a model has run here; this PC's own speed is never shown** — A model that has run here shows its measured seconds per megapixel on its card, the median of its recent runs in the operation log.

## Block J — Generate an image from a prompt

- ✅ **PB77** **No engine in Revelei can run a text-to-image diffusion model, let alone one bigger than 8 GB of video memory** — stable-diffusion.cpp generates 1024 px images from C# on the 8 GB desk, a 12 GB model by RAM offload; CUDA on NVIDIA, Vulkan elsewhere (design recorded in `spike/diffusion/README.md`).
