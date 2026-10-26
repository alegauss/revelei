// The app's icon, from the site's two marks, so the window, the exe and the installer wear the
// same picture as the tab and the nav.
//
// A Windows .ico holds one picture per size and the shell picks the closest, so each size is
// drawn from the mark made for it rather than scaled from one: 16 to 32 px from favicon.svg,
// whose shapes survive that size, and 48 px up from icon.svg, with the sun, the handle and the
// sparkle. Each entry is a PNG, which the .ico format has allowed since Windows Vista.
//
//   npm run app-icon      writes ../src/Revelei.App/Revelei.ico
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { Resvg } from "@resvg/resvg-js";

const siteDir = join(dirname(fileURLToPath(import.meta.url)), "..");
const out = join(siteDir, "..", "src", "Revelei.App", "Revelei.ico");
const small = readFileSync(join(siteDir, "public", "favicon.svg"), "utf8");
const large = readFileSync(join(siteDir, "public", "icon.svg"), "utf8");

const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
const images = sizes.map((size) => ({
  size,
  png: new Resvg(size <= 32 ? small : large, { fitTo: { mode: "width", value: size } }).render().asPng(),
}));

// ICONDIR (6 bytes), one ICONDIRENTRY (16 bytes) per image, then the images themselves.
const header = Buffer.alloc(6 + 16 * images.length);
header.writeUInt16LE(0, 0); // reserved
header.writeUInt16LE(1, 2); // type: icon
header.writeUInt16LE(images.length, 4);
let offset = header.length;
images.forEach(({ size, png }, i) => {
  const at = 6 + 16 * i;
  header.writeUInt8(size >= 256 ? 0 : size, at); // width; 0 means 256
  header.writeUInt8(size >= 256 ? 0 : size, at + 1); // height
  header.writeUInt8(0, at + 2); // palette colours: none
  header.writeUInt8(0, at + 3); // reserved
  header.writeUInt16LE(1, at + 4); // colour planes
  header.writeUInt16LE(32, at + 6); // bits per pixel
  header.writeUInt32LE(png.length, at + 8);
  header.writeUInt32LE(offset, at + 12);
  offset += png.length;
});

const ico = Buffer.concat([header, ...images.map((i) => i.png)]);
writeFileSync(out, ico);
console.log(`app icon: ${out}  ${sizes.join(", ")} px  (${(ico.length / 1024).toFixed(0)} kB)`);
