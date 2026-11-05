// The Store package's logos (PB90), from the same two marks as the app icon (app-icon.mjs), so
// Start, the taskbar and the Store wear the picture the exe does.
//
// The manifest names three logos; the shell asks the 44 px one for other sizes on the taskbar,
// in Start and in Alt+Tab, and finds those only through the resources.pri build-msix.ps1 makes.
// Each target size is written twice, the plain variant and its altform-unplated twin: a logo with
// no unplated variant is drawn on a square in the accent colour (claude-tray's T416). Up to
// 32 px the shapes come from favicon.svg, as the .ico's do.
//
//   npm run msix-assets   writes ../build/msix/Assets/*.png
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { Resvg } from "@resvg/resvg-js";

const siteDir = join(dirname(fileURLToPath(import.meta.url)), "..");
const out = join(siteDir, "..", "build", "msix", "Assets");
const small = readFileSync(join(siteDir, "public", "favicon.svg"), "utf8");
const large = readFileSync(join(siteDir, "public", "icon.svg"), "utf8");

// Kept in step with MsixTests in tests/Revelei.Architecture.Tests, which reads this folder.
const logos = [
  ["Square44x44Logo.png", 44],
  ["Square150x150Logo.png", 150],
  ["StoreLogo.png", 50],
];
for (const size of [16, 24, 32, 48, 256]) {
  logos.push([`Square44x44Logo.targetsize-${size}.png`, size]);
  logos.push([`Square44x44Logo.targetsize-${size}_altform-unplated.png`, size]);
}

mkdirSync(out, { recursive: true });
for (const [file, size] of logos) {
  const png = new Resvg(size <= 32 ? small : large, { fitTo: { mode: "width", value: size } }).render().asPng();
  writeFileSync(join(out, file), png);
}
console.log(`msix assets: ${out}  (${logos.length} logos)`);
