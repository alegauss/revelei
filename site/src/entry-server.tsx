import { renderToString } from "react-dom/server";
import { App } from "./App";
import { langFor } from "./routes";

// The build-side render: scripts/prerender.mjs calls it once per route and writes the result
// into that route's index.html, with the head patched from the route table below.
export { ROUTES, canonicalUrl, jsonLd, outputDir, OG_IMAGE } from "./routes";

export function render(path: string): string {
  return renderToString(<App lang={langFor(path)} />);
}
