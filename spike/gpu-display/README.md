# GPU image display spike (PB40)

How to show a 50 MP image on the GPU inside Revelei's WPF window with smooth zoom and pan.
This folder is a record of the answer, outside `Revelei.slnx`; no gate builds it.

## The host was already decided

quickshell measured the three ways a WPF app can present a D3D swapchain (its QS4,
`D:\Git\alegauss\quickshell\docs\measurements\host-probe.md`): a **child HWND** with its own
flip-model swapchain reached one refresh interval click-to-pixel (13.8 ms min, 16.0 p10), while
WPF's `D3DImage` never read below 32.4 ms at p10 and WinUI's `SwapChainPanel` never below
28.4 ms. WPF's popups and modals are HWND-backed and drew correctly over the child HWND. That
holds for Revelei as much as for a terminal, so `D3DImage` was not rebuilt here. What this
spike adds is the part quickshell never had: a very large texture.

## What was measured

`GpuDisplay` hosts a child HWND (`HwndHost` over a `static` window) in a WPF window, creates a
D3D11 device and a `FlipDiscard` swapchain at the surface's physical size, uploads an
8660 × 5774 (50 MP) RGBA8 texture with a full mip chain, and draws it with one fullscreen
triangle that samples it trilinearly through a pan and zoom transform. It then plays 600 frames
of a path zooming from fit to 8× (1:1 is about 6.8× here) and back while panning, at vsync, and
the same path uncapped.

```
dotnet build -c Release
bin\Release\net10.0-windows\win-x64\GpuDisplay.exe [--warp] [--frames N]
```

Desk: RTX 4060 8 GB (driver 610.74), 60 Hz display, surface 2534 × 1529 px, 2026-10-20.

| | RTX 4060 | WARP (CPU) |
| --- | --- | --- |
| Upload of level 0 | 64 ms | 29 ms |
| Mip generation | 1.6 ms | 48 ms |
| Video memory added | 261 MB | none (system memory) |
| Frame time at vsync, median / p99 / max | 16.67 / 16.92 / 17.07 ms | 16.68 / 16.90 / 16.90 ms |
| Frames over 20 ms | 0 of 600 | 0 of 120 |

Uncapped, the hardware path stays at 60 fps: a flip-model present without the tearing flag
waits on the compositor, so there is no headroom figure to read, only the absence of a dropped
frame. WARP sustained 104 fps uncapped at this surface size.

## Answer

- **Display through a child HWND with a flip-model swapchain** (`Revelei.Render`), not WPF's
  `Image`. A 50 MP image costs about 260 MB of video memory as RGBA8 with mips and pans and
  zooms without a frame over one refresh interval, on the GPU and on WARP alike.
- **Show at 8 bits.** The display is 8-bit; a 16-bit source converts once on upload (RGBA16
  would double the 260 MB to no visible end). The `RgbaImage` stays 16-bit for processing.
- **Generate mips on the GPU** (1.6 ms) and sample trilinearly, so the fitted view of a 50 MP
  photo does not alias.
- **Airspace**: nothing WPF draws can sit over the surface unless it is HWND-backed. Popups,
  menus and tooltips are; ordinary overlays are not. So the comparison slider's divider and
  labels (PB27) are drawn by the surface's own shader, and the Fluent controls sit beside it.
- **WARP is the fallback** when no hardware device opens; it kept up at this size.
- **Automation**: a swapchain publishes nothing to UI Automation, and winwright cannot capture a
  Mica window (WW552), so the surface's host element carries its state as automation
  properties: image size, zoom and divider position, for cases to read.
