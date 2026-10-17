import { useState } from 'react';
import { C, FONT, R } from '../../../lib/design';
import type { FeatureRun, FeatureStage, FeatureStageStatus } from '../../../types/featureRun';
import { FeatureSteps } from './FeatureSteps';

// Карточка фичи в режиме «Исполнение фичи»: сводка по агрегату `FeatureRun`.
//
// Состав сверху вниз (по макету v2):
//   1. шапка — имя фичи + ветка;
//   2. алерт состояния — «ждёт вас» (мерж) / «смержена» / «спайк закрыт»;
//   3. «Сейчас» — идущие задачи;
//   4. конвейер — стадии разрез→план→производство→ревью→проверка→финал→мерж,
//      каждая раскрывается (производство — в шаги, ревью/проверка/финал — в витки).
//
// Компонент «глупый»: всю арифметику (прогресс, вердикты, стадии) считает проектор,
// здесь только раскладка. Тесты проверяют именно этот рендер по фикстуре.

// Глифа стадии конвейера: цвет и форма кодируют статус.
function StageGlyph({ status }: { status: FeatureStageStatus }) {
  const common = { width: 16, height: 16, viewBox: '0 0 16 16', 'aria-hidden': true as const };
  switch (status) {
    case 'done':
      return (
        <svg {...common}>
          <circle cx="8" cy="8" r="6.5" fill={C.bgSelected} stroke={C.textMuted} strokeWidth="1" />
          <path d="M5.3 8.1 7.1 9.9l3.6-3.8" fill="none" stroke={C.textMuted} strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      );
    case 'todo':
      return (
        <svg {...common}>
          <circle cx="8" cy="8" r="6" fill="none" stroke={C.dashed} strokeWidth="1.5" strokeDasharray="2.5 2" />
        </svg>
      );
    case 'none':
      return (
        <svg {...common}>
          <circle cx="8" cy="8" r="6" fill="none" stroke={C.dashed} strokeWidth="1.5" />
          <path d="M5 11 11 5" stroke={C.dashed} strokeWidth="1.5" />
        </svg>
      );
    case 'wait':
      return (
        <svg {...common}><path d="M8 1.5 14.5 8 8 14.5 1.5 8Z" fill={C.info} /></svg>
      );
    case 'loop':
      return (
        <svg {...common}>
          <path d="M12.6 9.2A4.8 4.8 0 1 1 11.3 4.4" fill="none" stroke={C.warning} strokeWidth="2.2" strokeLinecap="round" />
          <path d="M9.2 2.6 12.6 3.4 11.8 6.8" fill="none" stroke={C.warning} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      );
    default: // run
      return (
        <svg {...common}>
          <circle cx="8" cy="8" r="6.5" fill="none" stroke={C.textHeading} strokeWidth="1.5" className="feature-pulse" />
          <circle cx="8" cy="8" r="3.5" fill={C.textHeading} />
        </svg>
      );
  }
}

// Алерт состояния: тонированная плашка с глифой и текстом.
function StatusAlert({ tone, children }: { tone: 'wait' | 'done' | 'closed'; children: React.ReactNode }) {
  const bg = tone === 'wait' ? C.infoBg : tone === 'done' ? C.successBg : C.bgSelected;
  const fg = tone === 'wait' ? C.info : tone === 'done' ? C.successText : C.textHeading;
  return (
    <div role="status" style={{ display: 'flex', flexDirection: 'column', gap: 8, background: bg, color: fg, borderRadius: R.lg, padding: '8px 10px', fontSize: 12.5 }}>
      {children}
    </div>
  );
}

// Одна стадия конвейера: глифа + название + мета; по клику раскрывает детали.
function StageRow({ stage, onToggle, expanded }: {
  stage: FeatureStage;
  expanded: boolean;
  onToggle: () => void;
}) {
  const expandable = stage.status !== 'todo' && stage.status !== 'none';
  return (
    <li style={{ position: 'relative' }}>
      <button
        onClick={expandable ? onToggle : undefined}
        aria-expanded={expanded}
        disabled={!expandable}
        style={{
          width: '100%', display: 'grid', gridTemplateColumns: '16px 1fr auto', gap: 9, alignItems: 'start',
          textAlign: 'left', border: 'none', background: 'transparent', cursor: expandable ? 'pointer' : 'default',
          padding: '5px 4px 5px 0', borderRadius: R.md, minHeight: 32, position: 'relative',
        }}
      >
        <span style={{ display: 'flex', justifyContent: 'center', paddingTop: 1, background: C.bgWhite, borderRadius: '50%', position: 'relative', zIndex: 1 }}>
          <StageGlyph status={stage.status} />
        </span>
        <span style={{ minWidth: 0 }}>
          <span style={{ fontSize: 13, color: C.textPrimary, display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
            <b style={{ fontWeight: 600, color: C.textHeading }}>{stage.name}</b>
          </span>
          <span style={{ fontSize: 12, color: C.textSecondary, marginTop: 1, display: 'block' }}>
            {stage.meta ?? (stage.status === 'todo' ? 'ещё не начат' : '')}
          </span>
        </span>
        {expandable && (
          <span style={{ display: 'flex', paddingTop: 1, color: C.textMuted }}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"
              style={{ transform: expanded ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }}>
              <path d="m6 9 6 6 6-6" />
            </svg>
          </span>
        )}
      </button>
      {expanded && <StageDetails stage={stage} />}
    </li>
  );
}

// Детали стадии: производство — шаги, ревью/проверка/финал — витки, мерж — текст.
function StageDetails({ stage }: { stage: FeatureStage }) {
  const box: React.CSSProperties = {
    margin: '2px 0 8px 25px', padding: '8px 10px', background: C.bgCard,
    border: `1px solid ${C.borderLight}`, borderRadius: R.lg, display: 'flex', flexDirection: 'column', gap: 8,
  };
  const head = (t: string) => (
    <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted }}>{t}</div>
  );
  if (stage.key === 'prod') {
    // Шаги приходят из родителя через props — здесь заглушка: данные в run.steps.
    return <div style={box}>{head('Задачи этапа')}<span style={{ fontSize: 12, color: C.textSecondary }}>шаги — см. «Сейчас» и список ниже</span></div>;
  }
  if (stage.rounds && stage.rounds.length) {
    return (
      <div style={box}>
        {head(stage.key === 'final' ? 'Финальное ревью' : stage.key === 'check' ? 'Проходы проверки' : 'Круги ревью')}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          {stage.rounds.map(r => (
            <div key={r.n} style={{ display: 'flex', gap: 7, alignItems: 'flex-start', fontSize: 12, color: C.textPrimary }}>
              <span style={{ color: C.textSecondary }}>{r.who ?? '—'}</span>
              <span style={{ color: C.textSecondary }}>· {r.reason ?? roundVerdictText(r.verdict)}</span>
            </div>
          ))}
        </div>
      </div>
    );
  }
  return (
    <div style={box}>
      {head('Задачи этапа')}
      <span style={{ fontSize: 12, color: C.textSecondary }}>в трекере нет</span>
    </div>
  );
}

function roundVerdictText(v: string): string {
  switch (v) {
    case 'returned': return 'вернул';
    case 'accepted_with_remarks': return 'принял с правками';
    case 'accepted': return 'принял';
    case 'withdrawn': return 'снят';
    default: return 'идёт';
  }
}

// Конвейер: список стадий с раскрывающимися деталями.
function Conveyor({ run }: { run: FeatureRun }) {
  const [open, setOpen] = useState<FeatureStage['key'] | null>(null);
  return (
    <ol style={{ listStyle: 'none', margin: 0, padding: 0, position: 'relative' }}>
      {run.stages.map(stage => (
        <StageRow
          key={stage.key}
          stage={stage}
          expanded={open === stage.key}
          onToggle={() => setOpen(open === stage.key ? null : stage.key)}
        />
      ))}
    </ol>
  );
}

// Главная карточка фичи.
export function FeatureRunView({ run }: { run: FeatureRun }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      {/* Шапка: имя + ветка + прогресс «N из M шагов» */}
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
        <span style={{ fontSize: 13, fontWeight: 600, color: C.textHeading }}>{run.name}</span>
        {run.branch && (
          <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted }}>{run.branch}</span>
        )}
        {run.progress.total > 0 && (
          <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, whiteSpace: 'nowrap' }}>
            {run.progress.done} из {run.progress.total} {plural(run.progress.total, 'шаг', 'шага', 'шагов')}
          </span>
        )}
      </div>

      {/* Алерты состояния */}
      {run.state === 'wait' && (
        <StatusAlert tone="wait">
          <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
            <span style={{ marginTop: 2 }}>
              <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 1.5 14.5 8 8 14.5 1.5 8Z" fill={C.info} /></svg>
            </span>
            <span><b>Ждёт вас.</b> Все шаги приняты, мержа ещё нет. Отдать фичу на мерж?</span>
          </div>
        </StatusAlert>
      )}
      {run.state === 'done' && run.merged && (
        <div style={{ display: 'flex', gap: 10, alignItems: 'center', background: C.successBg, borderRadius: R.lg, padding: '9px 11px' }}>
          <svg width="18" height="18" viewBox="0 0 16 16" aria-hidden="true">
            <circle cx="8" cy="8" r="7" fill={C.success} />
            <path d="M4.8 8.2 7 10.3l4.3-4.6" fill="none" stroke={C.onAccent} strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          <div>
            <div style={{ fontSize: 13, fontWeight: 700, color: C.successText }}>Фича смержена</div>
            <div style={{ fontSize: 12, color: C.textSecondary, fontFamily: FONT.mono }}>
              {run.progress.done} из {run.progress.total} {plural(run.progress.total, 'шаг', 'шага', 'шагов')} · ↺{run.ret}
            </div>
          </div>
        </div>
      )}

      {/* «Сейчас»: идущие задачи */}
      {run.running.length > 0 && (
        <>
          <SectionLabel>Сейчас</SectionLabel>
          {run.running.map((r, i) => (
            <div key={i} style={{ display: 'flex', gap: 9, alignItems: 'flex-start', background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, padding: '8px 10px' }}>
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ fontSize: 13, fontWeight: 600, color: C.textHeading }}>{r.who}{r.stepId ? ` · ${r.stepId}` : ''}</div>
                <div style={{ fontSize: 12, color: C.textSecondary, marginTop: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{r.title}</div>
              </div>
            </div>
          ))}
        </>
      )}

      {/* Конвейер */}
      <SectionLabel>Конвейер</SectionLabel>
      <Conveyor run={run} />

      {/* Шаги производства (по линиям) */}
      <SectionLabel>Шаги</SectionLabel>
      <FeatureSteps steps={run.steps} lanes={run.lanes} />
    </div>
  );
}

function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{ fontSize: 10, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted, margin: '2px 0 -4px' }}>
      {children}
    </div>
  );
}

function plural(n: number, one: string, few: string, many: string): string {
  const n10 = n % 10, n100 = n % 100;
  if (n10 === 1 && n100 !== 11) return one;
  if (n10 >= 2 && n10 <= 4 && (n100 < 10 || n100 >= 20)) return few;
  return many;
}
