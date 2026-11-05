# Improvements

## Block A — Project foundation

## Block B — Model management

## Block C — Inference and Real-ESRGAN end-to-end flow

## Block D — Editor: comparison, history and quality modes

## Block E — Face restoration and background removal

## Block F — Delivery: README, benchmarks and installers

### §PB86 PB86 — Publishing the next release as Revelei

From the portfolio review of 30/10/2026 (visibility, `produtos/revelei.md`), where the
author confirmed that the next release ships as Revelei. The main branch was renamed
after v0.3.0, and `build/installer.iss` already writes `Revelei-Setup-<version>.exe`,
but every published release is still PhotoBoost: the asset is
`PhotoBoost-Setup-0.3.0.exe`, and the v0.3.0 notes point at the README of
`Japode/photo-boost`. The README tells a visitor to download a file that does not exist.
Anyone arriving from the site or a post meets two names for one product.

The next release (with the 21 commits since v0.3.0) is published as Revelei: the
installer asset is `Revelei-Setup-<version>.exe` with its `SHA256SUMS`, the notes say
what Revelei is and link to `alegauss/revelei` and its site, and nothing in them names
`Japode/photo-boost`. The upgrade over an installed PhotoBoost keeps working (the AppId
is unchanged and the old shortcuts are removed, as `installer.iss` already does), and
that is checked once on a machine with v0.3.0 installed. The v0.3.0 notes get one line
saying the product is now Revelei, with the link.

Done when the latest release's installer is `Revelei-Setup-<version>.exe`, the README's
download instruction matches it, and an upgrade from PhotoBoost 0.3.0 lands in Revelei.

### §PB87 PB87 — Describing generation in the README and the site

From the portfolio review of 30/10/2026 (visibility, `produtos/revelei.md`), where the
author decided that generating an image from a prompt is part of what Revelei is
announced as. Block J put generation in the main branch (Z-Image Turbo, FLUX.2 klein,
Qwen-Image), yet the README opens on "It does four things" and the site on photo
restoration; neither says that Revelei can also create an image, which changes what the
product is and which communities it fits (the r/StableDiffusion reader in particular).

The README's first paragraph and the operations table add generation, with its models,
their licences and its hardware needs (video memory, RAM offload, the speed this PC
measures); the site gets a section with a real generated image and the prompt that made
it. The promise must not run ahead of the release: this lands with the release whose
installer can generate on a fresh install (PB83), so the text never describes a feature
the download lacks.

Done when the README and the site describe generation, and a fresh install of the
release they describe can generate an image after downloading one model.

### §PB88 PB88 — Naming Modelado as the sibling

From the portfolio review of 30/10/2026 (visibility, `estrategia/portfolio.md`). Revelei
and Modelado form one line, "AI on your PC, nothing leaves it": a better photo, and a
photo turned into a 3D model, both running locally, both with no account and nothing
uploaded. Modelado's README already cites its sibling (under the old PhotoBoost name,
which its own roadmap fixes); Revelei says nothing about Modelado, so a visitor who
liked one never finds the other.

The README gets a short "From the same author" mention near the end, and the site a card
in the place other product sites keep for related tools, naming Modelado with its
one-line promise ("a photo becomes a 3D model you can turn, print and share") and the
link to alegauss.github.io/modelado. The wording is the same shared line on both
products, so the pair reads as a family, and the two pages keep a matching design.

Done when the README and the site link to Modelado with the shared line, and Modelado's
pages link back to Revelei by its current name.

### §PB89 PB89 — Distributing through the Microsoft Store

From the portfolio review of 30/10/2026 (visibility, `estrategia/portfolio.md`). The
installer is not signed, so SmartScreen warns on the first download, and the people who
restore family photos look for apps in the Microsoft Store, not on GitHub. The decision
for all of the author's Windows apps is the Store, which signs the MSIX package itself,
instead of buying a certificate. claude-tray tests the path first (visibility VS58);
this line follows what that test learns.

Revelei's own points to check against the Store's rules: models of hundreds of megabytes
up to 15 GB are downloaded after install, to a folder the person chooses (PB74), which
an MSIX app must be allowed to write; the generation engine's GPU backend is meant to
arrive like a model (PB83), and the Store restricts downloading executable code; GFPGAN
and RMBG-1.4 are licensed for non-commercial use only, which the listing must state; and
the per-user, no-admin install stays. If one of these rules out MSIX, the outcome is
written down instead.

Done when Revelei is listed in the Store with its downloads working, or the reason it
cannot be is recorded here and in the product sheet.

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

Every run already logs its duration (`OperationLog`). For the photo operations
`MeasuredSpeed` reads it: the median of the last few runs per model, variant and
provider, by megapixel. Generation needs its own measure (resolution and steps, peak
memory), logged the same way once its engine reports them.

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

### §PB81 Running out of memory during generation

`MemoryFailures` already recognises out-of-memory from the ONNX side. The generation
layer gets the same classification for the engine's errors, and an ordered fallback,
each step logged and named in the status bar:

The engine already does the first two on its own (spike/diffusion/README.md): auto-fit
streams what does not fit from RAM, and a VAE decode that runs out retries in tiles; the
layer names them when the log says so.

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
that runs everywhere, the Vulkan one, 85 MB unpacked (spike/diffusion/README.md); CUDA,
914 MB zipped and 12 times faster at 1024 px on NVIDIA, is the download below, with its
MIT license collected by `collect-notices.ps1` into the licenses folder and the About
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
