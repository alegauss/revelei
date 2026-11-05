import { StrictMode } from "react";
import { createRoot, hydrateRoot } from "react-dom/client";
import { App } from "./App";
import { ConsentBanner } from "./components/Consent";
import { BASE } from "./content";
import { langFor } from "./routes";
import "./index.css";

const root = document.getElementById("root");
if (!root) {
  throw new Error("#root is missing from index.html");
}

// The prerender wrote this language's markup; the client hydrates it in place, so it has to
// read the same language off the same path.
const path = `/${window.location.pathname.slice(BASE.length)}`;
const lang = langFor(path);
hydrateRoot(
  root,
  <StrictMode>
    <App lang={lang} />
  </StrictMode>,
);

// The consent banner is not in the prerendered page at all, so it mounts rather than hydrates.
const consent = document.createElement("div");
document.body.appendChild(consent);
createRoot(consent).render(<ConsentBanner lang={lang} />);
