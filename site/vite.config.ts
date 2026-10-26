import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { siteThemeScript } from "@viglet/viglet-design-system/site";

// GitHub Pages serves a project site under the repository's name, so the site lives at
// https://japode.com/revelei/ and every asset path carries the prefix. Written here
// and in src/routes.ts, and nowhere else.
export const BASE = "/revelei/";

// The key the theme choice is stored under; src/content.ts names the same one for the toggle.
const THEME_KEY = "revelei-theme";

// The pre-paint theme script comes from the design system rather than being typed into
// index.html, so the page and the toggle cannot disagree about the rule. Replace-or-throw: a
// template that lost its anchor would ship a flash of the wrong theme.
function themeScript(): Plugin {
  const anchor = "<!--theme-script-->";
  return {
    name: "theme-script",
    transformIndexHtml(html) {
      if (!html.includes(anchor)) throw new Error(`index.html lost its ${anchor} anchor`);
      return html.replace(anchor, () => `<script>${siteThemeScript(THEME_KEY)}</script>`);
    },
  };
}

export default defineConfig({
  base: BASE,
  plugins: [react(), tailwindcss(), themeScript()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
  },
});
