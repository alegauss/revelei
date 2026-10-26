import type { ReactNode } from "react";
import { asset } from "../content";
import { useWords } from "../i18n";

// The path a photo takes through the app, as four small scenes of the window rather than four
// paragraphs: a reader who has never opened Revelei should finish this section knowing where
// to drop a file and where the result appears. Each scene loops gently and stops for readers
// who ask for reduced motion (index.css).

function Scene({ children }: Readonly<{ children: ReactNode }>) {
  return (
    <div className="scene" aria-hidden="true">
      <div className="scene-bar">
        <span />
        <span />
        <span />
      </div>
      <div className="scene-body">{children}</div>
    </div>
  );
}

export function Journey() {
  const journey = useWords().journey;
  const s = journey.scene;
  const scenes: ReactNode[] = [
    // 1. A photo card falls into the drop zone.
    <Scene key="drop">
      <div className="scene-drop">
        <span className="scene-drop-label">{s.drop}</span>
        <img className="scene-card" src={asset("demo/background-before.webp")} alt="" width={1103} height={755} loading="lazy" />
      </div>
    </Scene>,
    // 2. The operations panel, one chosen, its progress running.
    <Scene key="choose">
      <div className="scene-ops">
        {s.ops.map((name, i) => (
          <span key={name} className={i === 1 ? "on" : undefined}>
            {name}
          </span>
        ))}
        <div className="scene-progress">
          <i />
        </div>
        <small>{s.progress}</small>
      </div>
    </Scene>,
    // 3. The divider sweeping over a real result.
    <Scene key="compare">
      <div className="scene-compare">
        <img src={asset("demo/denoise-before.webp")} alt="" width={960} height={720} loading="lazy" />
        <img className="scene-compare-after" src={asset("demo/denoise-after.webp")} alt="" width={960} height={720} loading="lazy" />
        <i className="scene-compare-line" />
      </div>
    </Scene>,
    // 4. The export card: a format chosen, the file saved.
    <Scene key="export">
      <div className="scene-export">
        <div className="scene-formats">
          <span className="on">PNG</span>
          <span>JPEG</span>
          <span>WebP</span>
        </div>
        <div className="scene-file">
          <b>{s.file}</b>
          <small>{s.saved}</small>
        </div>
      </div>
    </Scene>,
  ];

  return (
    <section id="how" className="journey">
      <div className="wrap">
        <div className="sec-head reveal">
          <p className="eyebrow">{journey.eyebrow}</p>
          <h2>{journey.title}</h2>
        </div>
        <ol className="journey-steps">
          {journey.steps.map((step, i) => (
            <li key={step.title} className="journey-step reveal" style={{ transitionDelay: `${i * 80}ms` }}>
              {scenes[i]}
              <div className="journey-text">
                <span className="journey-n">{i + 1}</span>
                <h3>{step.title}</h3>
                <p>{step.body}</p>
              </div>
            </li>
          ))}
        </ol>
      </div>
    </section>
  );
}
