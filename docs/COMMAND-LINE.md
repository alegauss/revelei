# Command line

Revelei starts like any Windows app with no arguments. These flags exist so an automated UI
case can reach a state without a file dialog or drag and drop, and they work for anyone.

```
Revelei.exe [--open <image>] [--models <folder>] [--provider auto|winml|dml|cpu]
               [--export <file>] [--quality standard|high] [--manifest <file>]
               [--engine <folder>|fake]
```

Each flag takes its value after a space or an equals sign (`--provider cpu` or `--provider=cpu`)
and may be given once. An unknown flag, a missing value or a value outside the list is refused:
Revelei says which one in a message box and exits with code 2.

| Flag | What it does |
| --- | --- |
| `--open <image>` | Opens this image at start instead of the empty screen. |
| `--models <folder>` | Reads and downloads models in this folder alone, instead of the `models` folder the installer put beside the app followed by `%LOCALAPPDATA%\Revelei\models`. |
| `--provider auto\|winml\|dml\|cpu` | `auto` (the default) runs on DirectML on the GPU with the most video memory and falls back to the CPU, and where TensorRT-RTX runs on that GPU the upscalers use it once their engines are built in the background; `winml` first tries a provider Windows ML installed for this PC (TensorRT-RTX on NVIDIA, OpenVINO on Intel), then does what `auto` does, and fetches a provider not installed yet in the background for the next start; `dml` asks for DirectML; `cpu` never tries a GPU. The status bar shows what was chosen. |
| `--export <file>` | When the run finishes, writes the result here (PNG, JPG or WebP by extension) and stays open. |
| `--manifest <file>` | Downloads models from this manifest file instead of the one the app carries. Besides https, it may name plain http on this machine's loopback, which is how the UI cases serve a stand-in model from a local server. |
| `--engine <folder>\|fake` | Generates images with the stable-diffusion.cpp build in this folder instead of the one installed beside the app. `fake` starts a stand-in that samples in steps and draws a gradient, which is how the UI cases reach the Create screen on a desk with no GPU and no model. |
| `--quality standard\|high` | Starts with Quality on Standard, the FP16 models the installer carries, or on High quality, the FP32 ones the Models panel downloads. `compact`, its earlier name, still means standard. |

Every flag takes effect. `--quality high` keeps High quality chosen even before the FP32 models are
downloaded, and each operation then names the model it is missing.
