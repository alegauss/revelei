# Improvements

## Block A — Project foundation

## Block B — Model management

## Block C — Inference and Real-ESRGAN end-to-end flow

## Block D — Editor: comparison, history and quality modes

## Block E — Face restoration and background removal

## Block F — Delivery: README, benchmarks and installers

## Block G — Phase 2: denoising

## Block H — Editor: basic edits and toolbar

### §PB68 Invert colours

The owner listed "crop, invert, flip" on 2026-10-23. Flip is PB66, so invert is read
here as inverting the colours: each RGB sample becomes its complement, alpha kept, which
turns a scanned colour or black-and-white negative into a positive. It is an exact
operation on the 16-bit samples, so inverting twice gives back the same bytes, and it is
a version in the history like the other edits. Left as an idea until the owner says
whether "invert" meant this or mirroring, which PB66 already covers; if it meant
mirroring, this line is retired.

## Block I — Model manager: catalog, updates and hardware fit

### §PB76 Measured speed replaces the estimate

Every run already logs its duration (`OperationLog`). Keep, per model, quantisation and
provider, the median of the last few runs with the input size (megapixels for the photo
operations, resolution and steps for generation) and the peak memory, in a small JSON
file beside the settings.

The Models card then shows "Measured here: 14 s per 1024x1024 image" instead of the
estimate, and the verdict is recomputed from it. A Test speed button runs the model once
on a fixed input (the model's own test image, or a fixed prompt and seed for generation)
so a person can measure before relying on it; the bench tool (`tools/Revelei.Bench`)
uses the same code path.

An idea for now: its shape depends on what the generation engine reports per step, which
the engine spike in block J settles.

### §PB84 Publish the first signed catalog

PB70 left the app able to read schema 2 and take a fetched catalog only when it is
signed and not older than the embedded copy (`ModelCatalog` in Revelei.Inference), and
left the scripts able to write one (`build-manifest.py --sequence`) and sign it
(`sign-catalog.py`). Nothing is published, and the app compiles in neither key nor URL.

What the owner does, once:

1. `sign-catalog.py --new-key %USERPROFILE%\.revelei\catalog-key.pem`, kept out of the
   repository and backed up: losing it means the next release must ship a new public key.
2. Compile the printed public key and the stable catalog URL
   (`resolve/main/catalog.json` in alegauss/photo-boost-models) into `ModelCatalog`, with a
   test that the published file verifies against that key.
3. Build with `--sequence 1`, sign, and have `publish-models.py` upload catalog.json and
   catalog.json.sig to that path; record the sequence in published-models.json.
4. Embed that catalog as the floor in place of the schema 1 manifest, so
   `ModelDownloads.Embedded.Sequence` is the replay floor the refresh compares against.

Then the Models panel (PB75) gets Check now with the catalog's date, which runs
`ModelCatalog.RefreshAsync` and rebuilds the cards, never at launch, and an "Update
automatically" switch, off by default, that queues every card offering Update after a
refresh.

### §PB85 A quantisation picker on each card

PB75's card shows the quantisation `HardwareFit.Recommend` picked (or the one installed)
and its verdict, and Download fetches that one. A person who wants the larger, slower
one on an 8 GB card, or the smaller one to save disk, cannot choose it.

Add a picker to each card that lists every quantisation the catalog gives the model,
each with its size and its own `HardwareFit.Judge` verdict, the recommended one first
and marked. `ModelCardRow` carries the choice; Download enqueues it
(`DownloadQueue.Enqueue` already takes the quantisation). A model already installed in
one quantisation shows the others as a switch: download the new one, then remove the old
(for a diffusion model the shared files the two have in common stay,
`ModelDownloads.Remove`).

Today's ONNX models list fp32 alone in the catalog, so the picker only appears where the
catalog lists more than one; the winwright case needs a schema 2 fixture catalog with
two quantisations, served by the existing fixture server.

## Block J — Generate an image from a prompt

### §PB77 Spike: which engine runs generation inside Revelei

The goal is that a person installs Revelei and nothing else: no Python, no ComfyUI.
Three candidates:

| Engine | For | Against |
| --- | --- | --- |
| **stable-diffusion.cpp** (MIT, C/C++, ggml) | One native DLL called by P/Invoke; GGUF quantisations; `--offload-to-cpu`; Z-Image, Qwen-Image (2512 and 2.1), FLUX.2 klein, SD3.5 and SDXL supported; Vulkan backend for any GPU, CUDA for NVIDIA | Native code to ship and keep up to date; offload on Vulkan unverified |
| ONNX via Windows ML (what runs today) | Already in the app, DirectML on any GPU | No RAM offload: the transformer must fit in video memory; few diffusion exports |
| Embedded Python with PyTorch/diffusers | Every model, day one | Gigabytes of runtime, NVIDIA-centric, a second process to manage |

The spike, in `spike/diffusion/` like the earlier ones: call stable-diffusion.cpp's C
API from C# and generate 1024x1024 on the RTX 4060 8 GB / 64 GB desk with Z-Image Turbo
Q4_K_M, FLUX.2 klein 4B and Qwen-Image Q4 with offload, on the Vulkan and the CUDA
builds. Record time per image, peak video memory and RAM, whether a step callback and
cancel work through the API, and the DLL size of each backend.

The expected outcome is stable-diffusion.cpp, Vulkan by default and CUDA as an optional
download for NVIDIA. The spike ends with that decision recorded and the next tasks
re-scoped by it.

### §PB78 A Revelei.Generation layer over the engine

A new project, Revelei.Generation, beside Revelei.Inference and standing on
Revelei.Imaging alone; `LayeringTests` gets its row, and only the app references it.

It wraps the engine PB77 chose behind a small API: `GenerateAsync(GenerationRequest,
IProgress<GenerationProgress>, CancellationToken) -> RgbaImage`, where the request names
the model and quantisation, prompt, negative prompt where the model uses one, width,
height, steps, guidance, sampler and seed, and offload on or off. Progress reports the
stage (loading, encoding the prompt, step n of m, decoding) and, when the engine offers
it, a low-resolution preview of the current step.

The loaded model is kept, as `SessionCache` keeps ONNX sessions, until it is idle or
another model is asked for, because loading a 12 GB model takes longer than one image.
The native handle is owned by one SafeHandle and every call runs on one dedicated
thread, since the engine is not thread-safe.

Tests: the P/Invoke signatures against the header version pinned, a tiny model for an
end-to-end generation in CI where one exists, and the real models skipped with the
reason when `.cache\models` lacks them, as the existing model tests do.

### §PB79 The first generation models in the catalog

Start with the models that fit the owner's RTX 4060 8 GB and cover the range, each
re-checked against its model card before it is listed:

| Model | Why | License to confirm |
| --- | --- | --- |
| Z-Image Turbo (6B) | Good text in images, fast, about 7 GB peak at Q4 | Apache-2.0 |
| FLUX.2 klein 4B | Smallest, 4 steps | Apache-2.0 for the 4B |
| Qwen-Image 2512 (20B) | Best text rendering; runs here only with RAM offload | Apache-2.0 |

Qwen-Image 2.1 is better still but listed as non-commercial research; it is added only
with the non-commercial badge and after its terms are read. FLUX.2 dev stays out
(non-commercial, needs about 40 GB).

`scripts/publish-models.py` and `build-manifest.py` learn schema 2: GGUF files are not
re-exported, so the script records each upstream file at a pinned commit with its size,
SHA-256, role and memory figures (measured in the spike, PB77), pointing at the original
repository where its license allows, as RMBG-1.4 already does, and re-hosting only when
it must. The Python tests cover the new schema.

### §PB80 The Create screen

A Create mode beside the editor, reached from the toolbar and from the empty window
("Open a photo or create one").

Left column: the prompt (multi-line, kept between sessions), a negative prompt shown
only for models that use one, the model picker listing installed generation models with
their verdict (PB72) and a link to the Models panel when none is installed, the aspect
ratio (1:1, 3:2, 2:3, 16:9, 9:16) with the resolution derived from what the model was
trained on, steps and guidance with the model's defaults and a Reset, the seed with a
dice to randomise and a lock to keep, and the number of images.

Centre: the image as it forms, with the step preview PB78 reports, then the result;
Generate turns into Cancel while running, and the status bar says the stage, step n of
m, the elapsed time and, once one step is done, the time left. A run on a model whose
verdict is Runs, slower says so before it starts.

Below: a strip of this session's results; clicking one shows it with its prompt and
settings, and Use these settings copies them back.

The first time Create opens with no generation model, it offers the recommended one for
this PC with its size and verdict, through the same consent pattern as the faces and
background cards.

### §PB81 Running out of memory during generation

`MemoryFailures` already recognises out-of-memory from the ONNX side. The generation
layer gets the same classification for the engine's errors, and an ordered fallback,
each step logged and named in the status bar:

1. Full GPU fails: retry with the transformer offloaded to RAM.
2. Offload fails at this size: retry with VAE tiling (decoding in tiles), which is where most 8 GB cards run out at 1024 px and above.
3. Still failing: say which memory ran out and offer the next quantisation down, or a smaller size, rather than switching to the CPU silently, since a 20B model on the CPU takes many minutes per image.

System RAM is checked before loading against the catalog's figures (PB72), so a model
that cannot fit is refused with the reason before anything loads, instead of Windows
paging the whole machine to a halt. What worked is remembered per model and size, so the
next run starts from it.

### §PB82 From Create into the editor

Each result has Edit, which opens it in the editor as if it were a photo, with the
history starting from the generated image, so Upscale, Restore faces, Remove background,
the basic edits and Export all apply as they do today.

The prompt, negative prompt, model and quantisation, size, steps, guidance, sampler and
seed are written into the exported file: a PNG `tEXt`/`iTXt` chunk (the convention other
generators use, so their tools can read it), and XMP for JPEG and WebP. Opening a file
that carries them in Create fills the settings back, and the editor shows a small
"Generated with ..." line.

Results are also saved automatically to a Generated folder (in Pictures by default,
changeable), named by date and seed, so closing the app never loses an image; the export
flow stays for choosing format and place.

### §PB83 Shipping the engine

The installer (`build/installer.iss`, `build-installer.cmd`) carries the engine build
that runs everywhere, expected to be the Vulkan one from the spike (PB77), with its MIT
license collected by `collect-notices.ps1` into the licenses folder and the About
notices.

A faster backend tied to one vendor, such as CUDA for NVIDIA, is too large to put in
every installer. It becomes a catalog entry of kind `engine` (PB70), downloaded through
the same queue (PB73), verified by SHA-256, and offered by the Models panel only when
the detected GPU can use it, with the measured gain from the spike. The generation layer
picks the best installed backend and falls back to the bundled one if the other fails to
load.

`BuildGateTests` and `InstallerTests` check that the bundled engine DLL is present and
its version matches the header the P/Invoke layer was written against, and the README
gains the requirements and the license of each generation model and of the engine.
