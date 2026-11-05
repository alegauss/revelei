# Roadmap (active backlog)

## Block A — Project foundation

## Block B — Model management

## Block C — Inference and Real-ESRGAN end-to-end flow

## Block D — Editor: comparison, history and quality modes

## Block E — Face restoration and background removal

## Block F — Delivery: README, benchmarks and installers

- 📋 **PB86** (deps: —) **Every published release is still PhotoBoost, and the README sends people to a Revelei installer that does not exist** — The next release ships under the new name, so the page, the release and the README finally say the same thing. → §PB86
- 📋 **PB87** (deps: PB83) **The README and the site describe four photo operations and never say Revelei can create an image from a prompt** — Generation changes what the product is and who it reaches, and the author decided it is part of what Revelei is announced as. → §PB87
- 📋 **PB88** (deps: —) **A visitor who likes Revelei never learns that Modelado turns a photo into a 3D model the same offline way** — The two apps are one line, AI on your PC with nothing leaving it, and each page should lead to the other. → §PB88
- 📋 **PB89** (deps: PB90 ⏳, PB91, PB92, PB93) **Every first download meets a SmartScreen warning, and the Store, where photo apps are looked for, has no Revelei** — claude-tray is already in the Store through the same account, so what is left is the package, the listing and the two policy gaps. → §PB89
- ⏳ **PB90** (deps: —) **No MSIX package exists, so there is nothing to upload to the Store and nothing to install locally to test that path** — The VM gate, then Create's host process and the restart after moving models, seen in the registered package. → §PB90
- 📋 **PB91** (deps: PB90 ⏳) **A Store release means typing the listing, uploading screenshots and the package by hand in Partner Center** — claude-tray drives the same account through the submission API, so one script can send each release. → §PB91
- 📋 **PB92** (deps: PB83) **A Store install would download the CUDA engine's native DLLs after certification, code the Store bars apps from fetching** — PB83 plans the vendor backend as a catalog download, which suits the installer and breaks Store policy on dynamic code. → §PB92
- 📋 **PB93** (deps: —) **Create makes whatever image a prompt asks for, with no filter and no way to report a result, which Store review checks** — Store certification asks apps that generate content with AI for safeguards and a report path, and the age rating depends on them. → §PB93

## Block G — Phase 2: denoising

## Block H — Editor: basic edits and toolbar

## Block I — Model manager: catalog, updates and hardware fit

- 🛠 **PB76** (deps: PB72 ✅, PB78 ✅, PB79 ✅, PB80 ✅) **The hardware verdict stays an estimate after a model has run here; this PC's own speed is never shown** — A Test speed button, and generation's own measure once its engine reports per step. → §PB76
- 📋 **PB85** (deps: PB75 ✅) **A model card offers only the recommended quantisation; a larger or smaller one cannot be chosen** — Picking quality against speed and disk is the person's call, and the verdict per quantisation is already computed. → §PB85

## Block J — Generate an image from a prompt

- 💭 **PB81** (deps: PB78 ✅) **A generation that runs out of video or system memory fails instead of retrying in a way that fits** — The photo operations already fall back to smaller tiles and then the CPU (PB21); generation needs the same, or an 8 GB card fails on the models it can run. → §PB81
- 📋 **PB82** (deps: PB80 ✅) **A generated image cannot be upscaled, edited or exported, and its prompt and seed are lost once saved** — Revelei's own operations are what make a generated image usable (a 4x upscale, a removed background), and without the prompt and seed a good image cannot be reproduced. → §PB82
- 💭 **PB83** (deps: PB73 ✅, PB78 ✅) **The installer carries no generation engine, so a fresh install could not generate even after a model is downloaded** — The goal is one download for everything: the native engine ships with Revelei, with its license, and the larger GPU-specific backend arrives like a model. → §PB83

## Done when — PB76

- **Test speed measures a model, generation included, through the bench's code path** A
  card's Test speed runs the model once on a fixed input (its test image, or a fixed
  prompt and seed), logs it, and the card shows the measurement; the bench tool calls
  the same code.

## Done when — PB90

- **The registered package generates an image and restarts after a models move** In the
  VM, once it is free: run-tests.cmd green with MsixTests and PackageIdentityTests, then
  the -Register package creates one image (Revelei.Generation.Host.exe starts from the
  package folder) and comes back up with package identity after the models folder is
  moved.

## Non-goals

- **Cloud inference or external APIs** Every model runs locally; the network is used
  only to download model files on first launch.
- **Models committed to the repository** Weights are fetched and generated by scripts
  and stored in userData/models, never in git.
- **Builds for macOS, Linux, web or mobile** Revelei is Windows-only, so it can use
  DirectX, DirectML and the WPF stack shared with the sibling apps.
- **A WinUI 3 user interface** claude-tray, quickshell, pportal and winwright's in-app
  package are all WPF with the Fluent ThemeMode, so WPF reuses their design and tests as
  they are.
