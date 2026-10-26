// The prerender, the social card and the touch icon, after the client and SSR builds.
//
// prerenderSite writes the rendered page into dist/index.html, patching the <head> by
// replace-or-throw, and writes sitemap.xml and robots.txt from the same route table.
// ogCardSvg draws the family's social card in the warm tone with the Revelei mark, and
// renderOgCard rasterises it to dist/og.png at 1200 x 630. The 180 px touch icon is rasterised
// from public/icon.svg, since an Apple device will not take an SVG for it.
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { Resvg } from "@resvg/resvg-js";
import { ogCardSvg, prerenderSite, renderOgCard } from "@viglet/viglet-design-system/vite";

import { render, ROUTES, canonicalUrl, outputDir, OG_IMAGE } from "../dist-server/entry-server.js";

const siteDir = join(dirname(fileURLToPath(import.meta.url)), "..");
const distDir = join(siteDir, "dist");
const icon = readFileSync(join(siteDir, "public", "icon.svg"), "utf8");

const card = await renderOgCard({
  // English, the default page's language; one card serves both, as og:image is one URL.
  svg: ogCardSvg({
    eyebrow: "Revelei · for Windows",
    title: ["Your photos, the way"],
    titleAccent: "you remember them.",
    line: ["Upscale, denoise, restore faces and remove the background,", "with AI, on your own PC. Nothing is uploaded."],
    mark: icon,
    tone: "warm",
  }),
  out: join(distDir, "og.png"),
});
console.log(`og-image: dist/og.png  ${card.width}x${card.height}  (${(card.bytes / 1024).toFixed(0)} kB)`);

const touch = new Resvg(icon, { fitTo: { mode: "width", value: 180 } }).render().asPng();
writeFileSync(join(distDir, "apple-touch-icon.png"), touch);
console.log(`touch icon: dist/apple-touch-icon.png  180x180  (${(touch.length / 1024).toFixed(0)} kB)`);

const written = prerenderSite({
  distDir,
  routes: ROUTES,
  render,
  canonicalUrl,
  outputDir,
  ogImage: OG_IMAGE,
  git: { cwd: siteDir },
  log: console.log,
});

// The language half the shared prerender does not know about: each page's <html lang>, and the
// hreflang links that tell a search engine the two pages are one page in two languages.
// Replace-or-throw, like the prerender: a template that lost an anchor must fail the build.
const alternates = [
  ...ROUTES.map((r) => `<link rel="alternate" hreflang="${r.htmlLang}" href="${canonicalUrl(r.path)}" />`),
  `<link rel="alternate" hreflang="x-default" href="${canonicalUrl("/")}" />`,
].join("\n    ");
for (const route of ROUTES) {
  const file = join(distDir, outputDir(route.path), "index.html");
  let html = readFileSync(file, "utf8");
  for (const [from, to] of [
    ['<html lang="en" data-tone="warm">', `<html lang="${route.htmlLang}" data-tone="warm">`],
    ["<!--hreflang-->", alternates],
  ]) {
    if (!html.includes(from)) throw new Error(`${file} lost its ${from} anchor`);
    html = html.replace(from, () => to);
  }
  writeFileSync(file, html);
}

console.log(`prerender: ${written.length} route(s) in ${ROUTES.map((r) => r.htmlLang).join(" and ")}, sitemap.xml and robots.txt written to dist/`);
