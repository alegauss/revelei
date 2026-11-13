import { BASE, languages, releasesUrl, repoUrl, words, type Lang } from "./content";

// One page per language. The table is what the design system's prerender reads, and what
// scripts/prerender.mjs reads to set each page's <html lang> and its hreflang links.
// The Alegauss user site, which GitHub serves this project under since it left Japode; the
// old japode.com/revelei/ answers 404, so a canonical there sends search engines nowhere.
export const SITE_ORIGIN = "https://alegauss.com";

// Absolute, because a platform fetching the social card is not on this origin.
export const OG_IMAGE = `${SITE_ORIGIN}${BASE}og.png`;

const order: Lang[] = ["en", "pt"];

export const ROUTES = order.map((lang) => ({
  path: languages[lang].path,
  lang,
  htmlLang: languages[lang].htmlLang,
  title: words[lang].meta.title,
  description: words[lang].meta.description,
}));

/** The language a route path is in; English for anything else. */
export function langFor(path: string): Lang {
  return path.replace(/\/+$/, "") === "/pt" ? "pt" : "en";
}

/** The canonical and og:url for a route, carrying the base. */
export function canonicalUrl(path: string): string {
  const rel = path === "/" ? "" : `${path.replace(/^\//, "")}/`;
  return `${SITE_ORIGIN}${BASE}${rel}`;
}

/** The page's schema.org description of the app, in the route's language. The author is the
 *  Person the Alegauss hub declares, by its @id, so search engines tie the two together; the
 *  hub's own product page describes Revelei with the same fields. */
export function jsonLd(path: string) {
  const route = ROUTES.find((r) => r.path === path) ?? ROUTES[0];
  return {
    "@context": "https://schema.org",
    "@type": "SoftwareApplication",
    name: "Revelei",
    description: route.description,
    url: canonicalUrl(route.path),
    inLanguage: route.htmlLang,
    image: OG_IMAGE,
    applicationCategory: "MultimediaApplication",
    operatingSystem: "Windows 10, Windows 11",
    license: "https://spdx.org/licenses/Apache-2.0.html",
    isAccessibleForFree: true,
    offers: { "@type": "Offer", price: "0", priceCurrency: "USD" },
    downloadUrl: releasesUrl,
    sameAs: [repoUrl],
    author: { "@type": "Person", "@id": `${SITE_ORIGIN}/#person`, name: "Alexandre Oliveira", url: "https://github.com/alegauss" },
  };
}

/** Where a route's HTML is written, relative to dist/: "" for the root. */
export function outputDir(path: string): string {
  return path === "/" ? "" : path.replace(/^\//, "");
}
