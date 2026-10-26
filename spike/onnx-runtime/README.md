# ONNX Runtime package spike (PB5)

Which ONNX Runtime package runs in a self-contained x64 publish of this app, and what each
provider costs. This folder is a record of the answer, not live code: it is outside
`Revelei.slnx` and no gate builds it.

## How it was measured

`OrtSpike` builds a Real-ESRGAN-shaped model in memory (`TinyModel.cs`: Conv 3x3, LeakyRelu,
nearest Resize x2, Conv 3x3, dynamic height and width, 2,261 bytes), so no model file is
committed. It runs the model on the chosen provider and on CPU, and prints session creation
time, first run, the median of five more runs and the largest difference from the CPU output.

```
dotnet publish -c Release --self-contained -p:Flavor=DirectML -o artifacts\spike\dml
artifacts\spike\dml\OrtSpike.exe dml 512 0 [--placement]
```

`-p:Flavor` is `DirectML` (default), `Cuda` or `WinML`. `--placement` turns on ORT's verbose log,
whose "Node placements" lines show which ops fell back to CPU.

Machine: Windows 11 Pro 26300, RTX 4060 8 GB (driver 610.74) and Intel UHD 770, no NPU, no
CUDA toolkit. Measured 2026-10-19 on a 1x3x512x512 input.

## Results

| Package (version) | Provider | Native size added | Session create | Median run | Max diff vs CPU |
| --- | --- | --- | --- | --- | --- |
| Microsoft.ML.OnnxRuntime.DirectML 1.24.4 | CPU | 36 MB (`onnxruntime.dll` 17.3, `DirectML.dll` 18.5) | 2.5 ms | 35 ms | 0 |
| same | DirectML, RTX 4060 | same | 12 ms | 9 to 12 ms | 7e-7 |
| same | DirectML, UHD 770 | same | 11 ms | 13 ms | 5e-7 |
| Microsoft.ML.OnnxRuntime.Gpu.Windows 1.31.0 | CUDA | 206 MB, plus CUDA 13 and cuDNN 9 not shipped | fails | - | - |
| Microsoft.Windows.AI.MachineLearning 2.1.74 | PREFER_GPU (picked NvTensorRTRTX) | 41 MB (`onnxruntime.dll` 21.7, `DirectML.dll` 18.7, WinML 0.9) plus 25 MB CsWinRT projection | 460 to 510 ms | 2.4 ms | 2e-3 |
| same | DirectML through WinML's ORT 1.24.6 | same | - | 5.5 to 5.9 ms | 5e-7 |
| same | CPU | same | - | 18 ms | 0 |

Whole self-contained publish folder: 127 MB with the DirectML package, 142 MB with Windows ML.

What else the runs showed:

- **DirectML places every node on the GPU.** The verbose log reads "All nodes placed on
  [DmlExecutionProvider]" (fused to 3 nodes); nothing fell back to CPU.
- **Single-file works with DirectML** both ways: natives left beside the exe, and
  `IncludeNativeLibrariesForSelfExtract=true` (one 112 MB exe). The package also copies
  `DirectML.Debug.dll`, two `.pdb` files and `onnxruntime.lib`, about 14.5 MB the installer
  should exclude.
- **The DirectML package has stopped at ORT 1.24.4** while the core package is at 1.31.0,
  which is what sustained engineering looks like in practice.
- **CUDA fails at `AppendExecutionProvider_CUDA`**, with `OnnxRuntimeException` naming
  `cublasLt64_13.dll`. It is catchable, but shipping CUDA means redistributing CUDA 13 and
  cuDNN 9, over a gigabyte, for one GPU vendor.
- **`AppendExecutionProvider_DML` threw 887A0004 (DXGI_ERROR_UNSUPPORTED) once** in about
  thirty runs on a GPU that worked before and after. Provider setup must be inside the
  fallback's try, not only session creation.
- **Windows ML needs no Windows App SDK.** `Microsoft.Windows.AI.MachineLearning` alone works
  unpackaged and self-contained (reg-free WinRT, Windows 10 19H1 or later), with a
  `net10.0-windows10.0.19041.0` target. The first `EnsureAndRegisterCertifiedAsync` took
  57 s while Windows Update fetched the OpenVINO and NVIDIA TensorRT-RTX providers; later
  runs took about 0.5 s. TensorRT-RTX ran the model five times faster than DirectML but
  differed from CPU by 2e-3 (reduced precision), and builds its engine at session creation.

## Recommendation (not yet approved)

Ship **Microsoft.ML.OnnxRuntime.DirectML 1.24.4** now: it runs on any DX12 GPU, is exact
against CPU, starts in milliseconds and needs nothing from Windows Update.

Propose **Windows ML** as the long-term path, behind the same provider abstraction: it is
where Microsoft moved DirectML's successor work, and it brings TensorRT-RTX and OpenVINO
without shipping them. The open questions before switching are the first-run download
(57 s, needs a progress screen and an offline answer), the 2e-3 precision difference on
TensorRT-RTX against the INT8 and FP32 quality check, and the per-model engine build time.
The brief asks for any switch to be proposed and explained before it is made, so the
switch waits for the owner.

**CUDA is rejected** as a shipped provider: the redistributable is too large for one vendor,
and TensorRT-RTX through Windows ML covers NVIDIA without it.
