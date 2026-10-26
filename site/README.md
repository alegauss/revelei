# Revelei site

The public site, in English (`/revelei/`) and Portuguese (`/revelei/pt/`): a
self-contained Vite + React 19 + TypeScript + Tailwind v4 workspace. `dotnet build` and
`run-tests.cmd` neither build nor need it.

The chrome is the family's: the nav, footer, buttons and theme toggle come from
`@viglet/viglet-design-system/site`, the colours from its warm tone (`data-tone="warm"`, the
`--vg-*` tokens), and the prerender and social card from its `/vite` helpers, as on the
claude-tray site. The page between the nav and the footer is Revelei's own and is built
around the experience rather than the family's feature grids: it opens on a before/after the
reader can drag, then a dark "studio" to try each operation, the four moves from photo to
result, and only then privacy, speed, details and the download.

## Commands

```
npm install        # once
npm run dev        # dev server at /revelei/ (and /revelei/pt/)
npm run build      # tsc, client, SSR, then the prerender: both pages, og.png, the touch icon
npm run preview    # serve the built dist/
```

`npm run build` is the gate. The prerender writes each language's page with its own title,
description, canonical, `<html lang>` and hreflang links, by replace-or-throw, so a template
that lost an anchor fails the build.

## Where things live

| Path | What |
|---|---|
| `src/content.ts` | **Every word, in both languages.** The two dictionaries share one type, so a section cannot exist in only one; images, sizes and timings are written once beside them |
| `src/i18n.tsx` | The page's language, as a React context the sections read |
| `src/routes.ts` | The two routes and their metadata, read by the prerender |
| `src/components/Compare.tsx` | The before/after comparer: drag anywhere, or the keyboard through a hidden range input |
| `src/components/` | Hero, Studio, Journey, the later sections, and the chrome |
| `public/icon.svg` | The mark: a photo split by the comparison divider, coarse pixels before it and the smooth scene after it, on the clay tile |
| `public/favicon.svg` | The mark at tab size, with what does not survive 16 px taken out |
| `public/demo/` | The example images, every one a real Revelei result |
| `scripts/prerender.mjs` | The prerender, `og.png` and `apple-touch-icon.png` |

## The example images

Every "after" image was made by the Revelei app itself on 2026-10-20 (0.1.0, High quality,
DirectML on an RTX 4060 laptop GPU), driven through its window with `--export`; the timings on
the page are those runs, model loading included. The photos are public domain, from Wikimedia
Commons:

| Example | Photo | Before | After |
|---|---|---|---|
| Upscale | [Red fox (8385104980)](https://commons.wikimedia.org/wiki/File:Red_fox_(8385104980).jpg), USFWS Alaska | Reduced to 25% (375 × 334, box filter), then bicubic 4× as the ordinary enlargement | Upscale 4× |
| Denoise | [Great Horned Owl (4819455826)](https://commons.wikimedia.org/wiki/File:Great_Horned_Owl_(4819455826).jpg), USFWS | A 960 × 720 crop with simulated high-ISO noise (shot, read and half-resolution colour noise, seed 42); the page says it is simulated | Denoise |
| Faces | [Portrait of a young woman wearing a feathered hat](https://commons.wikimedia.org/wiki/File:Full-length_portrait_of_a_young_woman_wearing_a_feathered_hat_LCCN2006689599.jpg), Library of Congress | As published; a 260 px window on the face | Restore faces, strength 100% |
| Background | [Gray Wolf (6545954933)](https://commons.wikimedia.org/wiki/File:Gray_Wolf_(6545954933).jpg), USFWS Midwest Region | As published | Remove background, Refine edges on |

The research test images the model pipeline validates against (Real-ESRGAN's, GFPGAN's, BRIA's)
are not used here: their rights are unclear, and some show real people or cartoon characters.
