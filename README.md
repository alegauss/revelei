# Revelei

Revelei is a Windows app that improves photos with AI models running on your own computer.
Nothing is uploaded. It does four things:

| Operation | Model |
| --- | --- |
| Upscale 2× and 4× | Real-ESRGAN x2plus and x4plus |
| Restore faces | YuNet (detection) and GFPGAN v1.4 (restoration) |
| Remove the background | RMBG-1.4, its edge refined by ViTMatte |
| Remove noise | SCUNet (real, PSNR) |

The interface is WPF. Inference runs on Windows ML's ONNX Runtime, on the GPU through DirectML
or on the CPU. A result can be compared with the original, undone, and exported as PNG, JPEG or
WebP.

## Requirements

- **Windows 10 version 1903 (19H1) or later, or Windows 11, x64.** Windows ML does not run on
  older versions, and there is no build for ARM, x86, macOS or Linux.
- **A DirectX 12 GPU** to run on the graphics card. Without one, or if the GPU fails the startup
  check, everything runs on the CPU, more slowly, and the status bar says why.
- **Memory:** each tile's size is picked from the free memory (video memory on the GPU, RAM on
  the CPU), from 128 to 1024 px. For Real-ESRGAN x4, the heaviest model, that works out to about
  1.1 GB for 128 px tiles, 3.1 GB for 256 px and 6.2 GB for 384 px. With less memory the tile is
  simply smaller, and running out of memory mid-run retries with smaller tiles, then on the CPU.
- **Disk space:** about 100 MB for the models the installer carries, plus 325 MB once face
  restoration has been used and 267 MB once background removal has.

## Install

Download `Revelei-Setup-<version>.exe` from the
[releases](https://github.com/Japode/revelei/releases). It installs for the current user,
without administrator rights, and carries the models for upscaling and denoising, so those work
offline from the first launch.

**Restore faces** and **Remove background** download their models the first time you use them:
GFPGAN v1.4 (325 MB), and RMBG-1.4 with ViTMatte (267 MB). Before anything is fetched, the card
says how large the download is, and its button opens a panel listing each file, its source, where
it is saved and its license. The download runs while the rest of the app keeps working. It can be
paused and resumed, and a dropped connection is retried. Each file is checked against its SHA-256
before the app uses it, and once the models are verified the operation runs by itself. Only the
models are downloaded: photos never leave the PC. The About panel lists what was downloaded, can
remove it, and can download everything at once for offline use.

The **Quality** card at the top of the operations panel chooses between **Standard**, the FP16
models the installer carries, and **High quality**, the FP32 models, which About downloads with
everything else. On a GPU the two run at about the same speed, and on the CPU FP32 is also about
twice as fast.

## Run from source

You need the **.NET SDK 10.0.100** or a later feature band (`global.json`).

```
dotnet build Revelei.slnx
.\run.cmd
```

`.\run.cmd` builds Debug and opens a menu of manual test scenarios, each with an image. Given
arguments, it starts the app with them instead, for example
`.\run.cmd --open photo.jpg --provider cpu`. The command-line options are in
[docs/COMMAND-LINE.md](docs/COMMAND-LINE.md). Call it as `.\run.cmd`: a bare `run.cmd` can find
another one on the PATH first.

### Where the models live

The app looks for each model in this order:

1. the `models` folder beside the executable, which the installer fills;
2. `%LOCALAPPDATA%\Revelei\models`, where downloads go.

`--models <folder>` replaces both with one folder. `run.cmd` uses `.cache\models` when that
folder exists, which is where the export script writes. No model is stored in the repository.

## Build the models

The models are downloaded from their original sources and converted to ONNX by two Python
scripts. Tested with **Python 3.13**. The CPU build of PyTorch is enough: the export needs neither
CUDA nor a GPU.

```
python -m venv .venv
.venv\Scripts\python -m pip install torch==2.14.1 --index-url https://download.pytorch.org/whl/cpu
.venv\Scripts\python -m pip install -r scripts\requirements.txt
```

1. **Download the weights** (about 1 GB, into `.cache\weights`):

   ```
   .venv\Scripts\python scripts\download_models.py
   ```

   Standard library only. Each file is checked against the size and SHA-256 recorded in
   `scripts\model-sources.json`, an interrupted download resumes where it stopped, and a file
   already correct is not downloaded again.

2. **Export to ONNX** (into `.cache\models`):

   ```
   .venv\Scripts\python scripts\export-and-quantize.py
   .venv\Scripts\python scripts\export-and-quantize.py --only gfpgan-v1.4
   ```

   For each model it writes `fp32.onnx`, `fp16.onnx` and `int8.onnx`, with a `meta.json` the app
   reads. Each variant is compared with PyTorch and with FP32 on the model's own test image. An
   export that differs past the tolerance, or an INT8 below its PSNR floor, fails the script
   instead of writing a wrong model. ViTMatte has no FP16, and YuNet has FP32 only.

The models the app downloads (the FP32 set, and GFPGAN, RMBG-1.4 and ViTMatte on first use) are
published with `scripts\publish-models.py` to
[alegauss/photo-boost-models](https://huggingface.co/alegauss/photo-boost-models), with a manifest
pinned to a commit. The app embeds that manifest, so it knows every size and hash without the
network. RMBG-1.4 is not re-hosted; its entry points at BRIA's own repository.

## Build the installer

```
build\build-installer.cmd
```

It publishes Release (win-x64, self-contained), collects the packages' licenses, copies the
bundled models from `.cache\models` (upscaling, denoising and the face detector) and compiles the
installer with Inno Setup 6. A missing
model stops the build. `build\update-release.cmd <version>` sets the version, runs the tests and
calls `build-installer.cmd`.

## Tests

```
run-tests.cmd
```

It builds the solution with warnings as errors and the performance analyzers on, then runs every
test application in `tests\`, the winwright UI cases (`cases\*.cases.json`, skipped with the
reason on a machine whose screen cannot be observed) and the Python tests in `scripts\tests`. The
result is green only if everything ran: a missing suite, or no Python on the PATH, counts as a
failure. Tests that use the real models run when `.cache\models` exists and are skipped, with the
reason, when it does not.

The UI cases take over the desktop while they run, so run the suite in a VMware guest with
winwright's `run-tests-vm.cmd -Tree <this repository>`.

## Execution providers

By default (`--provider auto`) inference runs on DirectML, on the GPU with the most video memory,
and falls back to the CPU if that does not run. On an NVIDIA RTX card where Windows ML has
installed TensorRT-RTX, auto uses TensorRT-RTX for upscaling only. Each model's engine takes 10 to
17 s to build. It is built in the background on first launch and kept in
`%LOCALAPPDATA%\Revelei\engines`, and until then upscaling stays on DirectML. Measured on an
RTX 4060, a 4× upscale of a 933 × 1405 photo went from 47 s to 5.4 s. Background removal ran three
times slower on TensorRT-RTX, which is why the other operations stay on DirectML.
`--provider winml` uses the Windows ML provider (TensorRT-RTX or OpenVINO) for every model it can
build.

## FP16 and FP32, measured

The installer carries the FP16 set; FP32 is High quality. The table below comes from
`tools\Revelei.Bench`. It runs each operation through the app's own pipelines, on the model's
own test photo, in both variants, on the CPU and on DirectML:

```
dotnet run --project tools\Revelei.Bench -c Release -- --out bench.md
```

"First run" includes loading the session; "Run" is the faster of the two runs after it. Peak
memory is sampled every 10 ms from before the session loads: video memory on DirectML, the
process's private memory on the CPU. PSNR and SSIM compare the FP16 result with FP32 on the same
provider (the alpha channel only, for background removal). Upscaling and denoising use 256 px
tiles.

Measured on 2026-10-20: Intel Core i7-14700; NVIDIA GeForce RTX 4060, 8 GB.

| Operation | Provider | Variant | File | First run | Run | Peak memory | PSNR vs FP32 | SSIM vs FP32 |
| --- | --- | --- | --: | --: | --: | --: | --: | --: |
| Upscale 4× | CPU | FP16 | 32.2 MB | 7.53 s | 5.73 s | 1143 MB | 70.9 dB | 1.0000 |
| Upscale 4× | CPU | FP32 | 63.9 MB | 2.92 s | 2.06 s | 1151 MB | — | — |
| Upscale 4× | DirectML | FP16 | 32.2 MB | 1.99 s | 0.21 s | 413 MB | 60.1 dB | 0.9999 |
| Upscale 4× | DirectML | FP32 | 63.9 MB | 1.11 s | 0.16 s | 520 MB | — | — |
| Upscale 2× | CPU | FP16 | 32.2 MB | 3.08 s | 0.92 s | 191 MB | 71.9 dB | 1.0000 |
| Upscale 2× | CPU | FP32 | 64.0 MB | 1.41 s | 0.52 s | 282 MB | — | — |
| Upscale 2× | DirectML | FP16 | 32.2 MB | 2.02 s | 0.06 s | 146 MB | 59.7 dB | 1.0000 |
| Upscale 2× | DirectML | FP32 | 64.0 MB | 1.17 s | 0.04 s | 225 MB | — | — |
| Restore faces | CPU | FP16 | 162.5 MB | 3.77 s | 2.66 s | 1974 MB | 76.8 dB | 1.0000 |
| Restore faces | CPU | FP32 | 324.6 MB | 1.98 s | 1.54 s | 1446 MB | — | — |
| Restore faces | DirectML | FP16 | 162.5 MB | 1.19 s | 0.08 s | 1306 MB | 12.7 dB | 0.6177 |
| Restore faces | DirectML | FP32 | 324.6 MB | 1.10 s | 0.13 s | 1551 MB | — | — |
| Remove background | CPU | FP16 | 84.1 MB | 0.90 s | 0.54 s | 1256 MB | 84.8 dB | 1.0000 |
| Remove background | CPU | FP32 | 168.0 MB | 0.84 s | 0.54 s | 2322 MB | — | — |
| Remove background | DirectML | FP16 | 84.1 MB | 0.89 s | 0.09 s | 412 MB | 74.8 dB | 1.0000 |
| Remove background | DirectML | FP32 | 168.0 MB | 0.69 s | 0.07 s | 861 MB | — | — |
| Remove noise | CPU | FP16 | 35.6 MB | 5.63 s | 3.57 s | 958 MB | 82.6 dB | 1.0000 |
| Remove noise | CPU | FP32 | 69.5 MB | 6.06 s | 3.82 s | 578 MB | — | — |
| Remove noise | DirectML | FP16 | 35.6 MB | 10.16 s | 2.59 s | 314 MB | 66.0 dB | 1.0000 |
| Remove noise | DirectML | FP32 | 69.5 MB | 8.03 s | 1.41 s | 573 MB | — | — |

What the table shows:

- **FP16 is half the size and, except in one case, the same image:** 59.7 to 84.8 dB against
  FP32, SSIM 0.9999 or higher.
- **The exception is GFPGAN in FP16 on DirectML, which comes out wrong** (12.7 dB): each face
  becomes a flat square. The same file is right on the CPU. So GFPGAN is only ever downloaded in
  FP32, and the app restores faces with it even in Standard.
- **On the CPU FP32 is faster** for upscaling and face restoration (2.8× for the 4× upscale),
  which is what the FP32 download is for. On DirectML the two variants take about the same time,
  except for denoising, and FP16 uses less video memory.

## Model licenses

| Model | License |
| --- | --- |
| Real-ESRGAN x4plus and x2plus | BSD-3-Clause |
| GFPGAN v1.4 | Apache-2.0, with parts under **non-commercial** licenses (StyleGAN2, DFDNet) |
| YuNet 2023mar | MIT |
| RMBG-1.4 | BRIA's license, **non-commercial use**; commercial use needs a license from BRIA |
| ViTMatte small | Apache-2.0 (code MIT) |
| SCUNet | Apache-2.0 |

GFPGAN v1.4 and RMBG-1.4 may only be used non-commercially, so check each license before using a
result commercially. The app's About panel lists each installed model with the license from its
`meta.json`, and the installation's `licenses` folder holds the third-party packages' licenses.
Each model's source and license link are in `scripts\model-sources.json`.
