// Витрина — два варианта «спокойной» живой карточки инструмента с этапами прогона.
//
// Это МАКЕТ для выбора, а не боевой код: шапка карточки повторена упрощённо (иконка, имя,
// аргумент, время), боевые ToolUseView и ProgressBar не тронуты. Определённая полоса
// (сплошная/пунктир) — настоящий ProgressBar; тихая бегущая полоса и «дышащая» точка —
// локальные заготовки, которые после выбора уедут в примитивы (см. описания вариантов).
//
// Вариант A «Короткая полоса у таймера»: полоса не на всю ширину, а короткая, в шапке рядом
//   с «идёт M:SS»; бегущий отрезок медленнее и бледнее. Этапы — строкой под шапкой.
// Вариант B «Этапы вместо полосы»: пока сколько осталось неизвестно — полосы нет вовсе,
//   живость показывает медленно мигающая точка слева. Полоса появляется только с процентом,
//   короткая, под строкой этапов. Этапы — с галочками.

import { useEffect, useState, type CSSProperties, type ReactNode } from 'react';
import { Terminal, Plug } from 'lucide-react';
import { C, FONT, FS, SP, R } from '../lib/design';
import { ProgressBar } from '../components/ui';
import { PROGRESS_H } from '../components/ui/ProgressBar';
import { formatClock } from '../lib/toolTiming';
import { useIsMobile } from '../lib/breakpoints';

// Тихий бег и дыхание точки. Период длиннее боевого (1,4 с), отрезок бледнее —
// движение заметно краем глаза, но не тянет взгляд
const CALM_CSS = `
@keyframes kit-calm-run { from { transform: translateX(-100%); } to { transform: translateX(260%); } }
.kit-calm-run { animation: kit-calm-run 2.8s ease-in-out infinite; }
@keyframes kit-breathe { 0%, 100% { opacity: .3; } 50% { opacity: 1; } }
.kit-breathe { animation: kit-breathe 2.4s ease-in-out infinite; }
@media (prefers-reduced-motion: reduce) {
  .kit-calm-run { animation: none; width: 100% !important; opacity: .35; }
  .kit-breathe { animation: none; opacity: .7; }
}`;

// Секундные часы всей витрины — один интервал на все карточки
function useNow(): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);
  return now;
}

type Stage = { name: string; ms: number };
type Demo = {
  label: string;
  bash?: boolean;
  title: string;
  arg: string;
  startedAgo: number;                 // с — когда начался вызов
  done?: Stage[];                     // пройденные этапы с длительностью
  current?: { name: string; ago: number }; // текущий этап и сколько с его начала
  caption?: string;                   // подпись прогресса («412 из 7951 · упало 2»)
  percent?: number;
  exact?: boolean;
  // Итог закрытой карточки. summary — последние счётчики прогона («174 из 177 · упало 3»):
  // остаются на карточке без раскрытия, после F5 — из истории
  final?: { kind: 'done' | 'aborted'; ms: number; summary?: string };
};

const DEMOS: Demo[] = [
  { label: 'обычная команда (Bash), идёт', bash: true, title: 'Bash', arg: 'dotnet build backend/ClaudeHomeServer.slnx', startedAgo: 42 },
  { label: 'тесты — сборка', title: 'Тесты · dotnet', arg: 'backend/ClaudeHomeServer.Tests', startedAgo: 21, done: [{ name: 'очередь', ms: 12_000 }], current: { name: 'сборка', ago: 9 } },
  { label: 'тесты — прогон с процентом', title: 'Тесты · dotnet', arg: 'backend/ClaudeHomeServer.Tests', startedAgo: 235, done: [{ name: 'сборка', ms: 102_000 }, { name: 'подсчёт', ms: 3_000 }], current: { name: 'тесты', ago: 130 }, caption: '412 из 7951 · упало 2', percent: 5, exact: true },
  { label: 'тесты vitest — оценка (пунктир)', title: 'Тесты · vitest', arg: 'frontend', startedAgo: 40, done: [{ name: 'подсчёт файлов', ms: 4_000 }], current: { name: 'тесты', ago: 36 }, caption: '≈40%', percent: 40 },
  { label: 'готово с упавшими — счётчики и этапы остаются без раскрытия', title: 'Тесты · dotnet', arg: 'backend/ClaudeHomeServer.Tests', startedAgo: 0, done: [{ name: 'сборка', ms: 62_000 }, { name: 'подсчёт', ms: 2_000 }, { name: 'тесты', ms: 71_000 }], final: { kind: 'done', ms: 135_000, summary: '174 из 177 · упало 3' } },
  { label: 'готово, всё прошло', title: 'Тесты · dotnet', arg: 'backend/ClaudeHomeServer.Tests', startedAgo: 0, done: [{ name: 'сборка', ms: 102_000 }, { name: 'подсчёт', ms: 3_000 }, { name: 'тесты', ms: 130_000 }], final: { kind: 'done', ms: 235_000, summary: '7951 из 7951' } },
  { label: 'прервано на сборке', title: 'Тесты · dotnet', arg: 'backend/ClaudeHomeServer.Tests', startedAgo: 0, done: [{ name: 'очередь', ms: 12_000 }, { name: 'сборка', ms: 65_000 }], final: { kind: 'aborted', ms: 77_000 } },
];

// Шапка карточки — как в ToolUseView: место под спиннер, иконка с именем, аргумент, справа время
function Head({ demo, lead, right }: { demo: Demo; lead?: ReactNode; right: ReactNode }) {
  const color = demo.bash ? C.success : C.plan;
  return (
    <div style={{ padding: '3px 0', display: 'flex', alignItems: 'center', gap: 10 }}>
      <span style={{ width: 18, flexShrink: 0, display: 'flex', justifyContent: 'center' }}>{lead}</span>
      <span style={{ display: 'flex', alignItems: 'center', gap: 5, flexShrink: 0, color }}>
        {demo.bash ? <Terminal size={13} strokeWidth={2} /> : <Plug size={13} strokeWidth={2} />}
        <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>{demo.title}</span>
      </span>
      <span style={{ flex: 1, minWidth: 0, fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{demo.arg}</span>
      {right}
    </div>
  );
}

const meta: CSSProperties = { fontSize: FS.xs, color: C.textMuted, flexShrink: 0, fontVariantNumeric: 'tabular-nums' };

// Итог / «идёт» справа в шапке. Счётчики итога на десктопе — тут же, «готово · 2:15 · 174 из
// 177 · упало 3»; на телефоне шапке их не вместить — они встают первыми в строку этапов
function Clock({ demo, now, t0 }: { demo: Demo; now: number; t0: number }) {
  const isMobile = useIsMobile();
  if (demo.final) {
    const aborted = demo.final.kind === 'aborted';
    const summary = !isMobile ? demo.final.summary : undefined;
    return (
      <span style={{ ...meta, color: aborted ? C.dangerText : C.textMuted }}>
        {aborted ? 'прервано' : 'готово'} · {formatClock(demo.final.ms)}
        {summary && <span style={{ color: C.textMuted }}> · <Caption text={summary} /></span>}
      </span>
    );
  }
  return <span style={meta}>идёт {formatClock(now - t0 + demo.startedAgo * 1000)}</span>;
}

// Тихая бегущая полоса: бледный отрезок, медленный ход. Это предлагаемый `calm` у ProgressBar
function CalmRun({ width }: { width: number | string }) {
  return (
    <span role="progressbar" aria-busy="true" aria-label="Выполняется" style={{ display: 'block', width, height: PROGRESS_H.thin, borderRadius: R.max, background: C.progressTrack, overflow: 'hidden' }}>
      <span className="kit-calm-run" style={{ display: 'block', height: '100%', width: '35%', borderRadius: R.max, background: C.accent, opacity: 0.45 }} />
    </span>
  );
}

// «упало K» — красным, как в боевой подписи
function Caption({ text }: { text: string }) {
  return <>{text.split(/(упало [1-9]\d*)/).map((p, i) => i % 2 ? <span key={i} style={{ color: C.dangerText }}>{p}</span> : p)}</>;
}

// Строка этапов: пройденные прижимаются и режутся многоточием справа, текущий этап (и подпись
// прогресса при нём) не режется никогда — даже на 320 px видно, что идёт сейчас
function StageLine({ demo, now, t0, marks }: { demo: Demo; now: number; t0: number; marks: boolean }) {
  const aborted = demo.final?.kind === 'aborted';
  const past = demo.done ?? [];
  // У прерванной карточки последний этап — тот, на котором оборвалось
  const shownPast = aborted ? past.slice(0, -1) : past;
  const cut = aborted ? past[past.length - 1] : null;
  const pastText = shownPast.map(s => `${marks ? '✓ ' : ''}${s.name} ${formatClock(s.ms)}`).join(' · ');
  const cur = demo.current;
  // Телефон: счётчики итога — в начале строки и не режутся (в шапке им нет места)
  const summary = useIsMobile() ? demo.final?.summary : undefined;
  return (
    <div style={{ display: 'flex', minWidth: 0, height: 16, lineHeight: '16px', fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>
      {summary && <span style={{ flexShrink: 0, color: C.textSecondary }}><Caption text={summary} />{pastText ? ' · ' : ''}</span>}
      {pastText &&<span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>{pastText}</span>}
      {cur && (
        <span style={{ flexShrink: 0 }}>
          {pastText ? ' · ' : ''}
          <span style={{ color: C.textSecondary, fontWeight: 600 }}>{cur.name} {formatClock(now - t0 + cur.ago * 1000)}</span>
          {demo.caption && <> · <Caption text={demo.caption} /></>}
        </span>
      )}
      {cut && (
        <span style={{ flexShrink: 0, color: C.dangerText }}>
          {pastText ? ' · ' : ''}{marks ? '✕ ' : ''}{cut.name} {formatClock(cut.ms)}
        </span>
      )}
    </div>
  );
}

// Вариант A: короткая тихая полоса в шапке у таймера, этапы — строкой под шапкой
function CardA({ demo, now, t0 }: { demo: Demo; now: number; t0: number }) {
  const running = !demo.final;
  const bar = !running ? null
    : demo.percent != null
      ? <ProgressBar value={demo.percent} estimate={!demo.exact} size="thin" transition="width .5s linear" style={{ width: 48, flexShrink: 0 }} />
      : <span style={{ flexShrink: 0 }}><CalmRun width={48} /></span>;
  const hasStages = !!(demo.done?.length || demo.current);
  return (
    <div>
      <Head demo={demo} right={<>{bar}<Clock demo={demo} now={now} t0={t0} /></>} />
      {hasStages && <div style={{ paddingLeft: 28, paddingBottom: SP.xxs }}><StageLine demo={demo} now={now} t0={t0} marks={false} /></div>}
    </div>
  );
}

// Вариант B: полосы нет, пока нечего мерить; живость — мигающая точка слева.
// С процентом — короткая полоса под строкой этапов
function CardB({ demo, now, t0 }: { demo: Demo; now: number; t0: number }) {
  const running = !demo.final;
  const dot = running
    ? <span className="kit-breathe" style={{ width: 6, height: 6, borderRadius: R.max, background: C.accent, display: 'block' }} />
    : null;
  const hasStages = !!(demo.done?.length || demo.current);
  return (
    <div>
      <Head demo={demo} lead={dot} right={<Clock demo={demo} now={now} t0={t0} />} />
      {hasStages && (
        <div style={{ paddingLeft: 28, paddingBottom: SP.xxs }}>
          <StageLine demo={demo} now={now} t0={t0} marks />
          {running && demo.percent != null && (
            <ProgressBar value={demo.percent} estimate={!demo.exact} size="thin" transition="width .5s linear" style={{ maxWidth: 200, marginTop: SP.xxs }} />
          )}
        </div>
      )}
    </div>
  );
}

function Variant({ id, title, note, Card, now, t0 }: {
  id: string; title: string; note: string;
  Card: (p: { demo: Demo; now: number; t0: number }) => ReactNode;
  now: number; t0: number;
}) {
  return (
    <div data-kit={`progress-calm-${id}`} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, paddingBottom: SP.sm }}>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.sm }}>{title}</div>
      <div style={{ fontSize: FS.xs, color: C.textMuted }}>{note}</div>
      {DEMOS.map(d => (
        <div key={d.label} style={{ borderTop: `1px solid ${C.bgInset}`, paddingTop: SP.xs }}>
          <div style={{ fontSize: FS.xs, color: C.textMuted, opacity: 0.8 }}>{d.label}</div>
          <Card demo={d} now={now} t0={t0} />
        </div>
      ))}
    </div>
  );
}

export function ToolProgressCalmVariants() {
  const [t0] = useState(() => Date.now());
  const now = useNow();
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <style>{CALM_CSS}</style>
      <Variant id="a" title="Вариант A — короткая полоса у таймера"
        note="Полоса маленькая и бледная, стоит рядом с «идёт». Этапы — строкой под шапкой, текущий выделен."
        Card={CardA} now={now} t0={t0} />
      <Variant id="b" title="Вариант B — этапы вместо полосы"
        note="Пока неизвестно, сколько осталось, полосы нет — мигает точка. Полоса появляется только с процентом."
        Card={CardB} now={now} t0={t0} />
    </div>
  );
}
