# Diffusion engine spike (PB77)

Which engine runs text-to-image generation inside Revelei, with nothing else installed, on a
card with less video memory than the model. This folder is a record of the answer, not live
code: it is outside `Revelei.slnx` and no gate builds it.

## How it was measured

`DiffusionSpike` calls stable-diffusion.cpp's C API from C# by P/Invoke (`Native.cs`, the
structs of `include/stable-diffusion.h` laid out field for field) and generates one image. It
prints the load time, every progress callback, the time per image, the peak video memory
(nvidia-smi sampled every 250 ms, over the baseline other processes held) and the peak RAM
(the process's peak working set), and it can cancel at a given step.

```
dotnet build -c Release
cd <backend folder>          (ggml finds its backend DLLs in the working directory)
DiffusionSpike.exe <backend folder> <diffusion gguf> <llm gguf> <vae> out.png
               [--size 1024x1024] [--steps 8] [--cfg 1] [--offload] [--cancel-at 3] [--flash on|off]
```

Engine: stable-diffusion.cpp release `master-1024-77b72a2` (MIT), the Windows `vulkan` and
`cuda12` builds plus `cudart-sd-bin-win-cu12`. Nothing here is committed: the DLLs and the
models come from GitHub and Hugging Face by hand.

Machine: Windows 11 Pro 26300, RTX 4060 8 GB (driver 610.74) and Intel UHD 770, 64 GB RAM.
Other desktop apps held 1.5 to 5.4 GB of the RTX's memory during the runs, as on any desk.
Prompt "A red fox sitting in fresh snow at sunrise, ...", seed 42. Measured 2026-10-28.

## Results

1024x1024 unless noted. Steps are seconds per step after the first; the first step also
loads the weights to the GPU.

| Model (files) | Backend | Steps | Per step | Whole image | Peak VRAM over baseline | Peak RAM |
| --- | --- | --- | --- | --- | --- | --- |
| Z-Image Turbo Q4_K (3.9 GB) + Qwen3 4B Q4_K_M (2.5 GB) + FLUX VAE | CUDA | 8 | 2.1 s | 37 s | 2.3 GB | 3.3 GB |
| same | Vulkan | 8 | 25 to 30 s | 242 to 416 s | 3.1 GB | 7.0 GB |
| same, 512x512 | CUDA | 4 | 0.47 s | 11 s | 4.7 GB | 3.2 GB |
| same, 512x512 | Vulkan | 4 | 1.1 s | 26 s | 1.4 GB | 7.1 GB |
| same, 768x768 | Vulkan | 3 | 1.85 s | - | - | - |
| FLUX.2 klein 4B Q4_0 (2.5 GB) + Qwen3 4B Q4_K_M + FLUX.2 VAE | CUDA | 4 | 1.2 s | 20 s | 2.0 GB | 3.1 GB |
| Qwen-Image Q4_0 (11.9 GB) + Qwen2.5-VL 7B Q4_K_M (4.7 GB) + its VAE, CFG 2.5, offload | CUDA | 20 | 9.3 s | 234 s | 3.4 GB | 15.3 GB |
| same, 512x512, 4 steps | Vulkan | 4 | 17 to 21 s | 201 s | 2.5 GB | 17.9 GB |

All five 1024 images are correct and look like the prompt.

Backend size, unpacked: Vulkan 85 MB (`stable-diffusion.dll` 36 MB, `ggml-vulkan.dll` 40 MB,
the CPU variants 9 MB); CUDA 1.2 GB (`ggml-cuda.dll` 348 MB plus cuBLAS, cuBLASLt and the
CUDA runtime, 790 MB). Zipped they are 30 MB and 914 MB.

## What else the runs showed

- **The C API is enough.** `new_sd_ctx`, `generate_image` and the `*_init` defaults work from
  C# with no shim. `sd_set_progress_callback` reports every sampling step, and also the
  tensor loading and the VAE tiles as steps of their own, so a progress bar has to tell the
  sampling pass apart (`sd_get_preview_info` or the step totals).
- **Cancel works, and is not reported as such.** `sd_cancel_generation(ctx, SD_CANCEL_ALL)`
  from the progress callback stopped the run after the current step (11.6 s instead of
  37 s); `generate_image` then returns false with "sampling failed", so the caller has to
  remember that it asked.
- **The structs change from build to build.** `sd_ctx_params_t` and `sd_img_gen_params_t`
  gained and moved fields between recent releases. Revelei must pin one release, keep its
  struct definitions beside it, and test them on every engine update (`*_to_str` prints
  the parameters back, which is how this spike checked its layout).
- **Offload is automatic.** With no backend named, the engine's auto-fit plan places the
  diffusion model on the GPU and the text encoder and VAE in RAM, and streams what does not
  fit; `params_backend = "cpu"` (sd-cli's `--offload-to-cpu`) keeps every weight in RAM. A
  VAE decode that does not fit is retried with tiling on its own.
- **Vulkan on this NVIDIA card falls off a cliff at 1024x1024**: 25 to 30 s a step, against
  1.1 s at 512 and 1.85 s at 768, with the same single-segment graph and video memory to
  spare; flash attention off made it worse (68 s). The Vulkan device list also includes the
  UHD 770, which auto-fit left alone.
- **ggml loads its backend DLLs from the working directory or the executable's folder**, not
  from the DLL search path: an app that keeps them in a subfolder must load
  `ggml-*.dll` explicitly or set the working directory.
- A native crash would take the process with it; nothing here crashed, but a 12 GB model on
  a short-RAM machine is where one would.

## Decision

**stable-diffusion.cpp, called through its C API, is the engine.** One native DLL per
backend, GGUF quantisations, RAM offload that makes a 12 GB model run on an 8 GB card, a
step callback and a cancel. ONNX through Windows ML cannot offload, and an embedded Python
runtime is gigabytes and a second process for the same models.

**The backend follows the GPU, not a default.** On NVIDIA it is CUDA: Vulkan is 12 times
slower at the size people generate at. CUDA's 914 MB download is too large for the
installer, so it arrives like a model, offered when an NVIDIA card is found. Vulkan (30 MB)
ships with the installer for AMD and Intel GPUs and as the fallback, and the CPU build
covers a machine with neither.

**The engine runs out of process.** Pinning the struct layout and a crash that would take
the window are both reasons to put generation in a small host process the app talks to,
which can be restarted, killed on cancel if the cooperative cancel does not land, and
updated with its engine; PB78 builds that seam.
