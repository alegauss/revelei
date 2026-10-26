import { SiteButton, SiteFooter, SiteNav, SiteThemeToggle } from "@viglet/viglet-design-system/site";
import { asset, creditsFor, familyHref, familyName, languages, pageHref, releasesUrl, type Lang } from "../content";
import { useLang, useWords } from "../i18n";

// The nav and the footer, drawn by the design system's site layer so Revelei sits in the
// same family as the other product sites. Everything between them is this product's own.

const other = (lang: Lang): Lang => (lang === "en" ? "pt" : "en");

export function Nav() {
  const lang = useLang();
  const w = useWords();
  const target = other(lang);
  return (
    <SiteNav
      brand={{ href: pageHref(lang), name: "Revelei", logo: asset("icon.svg") }}
      // PB49: the family chip, the way back to the Alegauss storefront.
      parent={{ href: familyHref, name: familyName, pre: w.family.pre, title: w.family.title }}
      links={w.nav.links}
      actions={
        <>
          <SiteButton href={releasesUrl} rel="noopener">{w.nav.cta}</SiteButton>
          {/* The other language, named in that language's own words for screen readers. */}
          <SiteButton
            variant="ghost"
            href={pageHref(target)}
            hrefLang={languages[target].htmlLang}
            lang={languages[target].htmlLang}
            aria-label={languages[lang].switchLabel}
            title={languages[lang].switchLabel}
          >
            {languages[lang].switchTo}
          </SiteButton>
          <SiteThemeToggle storageKey="revelei-theme" label={w.nav.themeLabel} />
        </>
      }
    />
  );
}

export function Footer() {
  const lang = useLang();
  const w = useWords();
  const credits = creditsFor(lang);
  return (
    <SiteFooter
      brand={{ href: pageHref(lang), name: "Revelei", logo: asset("icon.svg") }}
      links={w.footer.links}
      disclaimer={
        <>
          {w.footer.disclaimer}{" "}
          <span className="credits">
            {credits.title}:{" "}
            {credits.items.map((c, i) => (
              <span key={c.href}>
                <a href={c.href} rel="noopener">
                  {c.label}
                </a>{" "}
                ({c.by}){i < credits.items.length - 1 ? ", " : ". "}
              </span>
            ))}
            {credits.note}
          </span>
        </>
      }
    />
  );
}
