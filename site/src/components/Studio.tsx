import { useState, type KeyboardEvent } from "react";
import { asset, operationsFor, type Backdrop } from "../content";
import { useLang, useWords } from "../i18n";
import { Compare } from "./Compare";

// The studio: the page's stand-in for the app window. A dark stage, as photo tools are, so the
// pictures and not the chrome carry the colour. The operations are tabs down the side, the way
// the app's operations panel lists them; each opens its own real before and after.

const KEY_STEP: Record<string, number> = { ArrowDown: 1, ArrowRight: 1, ArrowUp: -1, ArrowLeft: -1 };

export function Studio() {
  const lang = useLang();
  const studio = useWords().studio;
  const operations = operationsFor(lang);
  const [active, setActive] = useState(0);
  const [backdrop, setBackdrop] = useState<Backdrop>("checker");
  const op = operations[active];

  // Arrow keys move between tabs, as a tab list should.
  const onKey = (e: KeyboardEvent<HTMLDivElement>) => {
    const step = KEY_STEP[e.key];
    if (!step) return;
    e.preventDefault();
    const next = (active + step + operations.length) % operations.length;
    setActive(next);
    document.getElementById(`tab-${operations[next].id}`)?.focus();
  };

  return (
    <section id="try" className="studio">
      <div className="wrap">
        <div className="sec-head reveal">
          <p className="eyebrow">{studio.eyebrow}</p>
          <h2>{studio.title}</h2>
          <p>{studio.lead}</p>
        </div>

        <div className="studio-window reveal">
          <div className="studio-bar" aria-hidden="true">
            <span />
            <span />
            <span />
            <b>Revelei</b>
          </div>
          <div className="studio-body">
            <div className="studio-tabs" role="tablist" aria-orientation="vertical" aria-label={studio.tabsLabel} onKeyDown={onKey}>
              {operations.map((o, i) => (
                <button
                  key={o.id}
                  id={`tab-${o.id}`}
                  role="tab"
                  type="button"
                  aria-selected={i === active}
                  aria-controls={`panel-${o.id}`}
                  tabIndex={i === active ? 0 : -1}
                  className="studio-tab"
                  onClick={() => setActive(i)}
                >
                  <span className="studio-tab-ico" aria-hidden="true">
                    {o.icon}
                  </span>
                  <span>
                    <b>{o.name}</b>
                    <small>{o.verb}</small>
                  </span>
                </button>
              ))}
            </div>

            <div className="studio-panel" id={`panel-${op.id}`} role="tabpanel" aria-labelledby={`tab-${op.id}`}>
              <div className={`studio-canvas${op.width < 400 ? " studio-canvas-small" : ""}`}>
                <Compare
                  key={op.id}
                  before={op.before}
                  after={op.after}
                  beforeLabel={op.beforeLabel}
                  afterLabel={op.afterLabel}
                  width={op.width}
                  height={op.height}
                  label={studio.compareLabel(op.beforeLabel, op.afterLabel)}
                  backdrop={op.backdrops ? backdrop : undefined}
                />
              </div>
              <div className="studio-info">
                <h3>{op.title}</h3>
                <p>{op.body}</p>
                {op.backdrops && (
                  <div className="studio-backdrops" role="radiogroup" aria-label={studio.backdropLabel}>
                    <span>{studio.backdropLabel}</span>
                    {studio.backdrops.map((b) => (
                      <button
                        key={b.id}
                        type="button"
                        role="radio"
                        aria-checked={backdrop === b.id}
                        className={`swatch swatch-${b.id}`}
                        title={b.label}
                        aria-label={b.label}
                        onClick={() => setBackdrop(b.id)}
                      />
                    ))}
                  </div>
                )}
                <dl className="studio-facts">
                  <div>
                    <dt>{studio.modelLabel}</dt>
                    <dd>{op.model}</dd>
                  </div>
                  <div>
                    <dt>{studio.sizeLabel}</dt>
                    <dd>{op.size}</dd>
                  </div>
                  <div>
                    <dt>{studio.timeLabel}</dt>
                    <dd>{op.timeText}</dd>
                  </div>
                </dl>
                <p className="studio-note">{op.note}</p>
              </div>
            </div>
          </div>
        </div>
        {/* The tiny original the hero's 4x result came from, at its own size: the reader sees
            how little the app had to work with. */}
        <p className="studio-origin reveal">
          <img src={asset("demo/upscale-input.webp")} alt={studio.originAlt} width={375} height={334} loading="lazy" />
          <span>
            {studio.origin[0]}
            <b>{studio.origin[1]}</b>.
          </span>
        </p>
      </div>
    </section>
  );
}
